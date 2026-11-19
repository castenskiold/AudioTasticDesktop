using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>A file's tags were written (<see cref="Tags"/> as read afterwards).</summary>
public sealed class TagWrittenEventArgs(string path, TrackTags? tags) : EventArgs
{
    public string Path { get; } = path;
    public TrackTags? Tags { get; } = tags;
}

/// <summary>A tag write failed; the file is unchanged.</summary>
public sealed class TagWriteFailedEventArgs(string path, string message) : EventArgs
{
    public string Path { get; } = path;
    public string Message { get; } = message;
}

/// <summary>What happened to a batch of tag writes.</summary>
public sealed record TagWriteOutcome(int Written, int Deferred, IReadOnlyList<(string Path, string Message)> Failures);

/// <summary>
/// MD-01: writes tag edits off the UI thread, but never to a file the player is decoding (the current, preloaded or
/// fading track): those edits wait (later edits to the same file are merged in) and are written by
/// <see cref="Retry"/>, which the app calls when the playing track changes or playback stops. A file another reader
/// still has open (a deck being unloaded, the art or analysis reading it) is tried again for a few seconds; if it is
/// still busy, its edit waits like the others instead of failing.
/// </summary>
public sealed class TagWriteService(TagWriter writer, Func<string, bool> inUse, ILogger<TagWriteService> logger, Func<TimeSpan, Task>? delay = null)
{
    /// <summary>Waits between attempts at a busy file (about 3 s in all).</summary>
    private static readonly TimeSpan[] BusyRetries = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)];

    private readonly Func<TimeSpan, Task> _delay = delay ?? (t => Task.Delay(t));

    private enum Result
    {
        Written,
        Busy,
        Failed,
    }

    private readonly Dictionary<string, TagEdit> _pending = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Raised on a worker thread for every file written.</summary>
    public event EventHandler<TagWrittenEventArgs>? Written;

    /// <summary>Raised on a worker thread for every write that failed (including deferred ones written later).</summary>
    public event EventHandler<TagWriteFailedEventArgs>? Failed;

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public bool IsPending(string path)
    {
        lock (_lock)
        {
            return _pending.ContainsKey(path);
        }
    }

    /// <summary>Applies <paramref name="edit"/> to every file: now, or later for files in use.</summary>
    public Task<TagWriteOutcome> WriteAsync(IReadOnlyList<string> paths, TagEdit edit)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(edit);
        return Task.Run(() =>
        {
            int written = 0, deferred = 0;
            var failures = new List<(string, string)>();
            foreach (var path in paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                bool wait;
                lock (_lock)
                {
                    // A file with an edit already waiting keeps waiting, so the edits are applied in order.
                    wait = inUse(path) || _pending.ContainsKey(path);
                    if (wait)
                    {
                        _pending[path] = _pending.TryGetValue(path, out var earlier) ? Merge(earlier, edit) : edit;
                    }
                }
                if (wait)
                {
                    deferred++;
                    logger.LogInformation("Tag edit for {Path} waits until the player releases the file", path);
                }
                else
                {
                    var (result, message) = TryWrite(path, edit);
                    switch (result)
                    {
                        case Result.Written: written++; break;
                        case Result.Busy: deferred++; break;
                        default: failures.Add((path, message!)); break;
                    }
                }
            }
            return new TagWriteOutcome(written, deferred, failures);
        });
    }

    /// <summary>Writes the waiting edits of files no longer in use. Returns how many were written.</summary>
    public Task<int> Retry() => Task.Run(() =>
    {
        List<(string Path, TagEdit Edit)> due;
        lock (_lock)
        {
            due = [.. _pending.Where(p => !inUse(p.Key)).Select(p => (p.Key, p.Value))];
            foreach (var (path, _) in due)
            {
                _pending.Remove(path);
            }
        }
        return due.Count(d => TryWrite(d.Path, d.Edit).Result == Result.Written);
    });

    /// <summary>Writes, trying a busy file again for a while; a file still busy goes back to waiting.</summary>
    private (Result Result, string? Message) TryWrite(string path, TagEdit edit)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var tags = writer.Write(path, edit);
                Written?.Invoke(this, new TagWrittenEventArgs(path, tags));
                return (Result.Written, null);
            }
            catch (TagWriteException ex) when (IsBusy(ex) && attempt < BusyRetries.Length)
            {
                _delay(BusyRetries[attempt]).GetAwaiter().GetResult(); // a worker thread; never the UI thread
            }
            catch (TagWriteException ex) when (IsBusy(ex))
            {
                lock (_lock)
                {
                    // A newer edit that arrived meanwhile goes on top of this one.
                    _pending[path] = _pending.TryGetValue(path, out var newer) ? Merge(edit, newer) : edit;
                }
                logger.LogInformation("{Path} is in use; its tag edit is written when the file is free", path);
                return (Result.Busy, null);
            }
            catch (TagWriteException ex)
            {
                logger.LogWarning("{Message}", ex.Message);
                Failed?.Invoke(this, new TagWriteFailedEventArgs(path, ex.Message));
                return (Result.Failed, ex.Message);
            }
        }
    }

    /// <summary>A sharing or lock violation: another reader has the file open (Windows); not a missing or read-only file.</summary>
    private static bool IsBusy(TagWriteException ex) =>
        ex.InnerException is IOException io and not FileNotFoundException and not DirectoryNotFoundException && (io.HResult & 0xFFFF) is 32 or 33;

    /// <summary>A later edit on top of an earlier one: its fields and cover win.</summary>
    internal static TagEdit Merge(TagEdit earlier, TagEdit later)
    {
        var fields = new Dictionary<TagField, string?>(earlier.Fields);
        foreach (var (field, value) in later.Fields)
        {
            fields[field] = value;
        }
        return new TagEdit(fields, later.Cover ?? earlier.Cover);
    }
}
