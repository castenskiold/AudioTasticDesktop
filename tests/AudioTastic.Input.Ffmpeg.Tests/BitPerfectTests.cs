using AudioTastic.Core.Audio.Output;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// P6 exit criterion (PB-04/PB-05): exclusive mode is bit-perfect. A 16-bit and a 24-bit WAV are played through the
/// engine to a fake exclusive device of the same bit depth (volume 100 %, no ReplayGain, speed 1.0); the integer
/// samples the device receives (the same conversion MiniaudioOutput does) must equal the file's samples exactly.
/// The step from there to the hardware is miniaudio's pass-through (no conversion when the format matches).
/// </summary>
[Collection(nameof(RealTimeTests))] // plays in real time: not next to CPU-heavy tests (conversion)
public sealed class BitPerfectTests
{
    public BitPerfectTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    [Theory]
    [InlineData("sweep.wav16.wav", OutputSampleFormat.S16, 16)]
    [InlineData("sine1k.wav24.wav", OutputSampleFormat.S24, 24)]
    public async Task Exclusive_output_receives_the_files_samples_unchanged(string file, OutputSampleFormat format, int bits)
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-bitperfect-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Output, s => s.Output.Exclusive = true);
        settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = 100);
        using var output = new FakeAudioOutput { Capture = true };
        output.DeviceFormats.AddRange([new DeviceFormat(OutputSampleFormat.S16, 2, 44_100, true), new DeviceFormat(OutputSampleFormat.S24, 2, 44_100, true)]);
        using var engine = new PlaybackEngine([new FfmpegSourceFactory()], output, settings, NullLogger<PlaybackEngine>.Instance);
        var path = TestMedia.Require(file);

        await engine.OpenAsync([path]);
        Assert.True(output.IsExclusive);
        Assert.Equal(format, output.SampleFormat); // the smallest format that holds the source's bits
        Assert.Equal(44_100, output.Format!.Value.SampleRate);
        Assert.False(output.OpenOptions[^1].Dither); // same bit depth: no dither
        await Wait.UntilAsync(() => engine.State == PlaybackState.Stopped, 30_000);

        var reference = ReferenceWav.Read(path).Samples; // float = int / 2^(bits-1), exactly
        var captured = output.Captured;
        var start = Array.FindIndex(captured, v => v != 0) - Array.FindIndex(reference, v => v != 0);
        Assert.True(start >= 0);
        var bytes = new byte[reference.Length * SampleConverter.BytesPerSample(format)];
        var state = new DitherState(1);
        SampleConverter.Convert(captured.AsSpan(start, reference.Length), bytes, format, dither: false, ref state);
        var scale = 1L << (bits - 1);
        for (var i = 0; i < reference.Length; i++)
        {
            var expected = (long)Math.Round(reference[i] * (double)scale);
            var actual = bits == 16
                ? BitConverter.ToInt16(bytes, i * 2)
                : (bytes[i * 3] | (bytes[(i * 3) + 1] << 8) | (bytes[(i * 3) + 2] << 16)) << 8 >> 8;
            if (expected != actual)
            {
                Assert.Fail($"Sample {i}: the device got {actual}, the file has {expected}.");
            }
        }
    }
}
