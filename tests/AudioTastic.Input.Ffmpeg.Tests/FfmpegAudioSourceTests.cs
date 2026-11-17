using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// FM-01 / P1 exit criteria: decoded output matches the reference, gapless lengths are exact, and seeks land on the
/// exact target sample.
/// </summary>
public sealed class FfmpegAudioSourceTests
{
    private const int Rate = 44100;

    public FfmpegAudioSourceTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    public static TheoryData<string, string, bool, int> CoreFormats => new()
    {
        { "sine1k.wav16.wav", "pcm_s16le", true, 16 },
        { "sine1k.wav24.wav", "pcm_s24le", true, 24 },
        { "sine1k.aiff.aiff", "pcm_s16be", true, 16 },
        { "sine1k.flac.flac", "flac", true, 16 },
        { "sine1k.wavpack.wv", "wavpack", true, 16 },
        { "sine1k.mp3.mp3", "mp3", false, 0 },
        { "sine1k.aac.m4a", "aac", false, 0 },
    };

    [Theory]
    [MemberData(nameof(CoreFormats))]
    public void Opens_core_formats_with_stream_info(string file, string codec, bool lossless, int bits)
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require(file));

        Assert.Equal(codec, source.Info.Codec);
        Assert.Equal(lossless, source.Info.IsLossless);
        Assert.Equal(bits, source.Info.BitsPerSample);
        Assert.Equal(new AudioFormat(Rate, 2), source.Format);
        Assert.True(source.CanSeek);
        Assert.Equal(0, source.PositionFrames);
    }

    [Theory]
    [InlineData("sine1k")]
    [InlineData("sweep")]
    [InlineData("clicks")]
    [InlineData("silence")]
    public void Wav_decodes_exactly_like_the_independent_reference_reader(string signal)
    {
        var path = TestMedia.Require($"{signal}.wav16.wav");
        var reference = ReferenceWav.Read(path);
        using var source = FfmpegAudioSource.Open(path);

        var decoded = Signals.ReadAll(source);

        Assert.Equal(Signals.Sha256(reference.Samples), Signals.Sha256(decoded));
    }

    [Fact]
    public void Wav24_decodes_exactly_like_the_independent_reference_reader()
    {
        var path = TestMedia.Require("sine1k.wav24.wav");
        var reference = ReferenceWav.Read(path);
        using var source = FfmpegAudioSource.Open(path);

        Assert.Equal(Signals.Sha256(reference.Samples), Signals.Sha256(Signals.ReadAll(source)));
    }

    [Theory]
    [InlineData("sine1k", "flac.flac")]
    [InlineData("sine1k", "aiff.aiff")]
    [InlineData("sine1k", "wavpack.wv")]
    [InlineData("sweep", "flac.flac")]
    [InlineData("sweep", "aiff.aiff")]
    [InlineData("sweep", "wavpack.wv")]
    [InlineData("clicks", "flac.flac")]
    [InlineData("clicks", "wavpack.wv")]
    public void Lossless_formats_are_bit_identical_to_the_wav(string signal, string formatFile)
    {
        var reference = ReferenceWav.Read(TestMedia.Require($"{signal}.wav16.wav")).Samples;
        using var source = FfmpegAudioSource.Open(TestMedia.Require($"{signal}.{formatFile}"));

        Assert.Equal(Signals.Sha256(reference), Signals.Sha256(Signals.ReadAll(source)));
    }

    [Theory]
    [InlineData("sine1k.mp3.mp3", 5)]
    [InlineData("sine1k.aac.m4a", 5)]
    [InlineData("sweep.mp3.mp3", 10)]
    [InlineData("sweep.aac.m4a", 10)]
    public void Lossy_formats_have_the_exact_length_without_encoder_delay_or_padding(string file, int seconds)
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require(file));

        var frames = Signals.ReadAll(source).Length / 2;

        Assert.Equal(seconds * Rate, frames);
        Assert.Equal(seconds * Rate, source.LengthFrames);
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("aac", "m4a")]
    [InlineData("flac", "flac")]
    public void Gapless_pairs_have_the_same_split_as_the_wav_pair(string format, string extension)
    {
        var wav1 = ReferenceWav.Read(TestMedia.Require("gapless.wav16.part1.wav")).Samples.Length / 2;
        var wav2 = ReferenceWav.Read(TestMedia.Require("gapless.wav16.part2.wav")).Samples.Length / 2;
        using var part1 = FfmpegAudioSource.Open(TestMedia.Require($"gapless.{format}.part1.{extension}"));
        using var part2 = FfmpegAudioSource.Open(TestMedia.Require($"gapless.{format}.part2.{extension}"));

        var a = Signals.ReadAll(part1);
        var b = Signals.ReadAll(part2);

        Assert.Equal(wav1, a.Length / 2);
        Assert.Equal(wav2, b.Length / 2);
    }

    [Fact]
    public void Gapless_mp3_pair_joins_without_a_click()
    {
        var reference = ReferenceWav.Read(TestMedia.Require("gapless.reference.wav")).Samples;
        using var part1 = FfmpegAudioSource.Open(TestMedia.Require("gapless.mp3.part1.mp3"));
        using var part2 = FfmpegAudioSource.Open(TestMedia.Require("gapless.mp3.part2.mp3"));
        var joined = Signals.ReadAll(part1).Concat(Signals.ReadAll(part2)).ToArray();
        var split = (int)part1.PositionFrames;

        // Around the join, the decoded signal must line up with the continuous reference sine.
        var window = 2048 * 2;
        var start = (split * 2) - window;
        var lag = Signals.BestLag(reference.AsSpan(start, window * 2), joined.AsSpan(start, window * 2), 2, 8);
        Assert.Equal(0, lag);
        Assert.True(Signals.MaxAbsDifference(reference.AsSpan(start, window * 2), joined.AsSpan(start, window * 2)) < 0.05f);
    }

    public static TheoryData<string, bool> SeekFiles => new()
    {
        { "sweep.wav16.wav", true },
        { "sweep.aiff.aiff", true },
        { "sweep.flac.flac", true },
        { "sweep.wavpack.wv", true },
        { "sine1k.hires-24-96.flac", true },
        { "sweep.mp3.mp3", false },
        { "sweep.aac.m4a", false },
    };

    [Theory]
    [MemberData(nameof(SeekFiles))]
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
        long[] targets = [length / 2, 1, 1234, 44_100, 100_003, length - 4096, 0, 7];

        foreach (var target in targets)
        {
            if (!lossless && target > length * 0.7)
            {
                continue; // the sweep is above the lossy codecs' low-pass there: nothing left to align against
            }
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
                // Lossy decoders restart after a seek, so values differ slightly; alignment must still be exact.
                Assert.Equal(0, Signals.BestLag(sequential.AsSpan((int)target * channels), got, channels, 4));
                Assert.True(Signals.MaxAbsDifference(expected, got) < 0.05f, $"Seek to {target} in {file}: signal differs too much.");
            }
            Assert.Equal(target + (got.Length / channels), source.PositionFrames);
        }
    }

    [Fact]
    public void Seek_past_the_end_reads_nothing()
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require("sine1k.flac.flac"));

        source.Seek(10 * Rate);

        Assert.Equal(0, source.Read(new float[256]));
    }

    [Fact]
    public void Hires_flac_reports_24_bit_96_khz()
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require("sine1k.hires-24-96.flac"));

        Assert.Equal(24, source.Info.BitsPerSample);
        Assert.Equal(96_000, source.Format.SampleRate);
        Assert.Equal(5 * 96_000, Signals.ReadAll(source).Length / 2);
    }

    [Fact]
    public void Tags_are_read_from_the_container()
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require("sine1k.flac.flac"));

        Assert.Contains("encoder", source.Tags.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_or_invalid_files_throw_AudioSourceException()
    {
        var notAudio = Path.Combine(TestMedia.RepoRoot, "AudioTastic.slnx");

        Assert.Throws<AudioSourceException>(() => FfmpegAudioSource.Open(Path.Combine(TestMedia.RepoRoot, "missing.flac")));
        Assert.Throws<AudioSourceException>(() => FfmpegAudioSource.Open(notAudio));
    }

    [Fact]
    public void Read_rejects_buffers_that_split_a_frame()
    {
        using var source = FfmpegAudioSource.Open(TestMedia.Require("sine1k.flac.flac"));

        Assert.Throws<ArgumentException>(() => source.Read(new float[3]));
    }

    [Fact]
    public void Factory_accepts_core_extensions_only()
    {
        var factory = new FfmpegSourceFactory();

        Assert.True(factory.CanOpen("x.FLAC"));
        Assert.True(factory.CanOpen("a/b/c.wv"));
        Assert.False(factory.CanOpen("x.ogg")); // FM-02, P4
        Assert.False(factory.CanOpen("x.txt"));
    }
}
