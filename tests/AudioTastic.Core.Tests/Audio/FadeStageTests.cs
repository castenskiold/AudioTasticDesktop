using AudioTastic.Core.Audio;
using AudioTastic.Core.Playback;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>PB-02: fade ramps (linear in dB) and the engine's fades on pause, resume, stop and seek.</summary>
public sealed class FadeStageTests
{
    [Fact]
    public void Fade_out_falls_60_dB_over_the_length_evenly_in_dB_then_stays_silent()
    {
        var fade = new FadeStage();
        fade.Configure(1000); // 1 frame = 1 ms
        fade.FadeOut(100);
        var samples = Enumerable.Repeat(1f, 200).ToArray(); // mono

        fade.Process(samples, 1);

        // Linear in dB: equal steps of 0.6 dB per frame.
        var db10 = 20 * Math.Log10(samples[10]);
        var db50 = 20 * Math.Log10(samples[50]);
        Assert.Equal(-6.6, db10, 1);
        Assert.Equal(-30.6, db50, 1);
        Assert.All(samples[101..], v => Assert.Equal(0f, v));
        Assert.True(fade.IsSilent);
    }

    [Fact]
    public void Fade_in_rises_to_unity_and_snap_jumps()
    {
        var fade = new FadeStage();
        fade.Configure(1000);
        fade.FadeIn(50, fromSilence: true);
        var samples = Enumerable.Repeat(1f, 100).ToArray();

        fade.Process(samples, 1);

        Assert.True(samples[0] < 0.01f);
        Assert.True(samples[25] > samples[10]);
        Assert.All(samples[52..], v => Assert.Equal(1f, v));
        Assert.Equal(1f, fade.CurrentGain);

        fade.Snap(false);
        var silent = Enumerable.Repeat(1f, 10).ToArray();
        fade.Process(silent, 1);
        Assert.All(silent, v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task Engine_fades_out_on_pause_and_back_in_on_resume()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-fade-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Playback, s => s.Playback.Fades.PauseMilliseconds = 200);
        using var output = new FakeAudioOutput { Capture = true };
        using var engine = new PlaybackEngine([new SyntheticFactory()], output, settings, NullLogger<PlaybackEngine>.Instance);
        await engine.OpenAsync([TempFiles.Touch(SyntheticFactory.PathFor("fade", seconds: 30))]);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await engine.PauseAsync();

        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.True(engine.Graph.Fade.IsSilent);
        // The audio just before the silence is already more than 40 dB down: a fade, not a hard cut.
        var captured = output.Captured;
        var last = Array.FindLastIndex(captured, v => v != 0);
        Assert.True(last > 400, "No audio captured.");
        var tail = captured[(last - 100)..(last + 1)].Max(Math.Abs);
        Assert.True(tail < 0.01f, $"Tail level {tail}: the output was cut, not faded.");

        await engine.PauseAsync(); // resume
        await Wait.UntilAsync(() => engine.Graph.Fade.CurrentGain == 1f);
        Assert.Equal(PlaybackState.Playing, engine.State);

        settings.Update(SettingsGroup.Playback, s => s.Playback.Fades.OnStop = false);
        await engine.StopAsync();
        Assert.Equal(PlaybackState.Stopped, engine.State);
    }
}
