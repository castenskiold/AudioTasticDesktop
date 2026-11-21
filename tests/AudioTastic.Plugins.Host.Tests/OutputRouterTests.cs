using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Plugins.Host.Tests;

/// <summary>PB-04 / PG-01: output plugins listed beside the built-in devices, routed, guarded and switched off safely.</summary>
public sealed partial class PluginHostTests
{
    private const string SinkDevice = "Test output: Null sink";

    [Fact]
    public void Diverted_opens_go_to_the_diverted_output_until_it_is_cleared()
    {
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var diverted = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        var renderer = new CountingRenderer();
        var format = new AudioFormat(44_100, 2);
        var speakers = router.GetDevices()[0];

        router.Open(speakers, format, new OutputOptions(20), renderer);
        Assert.Single(builtIn.Opens);
        router.Divert(diverted); // CN-09: the mix goes to a TV
        Assert.True(router.IsOpen); // still open: the engine reopens, which switches
        Assert.Empty(router.GetDeviceFormats(speakers));
        router.Open(speakers, format, new OutputOptions(20), renderer);
        Assert.False(builtIn.IsOpen);
        Assert.Single(diverted.Opens);
        Assert.Single(builtIn.Opens);
        Assert.Equal("Fake", router.BackendName);

        router.Divert(null);
        router.Open(speakers, format, new OutputOptions(20), renderer); // the engine's reopen switches back
        Assert.False(diverted.IsOpen);
        Assert.Equal(2, builtIn.Opens.Count);
    }

    [Fact]
    public async Task Output_plugin_devices_are_listed_and_the_chosen_one_plays()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        var renderer = new CountingRenderer();

        var devices = router.GetDevices();
        Assert.Equal(["Speakers", "Headphones", SinkDevice], devices.Select(d => d.Name));

        router.Open(devices.Single(d => d.Name == SinkDevice), new AudioFormat(44_100, 2), new OutputOptions(20), renderer);
        router.StartOutput();
        await Wait.UntilAsync(() => renderer.Calls > 3, 5_000);
        Assert.Equal("Test sink", router.BackendName);
        Assert.Equal(SinkDevice, router.DeviceName);
        Assert.Equal("test.good", router.ActivePluginId);
        Assert.True(router.IsRunning);
        Assert.False(builtIn.IsOpen);

        // Choosing a built-in device closes the plugin's output and opens the built-in one.
        router.Open(devices.Single(d => d.Name == "Headphones"), new AudioFormat(44_100, 2), new OutputOptions(20), renderer);
        Assert.Null(router.ActivePluginId);
        Assert.True(builtIn.IsOpen);
        Assert.Equal("Headphones", router.DeviceName);
        router.Close();
    }

    [Fact]
    public void A_refusal_from_an_output_plugin_is_not_a_fault()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        var sink = router.GetDevices().Single(d => d.Name == SinkDevice);

        for (var i = 0; i < 5; i++)
        {
            Assert.Throws<InvalidOperationException>(() =>
                router.Open(sink, new AudioFormat(44_100, 2), new OutputOptions(20, Exclusive: true), new CountingRenderer()));
        }
        Assert.Equal(PluginStatus.Enabled, host.Plugins.Single().Status);
    }

    [Fact]
    public void Failing_output_plugin_is_disabled_and_its_devices_disappear()
    {
        MakePlugin(_builtIn, "test.bad", mode: "output-open-throws");
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        var sink = router.GetDevices().Single(d => d.Name == SinkDevice);

        for (var i = 0; i < 3; i++)
        {
            // Turned into InvalidOperationException, so the engine falls back to the default device.
            Assert.Throws<InvalidOperationException>(() =>
                router.Open(sink, new AudioFormat(44_100, 2), new OutputOptions(20), new CountingRenderer()));
        }

        Assert.Equal(PluginStatus.Failed, host.Plugins.Single().Status);
        Assert.DoesNotContain(router.GetDevices(), d => d.Name == SinkDevice);
    }

    [Fact]
    public async Task The_engine_moves_its_playing_output_to_a_diverted_output_and_back()
    {
        // CN-09 audio and video: the switch happens in the engine's reopen, while playing.
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var tv = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        using var engine = new AudioTastic.Core.Playback.PlaybackEngine(host.LiveSourceFactories, router, _settings,
            NullLogger<AudioTastic.Core.Playback.PlaybackEngine>.Instance);
        var file = Path.Combine(_dir, "tone.tst");
        File.WriteAllBytes(file, []);
        await engine.OpenAsync([file]);
        await Wait.UntilAsync(() => builtIn.IsRunning, 5_000);

        router.Divert(tv);
        await engine.ReopenOutputAsync();
        Assert.True(tv.IsRunning);
        Assert.False(builtIn.IsOpen);

        router.Divert(null);
        await engine.ReopenOutputAsync();
        Assert.True(builtIn.IsRunning);
        Assert.False(tv.IsOpen);
        Assert.Equal(AudioTastic.Core.Playback.PlaybackState.Playing, engine.State);
        Assert.Null(_settings.Current.Output.DeviceName); // the settings are untouched
        await engine.StopAsync();
    }

    [Fact]
    public async Task Switching_off_the_playing_output_plugin_continues_on_the_default_device()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        using var builtIn = new FakeAudioOutput();
        using var router = new OutputRouter(builtIn, host);
        _settings.Update(SettingsGroup.Output, s => s.Output.DeviceName = SinkDevice);
        using var engine = new AudioTastic.Core.Playback.PlaybackEngine(host.LiveSourceFactories, router, _settings,
            NullLogger<AudioTastic.Core.Playback.PlaybackEngine>.Instance);
        var files = Enumerable.Range(0, 4).Select(i => Path.Combine(_dir, $"tone{i}.tst")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllBytes(file, []);
        }

        await engine.OpenAsync(files);
        Assert.Equal("test.good", router.ActivePluginId);
        Assert.False(builtIn.IsOpen);

        host.Disable("test.good"); // e.g. in Settings › Plugins while it plays
        await Wait.UntilAsync(() => builtIn.IsRunning, 5_000);

        Assert.Null(router.ActivePluginId);
        Assert.Equal(AudioTastic.Core.Playback.PlaybackState.Playing, engine.State);
        Assert.DoesNotContain(router.GetDevices(), d => d.Name == SinkDevice);
        await engine.StopAsync();
    }

    private sealed class CountingRenderer : IAudioRenderer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Render(Span<float> output)
        {
            output.Clear();
            Interlocked.Increment(ref _calls);
        }
    }
}
