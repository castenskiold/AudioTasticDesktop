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
/// FM-05: DSD (DSF, DFF) decoded to PCM, and DoP: the 24-bit words must carry the exact DSD bits with alternating
/// 0x05/0xFA markers, and reach a (fake) exclusive 24-bit device unchanged. The fixtures are a 1 kHz DSD64 sine written
/// by scripts/generate_test_media.py, with the raw bitstreams beside them.
/// </summary>
public sealed class DsdTests
{
    public DsdTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    [Theory]
    [InlineData("sine1k.dsd64.dsf", "dsd_lsbf_planar")]
    [InlineData("sine1k.dsd64.dff", "dsd_msbf")]
    public void Dsd_decodes_to_pcm_at_352_8_kHz(string file, string codec)
    {
        using var source = new FfmpegSourceFactory().Open(TestMedia.Require(file));

        Assert.Equal(codec, source.Info.Codec);
        Assert.Equal(new AudioFormat(352_800, 2), source.Format);
        var samples = Signals.ReadAll(source);
        Assert.InRange(samples.Length / 2, 352_000, 353_000);
        var peak = samples.Skip(20_000).Max(Math.Abs);
        Assert.InRange(peak, 0.45f, 0.6f);
    }

    [Theory]
    [InlineData("sine1k.dsd64.dsf")]
    [InlineData("sine1k.dsd64.dff")]
    public void DoP_carries_the_exact_dsd_bits_with_alternating_markers(string file)
    {
        var left = File.ReadAllBytes(TestMedia.Require("sine1k.dsd64.raw-left"));
        var right = File.ReadAllBytes(TestMedia.Require("sine1k.dsd64.raw-right"));
        using var dop = DopSource.Open(TestMedia.Require(file));

        Assert.Equal("dop", dop.Info.Codec);
        Assert.Equal(new AudioFormat(176_400, 2), dop.Format);
        Assert.Equal(2_822_400, dop.DsdRate);
        var samples = Signals.ReadAll(dop);
        var frames = samples.Length / 2;
        Assert.Equal(left.Length / 2, frames);
        for (var f = 0; f < frames; f++)
        {
            AssertWord(samples[f * 2], f, left[f * 2], left[(f * 2) + 1]);
            AssertWord(samples[(f * 2) + 1], f, right[f * 2], right[(f * 2) + 1]);
        }
    }

    [Fact]
    public void DoP_seeks_to_the_exact_frame()
    {
        var left = File.ReadAllBytes(TestMedia.Require("sine1k.dsd64.raw-left"));
        using var dop = DopSource.Open(TestMedia.Require("sine1k.dsd64.dsf"));

        foreach (var target in new long[] { 100_001, 7, 50_000, 0 })
        {
            dop.Seek(target);
            Assert.Equal(target, dop.PositionFrames);
            var buffer = new float[200];
            Assert.Equal(100, dop.Read(buffer));
            for (var f = 0; f < 100; f++)
            {
                var frame = target + f;
                AssertWord(buffer[f * 2], frame, left[frame * 2], left[(frame * 2) + 1]);
            }
        }
    }

    [Fact]
    public async Task DoP_reaches_an_exclusive_24_bit_output_unchanged_at_any_volume()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-dop-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Output, s =>
        {
            s.Output.Exclusive = true;
            s.Output.DsdMode = DsdMode.Dop;
        });
        settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = 30); // DoP ignores the volume
        using var output = new FakeAudioOutput { Capture = true };
        output.DeviceFormats.AddRange([new DeviceFormat(OutputSampleFormat.S24, 2, 176_400, true), new DeviceFormat(OutputSampleFormat.S24, 2, 44_100, true)]);
        using var engine = new PlaybackEngine([new FfmpegSourceFactory()], output, settings, NullLogger<PlaybackEngine>.Instance);

        await engine.OpenAsync([TestMedia.Require("sine1k.dsd64.dsf")]);
        Assert.Equal("dop", engine.CurrentTrack!.Stream.Codec);
        Assert.True(output.IsExclusive);
        Assert.Equal(OutputSampleFormat.S24, output.SampleFormat);
        Assert.Equal(176_400, output.Format!.Value.SampleRate);
        await Wait.UntilAsync(() => engine.State == PlaybackState.Stopped, 20_000);

        // Convert what the device got to 24-bit words: they must be the DoP stream exactly.
        var captured = output.Captured;
        var bytes = new byte[captured.Length * 3];
        var state = new DitherState(1);
        SampleConverter.Convert(captured, bytes, OutputSampleFormat.S24, dither: false, ref state);
        var left = File.ReadAllBytes(TestMedia.Require("sine1k.dsd64.raw-left"));
        var first = Array.FindIndex(captured, v => v != 0);
        Assert.True(first >= 0 && first % 2 == 0);
        var start = first / 2;
        var matched = 0;
        for (var f = 0; f < 1000; f++)
        {
            var o = (start + f) * 2 * 3;
            Assert.Equal(f % 2 == 0 ? 0x05 : 0xFA, bytes[o + 2]);
            Assert.Equal(left[f * 2], bytes[o + 1]);
            Assert.Equal(left[(f * 2) + 1], bytes[o]);
            matched++;
        }
        Assert.Equal(1000, matched);
    }

    [Fact]
    public async Task Without_a_dop_capable_format_dsd_plays_as_pcm()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-dop-pcm-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Output, s =>
        {
            s.Output.Exclusive = true;
            s.Output.DsdMode = DsdMode.Dop;
        });
        using var output = new FakeAudioOutput();
        output.DeviceFormats.Add(new DeviceFormat(OutputSampleFormat.S16, 2, 44_100, true)); // a 16-bit-only device
        using var engine = new PlaybackEngine([new FfmpegSourceFactory()], output, settings, NullLogger<PlaybackEngine>.Instance);

        await engine.OpenAsync([TestMedia.Require("sine1k.dsd64.dff")]);

        Assert.StartsWith("dsd_", engine.CurrentTrack!.Stream.Codec, StringComparison.Ordinal);
        await engine.StopAsync();
    }

    private static void AssertWord(float sample, long frame, byte hi, byte lo)
    {
        var word = (int)Math.Round(sample * 8388608.0) & 0xFFFFFF;
        Assert.Equal(frame % 2 == 0 ? 0x05 : 0xFA, word >> 16);
        Assert.Equal(hi, (word >> 8) & 0xFF);
        Assert.Equal(lo, word & 0xFF);
    }
}
