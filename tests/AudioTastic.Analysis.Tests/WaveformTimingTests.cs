using System.Diagnostics;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Analysis.Tests;

/// <summary>Timing tests run on their own within this project, so other tests in it do not compete for the CPU.</summary>
[CollectionDefinition(nameof(TimingTests), DisableParallelization = true)]
public sealed class TimingTests;

/// <summary>
/// P5 exit criterion (CO-07): the waveform of a 5-minute FLAC is ready in under 2 s and instant from the cache.
/// Measured alone on Anders's PC: about 1.0 s (Debug) and 0.6 s (Release). Other test projects run at the same time, so
/// the best of up to three attempts counts.
/// </summary>
[Collection(nameof(TimingTests))]
public sealed class WaveformTimingTests : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;

    public WaveformTimingTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
        var dir = Directory.CreateTempSubdirectory("audiotastic-timing-").FullName;
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(_paths);
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

    [Fact]
    public async Task Five_minute_flac_waveform_is_ready_in_under_2_s_and_instant_from_cache()
    {
        var target = new AnalysisTarget(TestMedia.Require("long5min.flac"));
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= TimeSpan.FromSeconds(2); attempt++)
        {
            var cache = new CacheStore(AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, Path.Combine(_paths.Root, $"run{attempt}"),
                Path.Combine(_paths.Root, $"run{attempt}"), null, null, [AppPaths.PortableSwitch])).EnsureCreated());
            using var service = new AnalysisService(cache, [new FfmpegSourceFactory()], _settings, NullLogger<AnalysisService>.Instance);
            var done = new TaskCompletionSource<AnalysisCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Completed += (_, e) => done.TrySetResult(e);
            var watch = Stopwatch.StartNew();
            service.Request(target, AnalysisKinds.Waveform, AnalysisPriority.Current);
            var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            watch.Stop();
            Assert.Null(result.Error);
            best = watch.Elapsed < best ? watch.Elapsed : best;

            var cached = Stopwatch.StartNew();
            var waveform = service.TryGetWaveform(target);
            cached.Stop();
            Assert.NotNull(waveform);
            Assert.Equal(TimeSpan.FromMinutes(5), waveform.Duration);
            Assert.True(cached.Elapsed < TimeSpan.FromMilliseconds(100), $"Cached waveform took {cached.Elapsed.TotalMilliseconds:0} ms.");
        }
        Assert.True(best < TimeSpan.FromSeconds(2), $"Best of 3: the waveform took {best.TotalMilliseconds:0} ms.");
    }
}
