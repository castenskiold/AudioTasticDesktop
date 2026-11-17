using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// FM-02 (Ogg Vorbis, Opus), FM-03 (ALAC), FM-04 (WMA), FM-06 (TTA): stream info, bit-exact lossless decoding,
/// exact lengths and seeking, with the media from scripts/generate_test_media.py.
/// Musepack and Shorten have no fixtures yet (FFmpeg cannot encode them).
/// </summary>
public sealed class MoreFormatsTests
{
    public MoreFormatsTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    public static TheoryData<string, string, bool, int, int> Formats => new()
    {
        { "sine1k.vorbis.ogg", "vorbis", false, 0, 44_100 },
        { "sine1k.opus.opus", "opus", false, 0, 48_000 },
        { "sine1k.alac.m4a", "alac", true, 16, 44_100 },
        { "sine1k.wma.wma", "wmav2", false, 0, 44_100 },
        { "sine1k.tta.tta", "tta", true, 16, 44_100 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Opens_with_stream_info(string file, string codec, bool lossless, int bits, int rate)
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require(file));

        Assert.Equal(codec, source.Info.Codec);
        Assert.Equal(lossless, source.Info.IsLossless);
        Assert.Equal(bits, source.Info.BitsPerSample);
        Assert.Equal(new AudioFormat(rate, 2), source.Format);
        Assert.True(source.CanSeek);
    }

    [Theory]
    [InlineData("sine1k", "alac.m4a")]
    [InlineData("sweep", "alac.m4a")]
    [InlineData("clicks", "alac.m4a")]
    [InlineData("sine1k", "tta.tta")]
    [InlineData("sweep", "tta.tta")]
    [InlineData("clicks", "tta.tta")]
    public void Lossless_formats_are_bit_identical_to_the_wav(string signal, string formatFile)
    {
        var reference = ReferenceWav.Read(TestMedia.Require($"{signal}.wav16.wav")).Samples;
        using var source = FfmpegAudioSource.Open(TestMedia.Require($"{signal}.{formatFile}"));

        Assert.Equal(Signals.Sha256(reference), Signals.Sha256(Signals.ReadAll(source)));
    }

