using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Analysis;

/// <summary>What to analyse: a file, or a CUE virtual track inside it (FM-09).</summary>
public sealed record AnalysisTarget(string Path, TimeSpan? Start = null, TimeSpan? End = null)
{
    public static AnalysisTarget For(PlaylistEntry entry) => new(entry.Path, entry.Start, entry.End);

    /// <summary>Streams and other URLs are never analysed.</summary>
    public bool IsAnalysable => !Path.Contains("://", StringComparison.Ordinal);

    /// <summary>Cache kind for this target (virtual tracks get their own entries).</summary>
    internal string KindFor(string kind) => Start is null ? kind : $"{kind}@{Start.Value.Ticks}-{End?.Ticks ?? -1}";
}

[Flags]
public enum AnalysisKinds
{
    None = 0,
    Waveform = 1,
    Loudness = 2,
}

/// <summary>Lower runs first.</summary>
public enum AnalysisPriority
{
    Current = 0,
    Next = 1,
    Background = 2,
}

public sealed class AnalysisCompletedEventArgs(AnalysisTarget target, AnalysisKinds kinds, string? error) : EventArgs
{
    public AnalysisTarget Target { get; } = target;

    /// <summary>The kinds now available in the cache.</summary>
    public AnalysisKinds Kinds { get; } = kinds;

    /// <summary>Set when the file could not be decoded.</summary>
    public string? Error { get; } = error;
}

public sealed class AnalysisProgressEventArgs(AnalysisTarget target, double fraction) : EventArgs
{
    public AnalysisTarget Target { get; } = target;
    public double Fraction { get; } = fraction;
}

