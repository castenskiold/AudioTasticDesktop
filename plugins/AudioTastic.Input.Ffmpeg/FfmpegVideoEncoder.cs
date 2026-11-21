using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Video;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// SDK 0.4.2 (CN-09): live H.264 video through FFmpeg for casting visualisations. The H.264 encoder is the operating
/// system's where FFmpeg has one (h264_mf on Windows, h264_videotoolbox on macOS), else OpenH264 (BSD); never x264
/// (GPL). Audio, when asked, is AAC (FFmpeg's own encoder). Chromecasts get fragmented MP4, DLNA TVs MPEG-TS (both
/// tested live on Anders's devices, 21 Nov 2026).
/// </summary>
public sealed unsafe class FfmpegVideoEncoderFactory : IVideoEncoderFactory
{
    /// <summary>H.264 encoders in order of preference (the first this FFmpeg build has is used).</summary>
    public static readonly string[] H264Encoders = ["h264_mf", "h264_videotoolbox", "libopenh264"];

    private IReadOnlyCollection<VideoContainer>? _containers;

    public string Name => "FFmpeg";

    public IReadOnlyCollection<VideoContainer> Containers => _containers ??= FindContainers();

    public IVideoEncoder Create(VideoEncodeSettings settings, Stream output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        return new FfmpegVideoEncoder(settings, output);
    }

    /// <summary>The H.264 encoder this machine would use, or null.</summary>
    public static string? H264Encoder()
    {
        if (!FfmpegLibrary.IsAvailable)
        {
            return null;
        }
        foreach (var name in H264Encoders)
        {
            if (ffmpeg.avcodec_find_encoder_by_name(name) != null)
            {
                return name;
            }
        }
        return null;
    }

    private static List<VideoContainer> FindContainers()
    {
        var containers = new List<VideoContainer>();
        if (H264Encoder() is null || ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC) == null)
        {
            return containers;
        }
        if (ffmpeg.av_guess_format("mp4", null, null) != null)
        {
            containers.Add(VideoContainer.FragmentedMp4);
        }
        if (ffmpeg.av_guess_format("mpegts", null, null) != null)
        {
            containers.Add(VideoContainer.MpegTs);
        }
        return containers;
    }
}

/// <summary>One live video session: H.264 (+ AAC) encoders and a muxer writing through a custom I/O callback.</summary>
public sealed unsafe class FfmpegVideoEncoder : IVideoEncoder
{
    private const int IoBufferSize = 64 * 1024;

    private readonly Stream _output;
    private readonly VideoEncodeSettings _settings;
    private readonly avio_alloc_context_write_packet _writePacket; // kept alive while FFmpeg holds the pointer
    private readonly bool _nv12;
    private readonly int _channels;
    private AVFormatContext* _format;
    private AVIOContext* _io;
    private AVCodecContext* _video;
    private AVCodecContext* _audio;
    private AVStream* _videoStream;
    private AVStream* _audioStream;
    private AVFrame* _picture;
    private AVFrame* _sound;
    private AVPacket* _packet;
    private float[] _pending = [];
    private int _pendingFrames;
    private int _audioFrameSize;
    private long _videoPts;
    private long _audioPts;
    private bool _finished;
    private Exception? _writeError;
    private byte[] _copy = new byte[IoBufferSize];

