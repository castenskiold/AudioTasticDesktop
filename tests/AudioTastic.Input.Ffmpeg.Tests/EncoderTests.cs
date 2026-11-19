using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>SDK 0.4.1 encoders (FFmpeg plugin): FLAC round-trips bit-exactly, MP3 keeps the length.</summary>
public sealed class EncoderTests
{
    private static readonly FfmpegSourceFactory Decoder = new();
    private static readonly FfmpegEncoderFactory Encoder = new();

    [Theory]
    [InlineData("clicks.wav16.wav", 16)]
    [InlineData("clicks.wav24.wav", 24)]
    public void Flac_round_trips_bit_exactly_even_through_a_non_seekable_stream(string file, int bits)
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing");
        var path = TestMedia.Require(file);
        float[] original;
        AudioFormat format;
        using (var source = Decoder.Open(path))
        {
            format = source.Format;
            original = Signals.ReadAll(source);
        }
        var encoded = Encode(original, format, new EncodeSettings(EncodeFormat.Flac, bits));
        var decoded = DecodeFile(encoded, ".flac", out var decodedFormat);

        Assert.Equal(format, decodedFormat);
        Assert.Equal(original.Length, decoded.Length);
        Assert.Equal(Signals.Sha256(original), Signals.Sha256(decoded));
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(encoded, 0, 4));
    }

    [Fact]
    public void Mp3_keeps_the_length_and_the_signal()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing");
        Assert.SkipUnless(Encoder.Formats.Contains(EncodeFormat.Mp3), "This FFmpeg build has no MP3 encoder (libmp3lame)");
        var path = TestMedia.Require("clicks.wav16.wav");
        float[] original;
        AudioFormat format;
        using (var source = Decoder.Open(path))
        {
            format = source.Format;
            original = Signals.ReadAll(source);
        }
        var decoded = DecodeFile(Encode(original, format, new EncodeSettings(EncodeFormat.Mp3, BitrateKbps: 320)), ".mp3", out var decodedFormat);

        Assert.Equal(format.SampleRate, decodedFormat.SampleRate);
        // MP3 adds encoder delay and padding (gapless info lets FFmpeg trim most of it): within 50 ms.
        Assert.InRange(Math.Abs(decoded.Length - original.Length) / format.Channels, 0, format.SampleRate / 20);
        // The signal survived: same loudness within 0.5 dB (a full delay search is too slow for the test run).
        static double Rms(float[] s) => Math.Sqrt(s.Sum(x => (double)x * x) / s.Length);
        Assert.InRange(20 * Math.Log10(Rms(decoded) / Rms(original)), -0.5, 0.5);
    }

    [Fact]
    public void The_windows_and_linux_ffmpeg_builds_offer_flac()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing");
        Assert.Contains(EncodeFormat.Flac, Encoder.Formats);
    }

    private static byte[] Encode(float[] samples, AudioFormat format, EncodeSettings settings)
    {
        using var memory = new MemoryStream();
        using (var output = new ForwardOnlyStream(memory))
        using (var encoder = Encoder.Create(settings, format, output))
        {
            // Odd block sizes on purpose: the encoder must regroup them into its own frames.
            var offset = 0;
            var block = 777 * format.Channels;
            while (offset < samples.Length)
            {
                var n = Math.Min(block, samples.Length - offset);
                encoder.Write(samples.AsSpan(offset, n));
                offset += n;
            }
            encoder.Finish();
        }
        return memory.ToArray();
    }

    private static float[] DecodeFile(byte[] bytes, string extension, out AudioFormat format)
    {
        var file = Path.Combine(Path.GetTempPath(), $"audiotastic-encoder-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(file, bytes);
        try
        {
            using var source = Decoder.Open(file);
            format = source.Format;
            return Signals.ReadAll(source);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A stream that can only be written forward, like a network response.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
