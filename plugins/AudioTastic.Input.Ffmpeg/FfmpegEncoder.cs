using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// SDK 0.4.1 encoders through FFmpeg: FLAC (FFmpeg's own encoder, 16 or 24 bit) and MP3 (libmp3lame, when the FFmpeg
/// build has it). Used for cast transcoding (CN-04) and, later, conversion (TL-01).
/// </summary>
public sealed unsafe class FfmpegEncoderFactory : IAudioEncoderFactory
{
    private IReadOnlyCollection<EncodeFormat>? _formats;

    public string Name => "FFmpeg";

    public IReadOnlyCollection<EncodeFormat> Formats => _formats ??= FindFormats();

    public IAudioEncoder Create(EncodeSettings settings, AudioFormat format, Stream output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        return new FfmpegEncoder(settings, format, output);
    }

    private static List<EncodeFormat> FindFormats()
    {
        if (!FfmpegLibrary.IsAvailable)
        {
            return [];
        }
        var formats = new List<EncodeFormat>();
        if (ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC) != null)
        {
            formats.Add(EncodeFormat.Flac);
        }
        if (ffmpeg.avcodec_find_encoder_by_name("libmp3lame") != null)
        {
            formats.Add(EncodeFormat.Mp3);
        }
        return formats;
    }
}

/// <summary>One FFmpeg encoding session: encoder + muxer writing through a custom I/O callback to a .NET stream.</summary>
public sealed unsafe class FfmpegEncoder : IAudioEncoder
{
    private const int IoBufferSize = 32 * 1024;

    private readonly Stream _output;
    private readonly int _channels;
    private readonly AVSampleFormat _sampleFormat;
    private readonly int _frameSize;
    private readonly float[] _pending;
    private readonly avio_alloc_context_write_packet _writePacket; // kept alive while FFmpeg holds the pointer
    private AVCodecContext* _codec;
    private AVFormatContext* _format;
    private AVStream* _stream;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private AVIOContext* _io;
    private int _pendingFrames;
    private long _pts;
    private bool _finished;
    private Exception? _writeError;
    private byte[] _copy = new byte[IoBufferSize];

    public FfmpegEncoder(EncodeSettings settings, AudioFormat format, Stream output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FfmpegLibrary.EnsureLoaded();
        _output = output;
        _channels = format.Channels;
        var flac = settings.Format == EncodeFormat.Flac;
        var codec = flac ? ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC) : ffmpeg.avcodec_find_encoder_by_name("libmp3lame");
        if (codec == null)
        {
            throw new AudioSourceException($"This FFmpeg build cannot encode {settings.Format}.");
        }
        try
        {
            _codec = ffmpeg.avcodec_alloc_context3(codec);
            _codec->sample_rate = format.SampleRate;
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, _channels);
            _codec->ch_layout = layout;
            _codec->time_base = new AVRational { num = 1, den = format.SampleRate };
            if (flac)
            {
                var bits24 = settings.BitsPerSample > 16;
                _sampleFormat = bits24 ? AVSampleFormat.AV_SAMPLE_FMT_S32 : AVSampleFormat.AV_SAMPLE_FMT_S16;
                _codec->bits_per_raw_sample = bits24 ? 24 : 16;
            }
            else
            {
                _sampleFormat = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                _codec->bit_rate = settings.BitrateKbps * 1000L;
            }
            _codec->sample_fmt = _sampleFormat;

            AVFormatContext* formatContext = null;
            Check(ffmpeg.avformat_alloc_output_context2(&formatContext, null, flac ? "flac" : "mp3", null), "create the container");
            _format = formatContext;
            if ((_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }
            Check(ffmpeg.avcodec_open2(_codec, codec, null), "open the encoder");
            _stream = ffmpeg.avformat_new_stream(_format, null);
            Check(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec), "set up the stream");
            _stream->time_base = _codec->time_base;

            var buffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
            _writePacket = WritePacket;
            _io = ffmpeg.avio_alloc_context(buffer, IoBufferSize, 1, null, null, _writePacket, null);
            _format->pb = _io;
            _format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            Check(ffmpeg.avformat_write_header(_format, null), "write the header");

            _frameSize = _codec->frame_size > 0 ? _codec->frame_size : 4096;
            _frame = ffmpeg.av_frame_alloc();
            _frame->nb_samples = _frameSize;
            _frame->format = (int)_sampleFormat;
            _frame->sample_rate = format.SampleRate;
            Check(ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_codec->ch_layout), "set up the frame");
            Check(ffmpeg.av_frame_get_buffer(_frame, 0), "allocate the frame");
            _packet = ffmpeg.av_packet_alloc();
            _pending = new float[_frameSize * _channels];
        }
        catch
        {
            Free();
            throw;
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_codec == null, this);
        while (!samples.IsEmpty)
        {
            var room = (_frameSize - _pendingFrames) * _channels;
            var take = Math.Min(room, samples.Length);
            samples[..take].CopyTo(_pending.AsSpan(_pendingFrames * _channels));
            _pendingFrames += take / _channels;
            samples = samples[take..];
            if (_pendingFrames == _frameSize)
            {
                EncodePending();
            }
        }
    }

    public void Finish()
    {
        if (_finished || _codec == null)
        {
            return;
        }
        _finished = true;
        if (_pendingFrames > 0)
        {
            EncodePending(); // the last, shorter frame
        }
        Check(ffmpeg.avcodec_send_frame(_codec, null), "flush the encoder");
        Drain();
        Check(ffmpeg.av_write_trailer(_format), "finish the stream");
        ffmpeg.avio_flush(_io);
        ThrowIfWriteFailed();
        _output.Flush();
    }

    private void EncodePending()
    {
        Check(ffmpeg.av_frame_make_writable(_frame), "prepare the frame");
        var frames = _pendingFrames;
        _frame->nb_samples = frames;
        var source = _pending.AsSpan(0, frames * _channels);
        switch (_sampleFormat)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_S16:
                var s16 = new Span<short>(_frame->data[0], frames * _channels);
                for (var i = 0; i < s16.Length; i++)
                {
                    s16[i] = (short)Math.Clamp(MathF.Round(source[i] * 32768f), short.MinValue, short.MaxValue);
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S32:
                // 24-bit FLAC from S32: the sample sits in the top 24 bits.
                var s32 = new Span<int>(_frame->data[0], frames * _channels);
                for (var i = 0; i < s32.Length; i++)
                {
                    s32[i] = (int)Math.Clamp(Math.Round(source[i] * 8388608.0), -8388608, 8388607) << 8;
                }
                break;
            default: // planar float (MP3)
                for (var c = 0; c < _channels; c++)
                {
                    var plane = new Span<float>(_frame->extended_data[c], frames);
                    for (var i = 0; i < frames; i++)
                    {
                        plane[i] = source[(i * _channels) + c];
                    }
                }
                break;
        }
        _frame->pts = _pts;
        _pts += frames;
        _pendingFrames = 0;
        Check(ffmpeg.avcodec_send_frame(_codec, _frame), "encode");
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (result == ffmpeg.AVERROR_EOF || result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return;
            }
            Check(result, "encode");
            _packet->stream_index = _stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _codec->time_base, _stream->time_base);
            var written = ffmpeg.av_interleaved_write_frame(_format, _packet);
            ThrowIfWriteFailed();
            Check(written, "write");
        }
    }

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
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }
        if (_codec != null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
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