    public FfmpegVideoEncoder(VideoEncodeSettings settings, Stream output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FfmpegLibrary.EnsureLoaded();
        if (settings.Width % 2 != 0 || settings.Height % 2 != 0 || settings.Width < 16 || settings.Height < 16)
        {
            throw new AudioSourceException("Video frames need even sizes of at least 16 pixels.");
        }
        _settings = settings;
        _output = output;
        var name = FfmpegVideoEncoderFactory.H264Encoder() ?? throw new AudioSourceException("This FFmpeg build has no H.264 encoder.");
        EncoderName = name;
        _nv12 = name is "h264_mf" or "h264_videotoolbox";
        try
        {
            AVFormatContext* formatContext = null;
            var muxer = settings.Container == VideoContainer.FragmentedMp4 ? "mp4" : "mpegts";
            Check(ffmpeg.avformat_alloc_output_context2(&formatContext, null, muxer, null), "create the container");
            _format = formatContext;
            var globalHeader = (_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

            OpenVideo(name, globalHeader);
            if (settings.Audio is { } audio)
            {
                _channels = audio.Channels;
                OpenAudio(audio, globalHeader);
            }

            var buffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
            _writePacket = WritePacket;
            _io = ffmpeg.avio_alloc_context(buffer, IoBufferSize, 1, null, null, _writePacket, null);
            _format->pb = _io;
            _format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            AVDictionary* options = null;
            if (settings.Container == VideoContainer.FragmentedMp4)
            {
                // A live MP4: no index at the end; a fragment starts at every key frame (once a second).
                ffmpeg.av_dict_set(&options, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
            }
            var header = ffmpeg.avformat_write_header(_format, &options);
            ffmpeg.av_dict_free(&options);
            Check(header, "write the header");
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Free();
            throw;
        }
    }

    public string EncoderName { get; }

    private void OpenVideo(string name, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        _video = ffmpeg.avcodec_alloc_context3(codec);
        _video->width = _settings.Width;
        _video->height = _settings.Height;
        _video->pix_fmt = _nv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
        _video->time_base = new AVRational { num = 1, den = _settings.FramesPerSecond };
        _video->framerate = new AVRational { num = _settings.FramesPerSecond, den = 1 };
        _video->gop_size = _settings.FramesPerSecond; // a key frame every second: devices can join quickly
        _video->max_b_frames = 0;
        _video->bit_rate = _settings.VideoBitrate;
        _video->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _video->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        _video->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _video->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        if (globalHeader)
        {
            _video->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }
        AVDictionary* options = null;
        if (name == "h264_mf")
        {
            ffmpeg.av_dict_set(&options, "rate_control", "cbr", 0);
            ffmpeg.av_dict_set(&options, "scenario", "live_streaming", 0);
        }
        var opened = ffmpeg.avcodec_open2(_video, codec, &options);
        ffmpeg.av_dict_free(&options);
        Check(opened, $"open the video encoder ({name})");
        _videoStream = ffmpeg.avformat_new_stream(_format, null);
        Check(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _video), "set up the video stream");
        _videoStream->time_base = _video->time_base;

        _picture = ffmpeg.av_frame_alloc();
        _picture->width = _settings.Width;
        _picture->height = _settings.Height;
        _picture->format = (int)_video->pix_fmt;
        Check(ffmpeg.av_frame_get_buffer(_picture, 32), "allocate the picture");
    }

    private void OpenAudio(AudioFormat audio, bool globalHeader)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
        {
            throw new AudioSourceException("This FFmpeg build has no AAC encoder.");
        }
        _audio = ffmpeg.avcodec_alloc_context3(codec);
        _audio->sample_rate = audio.SampleRate;
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, audio.Channels);
        _audio->ch_layout = layout;
        _audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audio->bit_rate = _settings.AudioBitrate;
        _audio->time_base = new AVRational { num = 1, den = audio.SampleRate };
        if (globalHeader)
        {
            _audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }
        Check(ffmpeg.avcodec_open2(_audio, codec, null), "open the AAC encoder");
        _audioStream = ffmpeg.avformat_new_stream(_format, null);
        Check(ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audio), "set up the audio stream");
        _audioStream->time_base = _audio->time_base;

        _audioFrameSize = _audio->frame_size > 0 ? _audio->frame_size : 1024;
        _sound = ffmpeg.av_frame_alloc();
        _sound->nb_samples = _audioFrameSize;
        _sound->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _sound->sample_rate = audio.SampleRate;
        Check(ffmpeg.av_channel_layout_copy(&_sound->ch_layout, &_audio->ch_layout), "set up the audio frame");
        Check(ffmpeg.av_frame_get_buffer(_sound, 0), "allocate the audio frame");
        _pending = new float[_audioFrameSize * audio.Channels];
    }

    public void WriteFrame(ReadOnlySpan<byte> bgra)
    {
        ObjectDisposedException.ThrowIf(_video == null, this);
        if (bgra.Length < _settings.Width * _settings.Height * 4)
        {
            throw new ArgumentException("The frame is smaller than the video size.", nameof(bgra));
        }
        Check(ffmpeg.av_frame_make_writable(_picture), "prepare the picture");
        fixed (byte* source = bgra)
        {
            if (_nv12)
            {
                ToNv12(source, _settings.Width, _settings.Height, _picture->data[0], _picture->linesize[0], _picture->data[1], _picture->linesize[1]);
            }
            else
            {
                ToYuv420(source, _settings.Width, _settings.Height, _picture->data[0], _picture->linesize[0],
                    _picture->data[1], _picture->linesize[1], _picture->data[2], _picture->linesize[2]);
            }
        }
        _picture->pts = _videoPts++;
        Check(ffmpeg.avcodec_send_frame(_video, _picture), "encode video");
        Drain(_video, _videoStream);
    }

    public void WriteAudio(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_video == null, this);
        if (_audio == null)
        {
            return; // video only
        }
        while (!samples.IsEmpty)
        {
            var room = (_audioFrameSize - _pendingFrames) * _channels;
            var take = Math.Min(room, samples.Length);
            samples[..take].CopyTo(_pending.AsSpan(_pendingFrames * _channels));
            _pendingFrames += take / _channels;
            samples = samples[take..];
            if (_pendingFrames == _audioFrameSize)
            {
                EncodeAudio(_audioFrameSize);
            }
        }
    }

    private void EncodeAudio(int frames)
    {
        Check(ffmpeg.av_frame_make_writable(_sound), "prepare the audio frame");
        _sound->nb_samples = frames;
        for (var c = 0; c < _channels; c++)
        {
            var plane = new Span<float>(_sound->extended_data[c], frames);
            for (var i = 0; i < frames; i++)
            {
                plane[i] = _pending[(i * _channels) + c];
            }
        }
        _sound->pts = _audioPts;
        _audioPts += frames;
        _pendingFrames = 0;
        Check(ffmpeg.avcodec_send_frame(_audio, _sound), "encode audio");
        Drain(_audio, _audioStream);
    }

    public void Finish()
    {
        if (_finished || _video == null)
        {
            return;
        }
        _finished = true;
        if (_audio != null)
        {
            if (_pendingFrames > 0)
            {
                EncodeAudio(_pendingFrames);
            }
            Check(ffmpeg.avcodec_send_frame(_audio, null), "flush audio");
            Drain(_audio, _audioStream);
        }
        Check(ffmpeg.avcodec_send_frame(_video, null), "flush video");
        Drain(_video, _videoStream);
        Check(ffmpeg.av_write_trailer(_format), "finish the stream");
        ffmpeg.avio_flush(_io);
        ThrowIfWriteFailed();
        _output.Flush();
    }

    private void Drain(AVCodecContext* codec, AVStream* stream)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(codec, _packet);
            if (result == ffmpeg.AVERROR_EOF || result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return;
            }
            Check(result, "encode");
            _packet->stream_index = stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, codec->time_base, stream->time_base);
            var written = ffmpeg.av_interleaved_write_frame(_format, _packet);
            ThrowIfWriteFailed();
            Check(written, "write");
        }
    }

    // BT.709, limited range, in 8.8 fixed point; each 2 × 2 block shares one chroma sample.
    private static void ToNv12(byte* bgra, int width, int height, byte* y, int yStride, byte* uv, int uvStride)
    {
        for (var row = 0; row < height; row += 2)
        {
            var p0 = bgra + (row * width * 4);
            var p1 = p0 + (width * 4);
            var y0 = y + (row * yStride);
            var y1 = y0 + yStride;
            var c = uv + (row / 2 * uvStride);
            for (var x = 0; x < width; x += 2)
            {
                Block(p0 + (x * 4), p1 + (x * 4), y0 + x, y1 + x, out var u, out var v);
                c[x] = u;
                c[x + 1] = v;
            }
        }
    }

    private static void ToYuv420(byte* bgra, int width, int height, byte* y, int yStride, byte* u, int uStride, byte* v, int vStride)
    {
        for (var row = 0; row < height; row += 2)
        {
            var p0 = bgra + (row * width * 4);
            var p1 = p0 + (width * 4);
            var y0 = y + (row * yStride);
            var y1 = y0 + yStride;
            var ur = u + (row / 2 * uStride);
            var vr = v + (row / 2 * vStride);
            for (var x = 0; x < width; x += 2)
            {
                Block(p0 + (x * 4), p1 + (x * 4), y0 + x, y1 + x, out ur[x / 2], out vr[x / 2]);
            }
        }
    }

    private static void Block(byte* top, byte* bottom, byte* yTop, byte* yBottom, out byte u, out byte v)
    {
        int b0 = top[0], g0 = top[1], r0 = top[2];
        int b1 = top[4], g1 = top[5], r1 = top[6];
        int b2 = bottom[0], g2 = bottom[1], r2 = bottom[2];
        int b3 = bottom[4], g3 = bottom[5], r3 = bottom[6];
        yTop[0] = Luma(r0, g0, b0);
        yTop[1] = Luma(r1, g1, b1);
        yBottom[0] = Luma(r2, g2, b2);
        yBottom[1] = Luma(r3, g3, b3);
        var r = (r0 + r1 + r2 + r3) >> 2;
        var g = (g0 + g1 + g2 + g3) >> 2;
        var b = (b0 + b1 + b2 + b3) >> 2;
        u = (byte)Math.Clamp(128 + (((-26 * r) - (87 * g) + (112 * b) + 128) >> 8), 16, 240);
        v = (byte)Math.Clamp(128 + (((112 * r) - (102 * g) - (10 * b) + 128) >> 8), 16, 240);
    }

    private static byte Luma(int r, int g, int b) => (byte)(16 + (((47 * r) + (157 * g) + (16 * b) + 128) >> 8));

    /// <summary>FFmpeg's output callback: copies a block to the .NET stream (errors are rethrown after FFmpeg returns).</summary>
    private int WritePacket(void* opaque, byte* buffer, int size)
    {
        try
        {
            if (_copy.Length < size)
            {
                _copy = new byte[size];
            }
            Marshal.Copy((IntPtr)buffer, _copy, 0, size);
            _output.Write(_copy, 0, size);
            return size;
        }
#pragma warning disable CA1031 // Rethrown on the caller's side once FFmpeg returns (never across native frames).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _writeError ??= ex;
            return ffmpeg.AVERROR(5); // EIO
        }
    }

    private void ThrowIfWriteFailed()
    {
        if (_writeError is { } error)
        {
            throw error is IOException ? new IOException(error.Message, error) : new IOException("The output stream failed.", error);
        }
    }

    private void Check(int result, string action)
    {
        ThrowIfWriteFailed();
        if (result < 0)
        {
            const int size = 256;
            var buffer = stackalloc byte[size];
            ffmpeg.av_strerror(result, buffer, size);
            throw new AudioSourceException($"FFmpeg could not {action}: {Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {result}"}");
        }
    }

    private void Free()
    {
        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }
        if (_picture != null)
        {
            var frame = _picture;
            ffmpeg.av_frame_free(&frame);
            _picture = null;
        }
        if (_sound != null)
        {
            var frame = _sound;
            ffmpeg.av_frame_free(&frame);
            _sound = null;
        }
        if (_video != null)
        {
            var codec = _video;
            ffmpeg.avcodec_free_context(&codec);
            _video = null;
        }
        if (_audio != null)
        {
            var codec = _audio;
            ffmpeg.avcodec_free_context(&codec);
            _audio = null;
        }
        if (_io != null)
        {
            ffmpeg.av_freep(&_io->buffer);
            var io = _io;
            ffmpeg.avio_context_free(&io);
            _io = null;
        }
        if (_format != null)
        {
            _format->pb = null;
            ffmpeg.avformat_free_context(_format);
            _format = null;
        }
    }

    public void Dispose() => Free();
}