/// <summary>
/// AN-01 part 2: the background analysis queue. Jobs are prioritised (current track, next track, background),
/// merged when the same target is requested again, and skipped when the cache already holds valid results (cache key:
/// path + size + modified time). Each job decodes the file once and feeds every requested analyser.
/// Workers are below-normal-priority threads; their number follows Settings › General › analysis threads.
/// Events are raised on worker threads.
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private static readonly IAnalyzer[] AllAnalyzers = [new WaveformAnalyzer(), new LoudnessAnalyzer()];

    private readonly CacheStore _cache;
    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly SettingsStore _settings;
    private readonly ILogger<AnalysisService> _logger;
    private readonly Lock _lock = new();
    private readonly PriorityQueue<Job, (int Priority, long Sequence)> _queue = new();
    private readonly Dictionary<AnalysisTarget, Job> _pending = [];
    private readonly List<Thread> _workers = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0);
    private long _sequence;
    private int _desiredWorkers;
    private int _batchDone;
    private int _batchTotal;

    public AnalysisService(CacheStore cache, IEnumerable<IAudioSourceFactory> factories, SettingsStore settings, ILogger<AnalysisService> logger)
    {
        _cache = cache;
        _factories = factories;
        _settings = settings;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
        SetWorkers(settings.Current.General.AnalysisThreads);
    }

    /// <summary>Analysis results are now in the cache (or failed).</summary>
    public event EventHandler<AnalysisCompletedEventArgs>? Completed;

    /// <summary>Decoding progress of a job (about every 2 %).</summary>
    public event EventHandler<AnalysisProgressEventArgs>? Progress;

    /// <summary>The queue's counters changed (PW6 status text).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Jobs finished and requested since the queue was last empty; (0, 0) when idle.</summary>
    public (int Done, int Total) Status
    {
        get
        {
            lock (_lock)
            {
                return (_batchDone, _batchTotal);
            }
        }
    }

    public int WorkerCount
    {
        get
        {
            lock (_lock)
            {
                return _workers.Count(w => w.IsAlive);
            }
        }
    }

    /// <summary>Queues analysis of <paramref name="target"/>; kinds already cached are not redone.</summary>
    public void Request(AnalysisTarget target, AnalysisKinds kinds, AnalysisPriority priority = AnalysisPriority.Background)
    {
        if (!target.IsAnalysable || kinds == AnalysisKinds.None || _stop.IsCancellationRequested)
        {
            return;
        }
        lock (_lock)
        {
            if (_pending.TryGetValue(target, out var job))
            {
                job.Kinds |= kinds;
                if ((int)priority < job.Priority)
                {
                    job.Priority = (int)priority;
                    _queue.Enqueue(job, (job.Priority, _sequence++)); // the stale entry is skipped later
                    _signal.Release();
                }
                return;
            }
            job = new Job(target) { Kinds = kinds, Priority = (int)priority };
            _pending[target] = job;
            _queue.Enqueue(job, (job.Priority, _sequence++));
            _batchTotal++;
        }
        _signal.Release();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public Waveform? TryGetWaveform(AnalysisTarget target) =>
        Read(target, AllAnalyzers[0]) is { } data ? Waveform.Deserialize(data) : null;

    public LoudnessResult? TryGetLoudness(AnalysisTarget target) =>
        Read(target, AllAnalyzers[1]) is { } data ? LoudnessResult.Deserialize(data) : null;

    /// <summary>True while <paramref name="target"/> is queued or being analysed.</summary>
    public bool IsPending(AnalysisTarget target)
    {
        lock (_lock)
        {
            return _pending.ContainsKey(target);
        }
    }

    /// <summary>
    /// Copies cached results to the file's new version (after the file was changed on purpose, e.g. tags written),
    /// so it is not analysed again.
    /// </summary>
    public void Rekey(AnalysisTarget target, FileKey oldKey)
    {
        var newKey = FileKey.FromFile(target.Path);
        foreach (var analyzer in AllAnalyzers)
        {
            if (_cache.TryGet(oldKey, target.KindFor(analyzer.Kind), analyzer.DataVersion) is { } data)
            {
                _cache.Put(newKey, target.KindFor(analyzer.Kind), analyzer.DataVersion, data);
            }
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _stop.Cancel();
        Thread[] workers;
        lock (_lock)
        {
            workers = [.. _workers];
            _signal.Release(workers.Length);
        }
        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(5));
        }
        _signal.Dispose();
        _stop.Dispose();
    }

    private byte[]? Read(AnalysisTarget target, IAnalyzer analyzer)
    {
        if (!target.IsAnalysable)
        {
            return null;
        }
        try
        {
            return _cache.TryGet(FileKey.FromFile(target.Path), target.KindFor(analyzer.Kind), analyzer.DataVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.General)
        {
            SetWorkers(e.Settings.General.AnalysisThreads);
        }
    }

    private void SetWorkers(int count)
    {
        count = Math.Clamp(count, 1, 8);
        lock (_lock)
        {
            _desiredWorkers = count;
            _workers.RemoveAll(w => !w.IsAlive);
            while (_workers.Count < count)
            {
                var index = _workers.Count;
                var thread = new Thread(() => WorkLoop(index))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"AudioTastic analysis {index + 1}",
                };
                _workers.Add(thread);
                thread.Start();
            }
        }
        int alive;
        lock (_lock)
        {
            alive = _workers.Count;
        }
        _signal.Release(alive); // wake every worker, so surplus ones notice and exit
    }

    private void WorkLoop(int index)
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Job? job = null;
            lock (_lock)
            {
                if (index >= _desiredWorkers)
                {
                    _workers.Remove(Thread.CurrentThread);
                    _signal.Release(); // pass the wake-up on: it may have been meant for a job
                    return;
                }
                while (job is null && _queue.TryDequeue(out var next, out var key))
                {
                    if (next.Started || key.Priority != next.Priority)
                    {
                        continue; // stale duplicate of a re-prioritised job
                    }
                    next.Started = true;
                    job = next;
                }
            }
            if (job is not null)
            {
                Run(job, token);
            }
        }
    }

    private void Run(Job job, CancellationToken token)
    {
        var target = job.Target;
        string? error = null;
        var done = AnalysisKinds.None;
        try
        {
            var key = FileKey.FromFile(target.Path);
            AnalysisKinds kinds;
            lock (_lock)
            {
                kinds = job.Kinds;
            }
            var todo = new List<IAnalyzer>();
            foreach (var (kind, analyzer) in new[] { (AnalysisKinds.Waveform, AllAnalyzers[0]), (AnalysisKinds.Loudness, AllAnalyzers[1]) })
            {
                if (!kinds.HasFlag(kind))
                {
                    continue;
                }
                if (_cache.TryGet(key, target.KindFor(analyzer.Kind), analyzer.DataVersion) is not null)
                {
                    done |= kind;
                }
                else
                {
                    todo.Add(analyzer);
                }
            }
            if (todo.Count > 0)
            {
                foreach (var (analyzer, data) in Decode(target, todo, token))
                {
                    _cache.Put(key, target.KindFor(analyzer.Kind), analyzer.DataVersion, data);
                    done |= analyzer is WaveformAnalyzer ? AnalysisKinds.Waveform : AnalysisKinds.Loudness;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            error = ex.Message;
            _logger.LogInformation("Could not analyse {Path}: {Error}", target.Path, ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _pending.Remove(target);
                _batchDone++;
                if (_pending.Count == 0)
                {
                    _batchDone = 0;
                    _batchTotal = 0;
                }
            }
        }
        Completed?.Invoke(this, new AnalysisCompletedEventArgs(target, done, error));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<(IAnalyzer Analyzer, byte[] Data)> Decode(AnalysisTarget target, List<IAnalyzer> analyzers, CancellationToken token)
    {
        var factory = _factories.FirstOrDefault(f => f.CanOpen(target.Path))
            ?? throw new NotSupportedException($"No decoder for '{Path.GetExtension(target.Path)}'.");
        var source = factory.Open(target.Path);
        if (target.Start is { } start)
        {
            source = new RangeAudioSource(source, start, target.End);
        }
        using (source)
        {
            var format = source.Format;
            var runs = analyzers.Select(a => a.Start(format, source.LengthFrames)).ToArray();
            var buffer = new float[8192 * format.Channels];
            var length = source.LengthFrames;
            long position = 0;
            var nextReport = 0.0;
            int frames;
            while ((frames = source.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var chunk = buffer.AsSpan(0, frames * format.Channels);
                foreach (var run in runs)
                {
                    run.Process(chunk);
                }
                position += frames;
                if (length is > 0 && (double)position / length.Value >= nextReport)
                {
                    Progress?.Invoke(this, new AnalysisProgressEventArgs(target, Math.Min(1, (double)position / length.Value)));
                    nextReport += 0.02;
                }
            }
            return [.. analyzers.Zip(runs, (a, r) => (a, r.Finish()))];
        }
    }

    private sealed class Job(AnalysisTarget target)
    {
        public AnalysisTarget Target { get; } = target;
        public AnalysisKinds Kinds { get; set; }
        public int Priority { get; set; }
        public bool Started { get; set; }
    }
}
