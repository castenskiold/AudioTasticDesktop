using AudioTastic.Core.Playback;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>PB-01 gapless hand-over and PB-04/PB-05 output negotiation, with synthetic tracks.</summary>
public sealed class GaplessTests : IDisposable
{
    private readonly SyntheticFactory _factory = new();
    private readonly FakeAudioOutput _output = new() { Capture = true };
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;

    public GaplessTests()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-gapless-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(paths);
        _settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = 100); // unity gain: compare samples exactly
        _engine = new PlaybackEngine([_factory], _output, _settings, NullLogger<PlaybackEngine>.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _output.Dispose();
    }

    private static string Track(string name, int rate = 44_100, double seconds = 1) =>
        TempFiles.Touch(SyntheticFactory.PathFor(name, rate, seconds));

    [Fact]
    public async Task Next_track_follows_without_a_gap_sample_exactly()
    {
        var changes = new List<string>();
        _engine.TrackChanged += (_, e) =>
        {
            if (e.Track is { } t)
            {
                lock (changes)
                {
                    changes.Add(t.Title);
                }
            }
        };

        await _engine.OpenAsync([Track("one", seconds: 1.5), Track("two", seconds: 1)]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);

        var captured = _output.Captured;
        var start = FindStart(captured);
        var expected = Expected(66_150).Concat(Expected(44_100)).ToArray(); // track one, then track two from its start
        Assert.True(captured.Length >= start + expected.Length, "Not enough audio captured.");
        Assert.Equal(expected, captured.AsSpan(start, expected.Length).ToArray());
        lock (changes)
        {
            Assert.Equal(["one", "two"], changes);
        }
        Assert.Single(_output.Opens); // one device open for both tracks
    }

    [Fact]
    public async Task A_track_at_another_rate_is_resampled_unless_the_output_rate_must_match()
    {
        await _engine.OpenAsync([Track("one", 44_100, 1.2), Track("two", 48_000, 0.5)]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);
        Assert.Single(_output.Opens); // resampled into the open 44.1 kHz output

        _settings.Update(SettingsGroup.Output, s => s.Output.MatchOutputRate = true);
        await _engine.OpenAsync([Track("three", 44_100, 1.2), Track("four", 48_000, 0.5)]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);
        Assert.Equal(48_000, _output.Opens[^1].Format.SampleRate); // reopened at the second track's rate
    }

    [Fact]
    public async Task Gapless_off_still_plays_the_next_track()
    {
        _settings.Update(SettingsGroup.Playback, s => s.Playback.Gapless = false);
        await _engine.OpenAsync([Track("one", seconds: 0.5), Track("two", seconds: 0.5)]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);
        Assert.Equal(0, _engine.Graph.Handovers);
    }

    [Fact]
    public async Task Exclusive_mode_picks_the_device_format_and_falls_back_to_shared()
    {
        _output.DeviceFormats.AddRange([
            new DeviceFormat(OutputSampleFormat.S16, 2, 44_100, true),
            new DeviceFormat(OutputSampleFormat.S24, 2, 44_100, true),
            new DeviceFormat(OutputSampleFormat.S24, 2, 48_000, true),
        ]);
        _settings.Update(SettingsGroup.Output, s => s.Output.Exclusive = true);

        await _engine.OpenAsync([Track("a", 44_100, 5)], play: true); // synthetic sources report 32 bits
        Assert.True(_output.IsExclusive);
        Assert.Equal(OutputSampleFormat.S24, _output.SampleFormat); // the largest format the device has
        Assert.Equal(44_100, _output.Format!.Value.SampleRate);

        await _engine.OpenAsync([Track("b", 96_000, 5)]); // no 96 kHz format: the highest rate, resampled
        Assert.Equal(48_000, _output.Format!.Value.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(5), _engine.Duration);

        _settings.Update(SettingsGroup.Output, s => s.Output.AllowResampling = false);
        await _engine.OpenAsync([Track("c", 96_000, 5)]); // not allowed: shared mode at the track's rate
        Assert.False(_output.IsExclusive);
        Assert.Equal(96_000, _output.Format!.Value.SampleRate);

        _settings.Update(SettingsGroup.Output, s => s.Output.AllowResampling = true);
        _output.RefuseExclusive = true; // e.g. another app holds the device
        await _engine.OpenAsync([Track("d", 44_100, 5)]);
        Assert.False(_output.IsExclusive);
        Assert.Equal(OutputSampleFormat.F32, _output.SampleFormat);
        await _engine.StopAsync();
    }

    private static int FindStart(float[] captured)
    {
        for (var i = 0; i + 1 < captured.Length; i += 2)
        {
            if (captured[i] == SyntheticSource.ValueAt(0, 0) && captured[i + 1] == SyntheticSource.ValueAt(0, 1) &&
                captured[i + 2] == SyntheticSource.ValueAt(1, 0))
            {
                return i;
            }
        }
        throw new InvalidOperationException("Track start not found in the captured audio.");
    }

    private static IEnumerable<float> Expected(int frames)
    {
        for (var f = 0; f < frames; f++)
        {
            yield return SyntheticSource.ValueAt(f, 0);
            yield return SyntheticSource.ValueAt(f, 1);
        }
    }
}