    [Theory]
    [InlineData("sine1k.vorbis.ogg", 5 * 44_100)]
    [InlineData("sweep.vorbis.ogg", 10 * 44_100)]
    [InlineData("sine1k.opus.opus", 5 * 48_000)]
    [InlineData("sweep.opus.opus", 10 * 48_000)]
    public void Ogg_formats_have_the_exact_length(string file, int frames)
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require(file));

        Assert.Equal(frames, Signals.ReadAll(source).Length / 2);
    }

    [Fact]
    public void Wma_length_is_within_one_block_of_the_original()
    {
        // WMA has no gapless information: the length is only exact to one block (2048 frames).
        using var source = FfmpegAudioSource.Open(TestMedia.Require("sine1k.wma.wma"));

        var frames = Signals.ReadAll(source).Length / 2;

        Assert.InRange(frames, (5 * 44_100) - 2048, (5 * 44_100) + 2048);
    }

    [Theory]
    [InlineData("vorbis", "ogg")]
    [InlineData("opus", "opus")]
    public void Ogg_gapless_pairs_keep_the_split(string format, string extension)
    {
        var wav1 = ReferenceWav.Read(TestMedia.Require("gapless.wav16.part1.wav")).Samples.Length / 2;
        var wav2 = ReferenceWav.Read(TestMedia.Require("gapless.wav16.part2.wav")).Samples.Length / 2;
        using var part1 = FfmpegAudioSource.Open(TestMedia.Require($"gapless.{format}.part1.{extension}"));
        using var part2 = FfmpegAudioSource.Open(TestMedia.Require($"gapless.{format}.part2.{extension}"));
        var scale = part1.Format.SampleRate / 44_100.0; // Opus decodes at 48 kHz (resampled: ±1 frame)

        Assert.InRange(Signals.ReadAll(part1).Length / 2, (long)(wav1 * scale) - 1, (long)(wav1 * scale) + 1);
        Assert.InRange(Signals.ReadAll(part2).Length / 2, (long)(wav2 * scale) - 1, (long)(wav2 * scale) + 1);
    }

    [Theory]
    [InlineData("sweep.alac.m4a", true)]
    [InlineData("sweep.tta.tta", true)]
    [InlineData("sweep.vorbis.ogg", false)]
    [InlineData("sweep.opus.opus", false)]
    public void Seek_lands_on_the_exact_target_frame(string file, bool lossless)
    {
        var path = TestMedia.Require(file);
        float[] sequential;
        using (var whole = FfmpegAudioSource.Open(path))
        {
            sequential = Signals.ReadAll(whole);
        }
        using var source = FfmpegAudioSource.Open(path);
        var channels = source.Format.Channels;
        var length = sequential.Length / channels;
        long[] targets = [length / 2, 1, 1234, 44_100, 100_003, 0, 7];

        foreach (var target in targets)
        {
            source.Seek(target);
            Assert.Equal(target, source.PositionFrames);
            var got = Signals.ReadFrames(source, 2048);
            var expected = sequential.AsSpan((int)target * channels, got.Length);
            if (lossless)
            {
                Assert.True(expected.SequenceEqual(got), $"Seek to {target} in {file} is not sample-exact.");
            }
            else
            {
                Assert.Equal(0, Signals.BestLag(sequential.AsSpan((int)target * channels), got, channels, 4));
                Assert.True(Signals.MaxAbsDifference(expected, got) < 0.05f, $"Seek to {target} in {file}: signal differs too much.");
            }
        }
    }

    [Fact]
    public void Wma_seeks_within_a_millisecond()
    {
        // ASF stores timestamps in milliseconds, so WMA seeks are exact to about 1 ms (44 frames), not to the sample.
        var path = TestMedia.Require("sweep.wma.wma");
        float[] sequential;
        using (var whole = FfmpegAudioSource.Open(path))
        {
            sequential = Signals.ReadAll(whole);
        }
        using var source = FfmpegAudioSource.Open(path);
        var lags = new List<int>();
        foreach (var target in new long[] { 100_003, 1234, 44_100, 200_000, 7 })
        {
            source.Seek(target);
            Assert.Equal(target, source.PositionFrames);
            var got = Signals.ReadFrames(source, 2048);
            lags.Add(Signals.BestLag(sequential.AsSpan((int)target * 2), got, 2, 128));
        }
        Assert.True(lags.All(l => Math.Abs(l) <= 48), "Lags: " + string.Join(", ", lags));
    }

    [Fact]
    public void Drm_protected_asf_headers_are_detected()
    {
        var header = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();
        var encryption = new Guid("2211B3FB-BD23-11D2-B4B7-00A0C955FC6E").ToByteArray();
        byte[] protectedHeader = [.. header, .. new byte[40], .. encryption, .. new byte[20]];
        byte[] plainHeader = [.. header, .. new byte[80]];

        Assert.True(AsfDrm.IsProtected(protectedHeader));
        Assert.False(AsfDrm.IsProtected(plainHeader));
        Assert.False(AsfDrm.IsProtected(TestMedia.Require("sine1k.wma.wma")));
    }

    [Fact]
    public void Drm_protected_wma_gives_a_clear_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"drm-{Guid.NewGuid():N}.wma");
        var header = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();
        var encryption = new Guid("298AE614-2622-4C17-B935-DAE07EE9289C").ToByteArray();
        File.WriteAllBytes(path, [.. header, .. new byte[40], .. encryption, .. new byte[200]]);
        try
        {
            var ex = Assert.Throws<AudioSourceException>(() => FfmpegAudioSource.Open(path));
            Assert.Contains("DRM", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
