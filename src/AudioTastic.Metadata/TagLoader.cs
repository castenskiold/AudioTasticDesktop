using System.Collections.Concurrent;
using AudioTastic.Core.Playlists;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>
/// MD-01 / PL-07: fills in playlist entries' tags and durations on a background thread, never on the UI thread.
/// Entries the user can see are read first (<see cref="RequestVisible"/>); then whole playlists are probed so the
/// total duration becomes exact (<see cref="ProbePlaylist"/>). Results are reported in batches through
/// <see cref="Updated"/> (raised on the worker thread), at most every 250 ms.
/// </summary>
public sealed class TagLoader : IDisposable
{
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly TagService _tags;
    private readonly ILogger<TagLoader> _logger;
    private readonly ConcurrentStack<(Playlist Playlist, PlaylistEntry Entry)> _visible = new();
    private readonly ConcurrentQueue<(Playlist Playlist, PlaylistEntry Entry)> _background = new();
    private readonly ConcurrentDictionary<PlaylistEntry, byte> _queued = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Playlist> _changed = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _lastReport;

    public TagLoader(TagService tags, ILogger<TagLoader> logger)
    {
        _tags = tags;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioTastic tag loader", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Playlists whose entries got new tags (a batch). Raised on the loader thread.</summary>
    public event EventHandler<IReadOnlyCollection<Playlist>>? Updated;

    /// <summary>Entries on screen: read next (most recent request first).</summary>
    public void RequestVisible(Playlist playlist, IEnumerable<PlaylistEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (!entry.TagsLoaded)
            {
                _visible.Push((playlist, entry));
            }
        }
        _wake.Release();
    }

    /// <summary>Every entry of the playlist, in the background (durations for the totals, sorting by tags).</summary>
    public void ProbePlaylist(Playlist playlist)
    {
        foreach (var entry in playlist.Entries)
        {
            if (!entry.TagsLoaded && _queued.TryAdd(entry, 0))
            {
                _background.Enqueue((playlist, entry));
            }
        }
        _wake.Release();
    }

    /// <summary>Reads one entry now (used when an entry is added and its tags are needed at once).</summary>
    public void Load(PlaylistEntry entry)
    {
        if (entry.TagsLoaded || entry.IsUrl)
        {
            return;
        }
        var fileTags = _tags.Read(entry.Path);
        entry.IsMissing = !File.Exists(entry.Path);
        if (fileTags is not null)
        {
            // A CUE track keeps its own title and performer; a whole file prefers the file's tags over playlist text.
            entry.Tags = entry.IsVirtual ? (entry.Tags ?? new()).Merge(fileTags) : fileTags.Merge(entry.Tags);
        }
        entry.TagsLoaded = true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Release();
        // Only release the handles once the worker has really stopped (it may be inside a slow file read).
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
            _wake.Dispose();
        }
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (TryNext(out var work))
            {
                try
                {
                    if (!work.Entry.TagsLoaded)
                    {
                        Load(work.Entry);
                        lock (_changed)
                        {
                            _changed.Add(work.Playlist);
                        }
                    }
                }
#pragma warning disable CA1031 // A single bad file must not stop the loader.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    work.Entry.TagsLoaded = true;
                    _logger.LogDebug(ex, "Reading tags failed for {Path}", work.Entry.Path);
                }
                ReportIfDue(force: false);
                continue;
            }
            ReportIfDue(force: true);
            try
            {
                _wake.Wait(1000, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private bool TryNext(out (Playlist Playlist, PlaylistEntry Entry) work)
    {
        if (_visible.TryPop(out work))
        {
            return true;
        }
        if (_background.TryDequeue(out work))
        {
            _queued.TryRemove(work.Entry, out _);
            return true;
        }
        return false;
    }

    private void ReportIfDue(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastReport < BatchInterval.TotalMilliseconds)
        {
            return;
        }
        Playlist[] batch;
        lock (_changed)
        {
            if (_changed.Count == 0)
            {
                return;
            }
            batch = [.. _changed];
            _changed.Clear();
        }
        _lastReport = now;
        Updated?.Invoke(this, batch);
    }
}
