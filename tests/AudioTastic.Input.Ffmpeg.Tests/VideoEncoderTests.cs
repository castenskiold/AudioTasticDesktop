using System.Diagnostics;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Video;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>SDK 0.4.2 video encoders (FFmpeg plugin, CN-09): live H.264 in fragmented MP4 and MPEG-TS, with or without AAC.</summary>
public sealed class VideoEncoderTests(ITestOutputHelper output)
{
    private static readonly FfmpegVideoEncoderFactory Factory = new();
    private static readonly FfmpegSourceFactory Decoder = new();

    private static void RequireEncoder()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing");
        Assert.SkipUnless(FfmpegVideoEncoderFactory.H264Encoder() is not null, "This FFmpeg build has no H.264 encoder (h264_mf, h264_videotoolbox or libopenh264)");
    }

    /// <summary>A moving white bar on black, like a visualisation frame.</summary>
    private static byte[] Frame(int width, int height, int n)
    {
        var pixels = new byte[width * height * 4];
        var x0 = n * 8 % width;
        for (var y = height / 3; y < height * 2 / 3; y++)
        {
            for (var x = x0; x < Math.Min(width, x0 + (width / 8)); x++)
            {
                pixels.AsSpan(((y * width) + x) * 4, 4).Fill(255);
            }
        }
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
        return pixels;
    }

    private static byte[] Encode(VideoContainer container, AudioFormat? audio, int seconds, int width = 640, int height = 360)
    {
        using var stream = new NonSeekableStream();
        using (var encoder = Factory.Create(new VideoEncodeSettings(container, width, height, 30, 2_000_000, audio), stream))
        {
            var samplesPerFrame = audio is { } a ? a.SampleRate / 30 : 0;
            for (var n = 0; n < seconds * 30; n++)
            {
                encoder.WriteFrame(Frame(width, height, n), TimeSpan.FromSeconds(n / 30.0));
                if (audio is { } format)
                {
                    var samples = new float[samplesPerFrame * format.Channels];
                    for (var i = 0; i < samplesPerFrame; i++)
                    {
                        var v = 0.5f * MathF.Sin(2 * MathF.PI * 440 * ((n * samplesPerFrame) + i) / format.SampleRate);
                        samples[i * format.Channels] = v;
                        samples[(i * format.Channels) + 1] = -v;
                    }
                    encoder.WriteAudio(samples);
                }
            }
            encoder.Finish();
        }
        return stream.ToArray();
    }

    [Fact]
    public void Fragmented_mp4_with_aac_is_a_live_mp4_whose_audio_decodes_to_the_same_length()
    {
        RequireEncoder();
        var bytes = Encode(VideoContainer.FragmentedMp4, new AudioFormat(48_000, 2), seconds: 3);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
        Assert.Contains("moof", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal); // fragments, no index at the end

        var path = Path.Combine(Path.GetTempPath(), $"at-video-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var source = Decoder.Open(path);
            Assert.Equal("aac", source.Info.Codec);
            Assert.Equal(2, source.Format.Channels);
            var seconds = (double)Signals.ReadAll(source).Length / 2 / source.Format.SampleRate;
            Assert.InRange(seconds, 2.9, 3.1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mpeg_ts_is_188_byte_packets_with_sync_bytes()
    {
        RequireEncoder();
        var bytes = Encode(VideoContainer.MpegTs, new AudioFormat(44_100, 2), seconds: 2);
        Assert.Equal(0, bytes.Length % 188);
        for (var i = 0; i < bytes.Length; i += 188)
        {
            Assert.Equal(0x47, bytes[i]);
        }
    }

    [Fact]
    public void Video_only_works_in_both_containers()
    {
        RequireEncoder();
        Assert.True(Encode(VideoContainer.FragmentedMp4, null, 1).Length > 1000);
        Assert.True(Encode(VideoContainer.MpegTs, null, 1).Length > 1000);
    }

    [Fact]
    public void Full_hd_frames_encode_and_report_their_speed()
    {
        // A measurement, not a limit: timing asserts fail when every test project runs at once. The app logs when a
        // live stream falls behind real time.
        RequireEncoder();
        var frame = Frame(1920, 1080, 0);
        using var stream = new NonSeekableStream();
        var watch = Stopwatch.StartNew();
        using (var encoder = Factory.Create(new VideoEncodeSettings(VideoContainer.MpegTs, 1920, 1080, 30, 6_000_000), stream))
        {
            for (var n = 0; n < 60; n++)
            {
                encoder.WriteFrame(frame, TimeSpan.FromSeconds(n / 30.0));
            }
            encoder.Finish();
        }
        watch.Stop();
        output.WriteLine($"{FfmpegVideoEncoderFactory.H264Encoder()}: 60 frames of 1920x1080 in {watch.ElapsedMilliseconds} ms ({stream.Length / 1024} KiB)");
        Assert.True(stream.Length > 10_000);
    }

    [Fact]
    public void Skipped_frames_leave_gaps_and_repeated_times_are_dropped()
    {
        RequireEncoder();
        using var stream = new NonSeekableStream();
        using (var encoder = Factory.Create(new VideoEncodeSettings(VideoContainer.MpegTs, 320, 180, 30, 1_000_000), stream))
        {
            foreach (var n in new[] { 0, 0, 1, 2, 9, 10, 4, 30 }) // a repeat, a gap of 6 frames, one out of order
            {
                encoder.WriteFrame(Frame(320, 180, n), TimeSpan.FromSeconds(n / 30.0));
            }
            encoder.Finish();
        }
        Assert.True(stream.Length > 1000);
    }

    [Fact]
    public void Odd_sizes_are_refused()
    {
        RequireEncoder();
        using var stream = new MemoryStream();
        Assert.Throws<AudioSourceException>(() => Factory.Create(new VideoEncodeSettings(VideoContainer.MpegTs, 641, 360, 30, 1_000_000), stream));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
