using System.Diagnostics;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Analysis.Tests;

/// <summary>AN-01 queue and CO-07 waveforms on real files (needs FFmpeg and the generated media).</summary>
public sealed class AnalysisServiceTests : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly CacheStore _cache;
    private readonly CountingFactory _factory = new(new FfmpegSourceFactory());

    public AnalysisServiceTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
        var dir = Directory.CreateTempSubdirectory("audiotastic-analysis-").FullName;
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(_paths);
        _cache = new CacheStore(_paths);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnalysisService Create(int threads = 1)
    {
        _settings.Update(SettingsGroup.General, s => s.General.AnalysisThreads = threads);
        return new AnalysisService(_cache, [_factory], _settings, NullLogger<AnalysisService>.Instance);
    }

    [Fact]
    public async Task Analyses_waveform_and_loudness_in_one_decode_and_caches_them()
    {
        using var service = Create();
        var target = new AnalysisTarget(TestMedia.Require("sine1k.flac.flac"));

        var done = await RunAsync(service, target, AnalysisKinds.Waveform | AnalysisKinds.Loudness);

        Assert.Null(done.Error);
        Assert.Equal(AnalysisKinds.Waveform | AnalysisKinds.Loudness, done.Kinds);
        Assert.Equal(1, _factory.Opens);
        var waveform = service.TryGetWaveform(target)!;
        Assert.Equal(2, waveform.Channels);
        Assert.Equal(WaveformAnalyzer.Buckets, waveform.Buckets);
        Assert.Equal(TimeSpan.FromSeconds(5), waveform.Duration);
        // FFmpeg's sine source has amplitude 1/8, and the mono-to-stereo upmix lowers it by 3 dB: peak 0.0884.
        Assert.All(waveform.Max[0], v => Assert.InRange(v, 0.085f, 0.09f));
        Assert.InRange(service.TryGetLoudness(target)!.IntegratedLufs, -21.2, -20.9);

        // A second request is answered from the cache without decoding.
        done = await RunAsync(service, target, AnalysisKinds.Waveform | AnalysisKinds.Loudness);
        Assert.Equal(AnalysisKinds.Waveform | AnalysisKinds.Loudness, done.Kinds);
        Assert.Equal(1, _factory.Opens);
    }

    [Fact]
    public async Task Five_minute_flac_waveform_is_ready_in_under_2_s_and_instant_from_cache()
    {
        // P5 exit criterion (the CI/dev machine stands in for a mid-range PC).
        var target = new AnalysisTarget(TestMedia.Require("long5min.flac"));
        using (var service = Create())
        {
            var watch = Stopwatch.StartNew();
            var done = await RunAsync(service, target, AnalysisKinds.Waveform);
            watch.Stop();
            Assert.Null(done.Error);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Waveform took {watch.Elapsed.TotalMilliseconds:0} ms.");
        }
        using (var service = Create())
        {
            var watch = Stopwatch.StartNew();
            var waveform = service.TryGetWaveform(target);
            watch.Stop();
            Assert.NotNull(waveform);
            Assert.Equal(TimeSpan.FromMinutes(5), waveform.Duration);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(100), $"Cached waveform took {watch.Elapsed.TotalMilliseconds:0} ms.");
        }
    }

    [Fact]
    public async Task Current_track_jumps_the_queue()
    {
        using var service = Create(threads: 1);
        var order = new List<string>();
        var allDone = new TaskCompletionSource();
        string[] files = ["sweep.wav16.wav", "sweep.flac.flac", "sweep.aiff.aiff", "sweep.wavpack.wv", "sine1k.wav16.wav"];
        service.Completed += (_, e) =>
        {
            lock (order)
            {
                order.Add(Path.GetFileName(e.Target.Path));
                if (order.Count == files.Length)
                {
                    allDone.TrySetResult();
                }
            }
        };
        foreach (var file in files[..^1])
        {
            service.Request(new AnalysisTarget(TestMedia.Require(file)), AnalysisKinds.Waveform);
        }
        service.Request(new AnalysisTarget(TestMedia.Require(files[^1])), AnalysisKinds.Waveform, AnalysisPriority.Current);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The single worker may already have started the first background job; the current one comes next.
        Assert.Contains(files[^1], order.Take(2));
        Assert.Equal((0, 0), service.Status);
    }

    [Fact]
    public async Task Virtual_tracks_and_failures()
    {
        using var service = Create();
        var path = TestMedia.Require("sweep.flac.flac");
        var whole = new AnalysisTarget(path);
        var part = new AnalysisTarget(path, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

        await RunAsync(service, whole, AnalysisKinds.Waveform);
        await RunAsync(service, part, AnalysisKinds.Waveform);

        Assert.Equal(TimeSpan.FromSeconds(10), service.TryGetWaveform(whole)!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(3), service.TryGetWaveform(part)!.Duration);

        var broken = await RunAsync(service, new AnalysisTarget(Path.Combine(TestMedia.RepoRoot, "AudioTastic.slnx")), AnalysisKinds.Waveform);
        Assert.NotNull(broken.Error);
        Assert.Equal(AnalysisKinds.None, broken.Kinds);

        // Streams are never queued.
        service.Request(new AnalysisTarget("http://radio.example/live"), AnalysisKinds.Waveform);
        Assert.Equal((0, 0), service.Status);
    }

    [Fact]
    public async Task Worker_count_follows_the_setting()
    {
        using var service = Create(threads: 1);
        Assert.Equal(1, service.WorkerCount);
        _settings.Update(SettingsGroup.General, s => s.General.AnalysisThreads = 3);
        Assert.Equal(3, service.WorkerCount);
        _settings.Update(SettingsGroup.General, s => s.General.AnalysisThreads = 1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.WorkerCount > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, service.WorkerCount);
    }

    [Fact]
    public void Waveform_round_trips_through_the_cache_format()
    {
        var run = new WaveformAnalyzer().Start(new AudioFormat(44_100, 2), 3 * 44_100);
        run.Process(LoudnessTests.Sine(44_100, 2, 100, 0.8, 2));
        run.Process(new float[44_100 * 2]);
        var waveform = Waveform.Deserialize(run.Finish())!;

        Assert.Equal(TimeSpan.FromSeconds(3), waveform.Duration);
        Assert.Equal(WaveformAnalyzer.Buckets, waveform.Buckets);
        Assert.InRange(waveform.Max[1][..100].Max(), 0.79f, 0.81f);
        Assert.InRange(waveform.Min[0][..100].Min(), -0.81f, -0.79f);
        Assert.Equal(0, waveform.Max[0][^1]); // the silent last second
        Assert.Null(Waveform.Deserialize([0, 1]));
    }

    private static async Task<AnalysisCompletedEventArgs> RunAsync(AnalysisService service, AnalysisTarget target, AnalysisKinds kinds)
    {
        var done = new TaskCompletionSource<AnalysisCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, AnalysisCompletedEventArgs e)
        {
            if (e.Target == target)
            {
                done.TrySetResult(e);
            }
        }
        service.Completed += OnCompleted;
        try
        {
            service.Request(target, kinds, AnalysisPriority.Current);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            service.Completed -= OnCompleted;
        }
    }

    private sealed class CountingFactory(IAudioSourceFactory inner) : IAudioSourceFactory
    {
        private int _opens;
        public int Opens => _opens;
        public string Name => inner.Name;
        public IReadOnlyCollection<string> Extensions => inner.Extensions;
        public bool CanOpen(string path) => inner.CanOpen(path);

        public IAudioSource Open(string path)
        {
            Interlocked.Increment(ref _opens);
            return inner.Open(path);
        }
    }
}
