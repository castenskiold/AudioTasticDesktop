using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>
/// PB-04: the real miniaudio shim on miniaudio's null backend (no sound card needed). Skipped when the native
/// library has not been built (see native/README.md).
/// </summary>
public sealed class MiniaudioOutputTests
{
    private static MiniaudioOutput CreateOrSkip()
    {
        try
        {
            return new MiniaudioOutput(useNullBackend: true);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is DllNotFoundException)
        {
            Assert.Skip("miniaudio native library missing: build native/ with CMake (native/README.md).");
            throw;
        }
    }

    [Fact]
    public void Lists_the_null_device()
    {
        using var output = CreateOrSkip();

        var devices = output.GetDevices();

        Assert.Equal("Null", output.BackendName);
        Assert.NotEmpty(devices);
    }

    [Fact]
    public async Task Pulls_audio_from_the_renderer_until_stopped()
    {
        using var output = CreateOrSkip();
        var renderer = new CountingRenderer();

        output.Open(null, new AudioFormat(48_000, 2), 20, renderer);
        output.StartOutput();
        await Wait.UntilAsync(() => renderer.Frames > 48_000 / 10);
        output.StopOutput();
        var after = renderer.Frames;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(output.IsOpen);
        Assert.False(output.IsRunning);
        Assert.Equal(after, renderer.Frames);
        Assert.Equal(new AudioFormat(48_000, 2), output.Format);
    }

    [Fact]
    public void Open_replaces_a_previously_open_device()
    {
        using var output = CreateOrSkip();
        var renderer = new CountingRenderer();

        output.Open(null, new AudioFormat(44_100, 2), 20, renderer);
        output.Open(null, new AudioFormat(96_000, 2), 20, renderer);

        Assert.Equal(96_000, output.Format!.Value.SampleRate);
        output.Close();
        Assert.False(output.IsOpen);
    }

    private sealed class CountingRenderer : IAudioRenderer
    {
        private long _frames;
        public long Frames => Interlocked.Read(ref _frames);

        public void Render(Span<float> output)
        {
            output.Clear();
            Interlocked.Add(ref _frames, output.Length / 2);
        }
    }
}
