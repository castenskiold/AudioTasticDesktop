using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>
/// EQ-06 latency compensation: the position the player shows lags what the deck has handed on by the DSP chain's
/// delay (look-ahead limiter, VST3 plug-in latency), so the time display matches what is heard.
/// </summary>
public sealed class LatencyCompensationTests : IDisposable
{
    private readonly SyntheticFactory _factory = new();
    private readonly FakeAudioOutput _output = new();
    private readonly PlaybackEngine _engine;

    public LatencyCompensationTests()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-latency-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _engine = new PlaybackEngine([_factory], _output, new SettingsStore(paths), NullLogger<PlaybackEngine>.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _output.Dispose();
    }

    /// <summary>A stage that passes audio through and reports a fixed delay.</summary>
    private sealed class DelayReportingStage(int frames) : DspStage("latency", "Latency")
    {
        public override int LatencyFrames => frames;

        public override void Configure(int sampleRate, int channels)
        {
        }

        public override void Process(Span<float> samples, int channels)
        {
        }
    }

    [Fact]
    public void The_chain_adds_up_the_delays_of_its_enabled_stages()
    {
        var chain = new DspChain();
        var limiter = new LimiterStage { Enabled = true };
        limiter.Apply(new LimiterSettings());
        var plugin = new DelayReportingStage(1000) { Enabled = true };
        var off = new DelayReportingStage(5000) { Enabled = false };
        chain.SetStages([limiter, plugin, off]);
        chain.Configure(48_000, 2);
        Assert.Equal(240 + 1000, chain.LatencyFrames); // the limiter looks 5 ms ahead
        chain.Bypass = true;
        Assert.Equal(0, chain.LatencyFrames);
    }

    [Fact]
    public async Task The_position_is_the_audible_one_and_never_before_a_seek()
    {
        const int rate = 48_000;
        const int latency = rate / 10; // 100 ms
        _engine.Graph.Dsp.SetStages([new DelayReportingStage(latency) { Enabled = true }]);

        await _engine.OpenAsync([TempFiles.Touch(SyntheticFactory.PathFor("latency", rate, 10, 2))]);
        await Wait.UntilAsync(() => _engine.Graph.DeckA.PositionFrames > rate / 2);
        await _engine.PauseAsync();
        var handedOn = _engine.Graph.DeckA.PositionFrames;
        Assert.Equal(handedOn - latency, (long)Math.Round(_engine.Position.TotalSeconds * rate));

        await _engine.SeekAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), _engine.Position); // right after a jump: the target, not 4.9 s

        _engine.Graph.Dsp.SetStages([]);
        Assert.Equal(_engine.Graph.DeckA.PositionFrames, (long)Math.Round(_engine.Position.TotalSeconds * rate));
    }
}
