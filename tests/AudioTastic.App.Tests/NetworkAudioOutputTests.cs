using AudioTastic.App.Integration;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.App.Tests;

/// <summary>CN-09 audio and video: the engine's output while its mix goes to a TV.</summary>
public sealed class NetworkAudioOutputTests
{
    /// <summary>Renders a ramp: sample i of the stream (per channel) is i / 100000.</summary>
    private sealed class Ramp : IAudioRenderer
    {
        private long _frame;

        public void Render(Span<float> output)
        {
            for (var i = 0; i < output.Length / 2; i++, _frame++)
            {
                output[i * 2] = output[(i * 2) + 1] = _frame / 100_000f;
            }
        }
    }

    [Fact]
    public void Pulls_the_mix_while_started_and_silence_while_stopped()
    {
        using var output = new NetworkAudioOutput("TV");
        output.Open(null, new AudioFormat(48_000, 2), new OutputOptions(20), new Ramp());
        var block = new float[1600 * 2];

        output.Pull(block);
        Assert.All(block, s => Assert.Equal(0f, s)); // opened, not started: silence

        output.StartOutput();
        output.Pull(block);
        Assert.Equal(0f, block[0]);
        Assert.Equal(1599 / 100_000f, block[^1]);
        output.Pull(block);
        Assert.Equal(1600 / 100_000f, block[0]); // continuous

        output.StopOutput(); // paused: the TV keeps getting (silent) audio
        output.Pull(block);
        Assert.All(block, s => Assert.Equal(0f, s));
        Assert.Equal("Cast to TV", output.DeviceName);
    }

    [Fact]
    public void The_first_rate_is_the_stream_rate_and_later_rates_are_resampled_to_it()
    {
        using var output = new NetworkAudioOutput("TV");
        Assert.Equal(48_000, output.StreamRate); // before the engine opens it
        output.Open(null, new AudioFormat(44_100, 2), new OutputOptions(20), new Ramp());
        Assert.Equal(44_100, output.StreamRate);
        output.StartOutput();
        var block = new float[44_100 * 2];
        output.Pull(block);
        Assert.Equal(44_099 / 100_000f, block[^1]); // untouched

        output.Open(null, new AudioFormat(96_000, 2), new OutputOptions(20), new Ramp()); // a 96 kHz track reopens the output
        Assert.Equal(44_100, output.StreamRate);
        output.StartOutput();
        output.Pull(block); // one second at 44.1 kHz = one second of the 96 kHz ramp
        Assert.InRange(block[^2], 95_000 / 100_000f, 96_000 / 100_000f);
    }

    [Fact]
    public void Exclusive_mode_is_refused()
    {
        using var output = new NetworkAudioOutput("TV");
        Assert.Throws<InvalidOperationException>(() => output.Open(null, new AudioFormat(44_100, 2), new OutputOptions(20, Exclusive: true), new Ramp()));
        Assert.False(output.IsOpen);
    }
}
