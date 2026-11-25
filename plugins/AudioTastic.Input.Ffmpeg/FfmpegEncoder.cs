using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// SDK encoders through FFmpeg, used for cast transcoding (CN-04) and conversion (TL-01): FLAC, ALAC and WAV (16 or
/// 24 bit), MP3 (libmp3lame, CBR or VBR), AAC (FFmpeg's own encoder, in .m4a), Opus (libopus) and Vorbis (libvorbis).
/// Each format is offered only when this FFmpeg build has its encoder.
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

    /// <summary>
    /// The sample rate the encoder supports closest to the input's (the input's own when allowed, else the next higher
    /// one, else the highest); MP3 and Opus at most two channels.
    /// </summary>
    public AudioFormat InputFormatFor(EncodeSettings settings, AudioFormat input)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var channels = settings.Format is EncodeFormat.Mp3 or EncodeFormat.Opus ? Math.Min(input.Channels, 2) : input.Channels;
        var codec = FfmpegLibrary.IsAvailable ? FfmpegEncoder.FindCodec(settings) : null;
        if (codec == null)
        {
            return new AudioFormat(input.SampleRate, channels);
        }
        var rates = FfmpegEncoder.SupportedRates(codec);
        var rate = rates.Count == 0 || rates.Contains(input.SampleRate) ? input.SampleRate
            : rates.Where(r => r > input.SampleRate).DefaultIfEmpty(rates.Max()).Min();
        return new AudioFormat(rate, channels);
    }

    private static List<EncodeFormat> FindFormats()
    {
        if (!FfmpegLibrary.IsAvailable)
        {
            return [];
        }
        return [.. Enum.GetValues<EncodeFormat>().Where(f => FfmpegEncoder.FindCodec(new EncodeSettings(f)) != null)];
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
    private readonly bool _padLastFrame;
    private readonly float[] _pending;
    private readonly avio_alloc_context_write_packet _writePacket; // kept alive while FFmpeg holds the pointer
    private readonly avio_alloc_context_seek? _seek;
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
        ArgumentNullException.ThrowIfNull(output);
        FfmpegLibrary.EnsureLoaded();
        _output = output;
        _channels = format.Channels;
        var codec = FindCodec(settings);
        if (codec == null)
        {
            throw new AudioSourceException($"This FFmpeg build cannot encode {settings.Format}.");
        }
        var bits24 = settings.BitsPerSample > 16;
        try
        {
            _codec = ffmpeg.avcodec_alloc_context3(codec);
            _codec->sample_rate = format.SampleRate;
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, _channels);
            _codec->ch_layout = layout;
            _codec->time_base = new AVRational { num = 1, den = format.SampleRate };
            _sampleFormat = settings.Format switch
            {
                EncodeFormat.Flac or EncodeFormat.Wav => bits24 ? AVSampleFormat.AV_SAMPLE_FMT_S32 : AVSampleFormat.AV_SAMPLE_FMT_S16,
                EncodeFormat.Alac => bits24 ? AVSampleFormat.AV_SAMPLE_FMT_S32P : AVSampleFormat.AV_SAMPLE_FMT_S16P,
                EncodeFormat.Opus => AVSampleFormat.AV_SAMPLE_FMT_FLT,
                _ => AVSampleFormat.AV_SAMPLE_FMT_FLTP, // MP3, AAC, Vorbis
            };
            if (settings.Format is EncodeFormat.Flac or EncodeFormat.Alac or EncodeFormat.Wav)
            {
                _codec->bits_per_raw_sample = bits24 ? 24 : 16; // 24-bit samples sit in the top bits of S32
            }
            else if (settings.Format == EncodeFormat.Mp3 && settings.Vbr)
            {
                _codec->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                _codec->global_quality = Math.Clamp(settings.VbrQuality, 0, 9) * ffmpeg.FF_QP2LAMBDA; // LAME -V
            }
            else
            {
                _codec->bit_rate = settings.BitrateKbps * 1000L;
            }
            _codec->sample_fmt = _sampleFormat;

            AVFormatContext* formatContext = null;
            Check(ffmpeg.avformat_alloc_output_context2(&formatContext, null, MuxerOf(settings.Format), null), "create the container");
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
            // A seekable output lets muxers go back to finish their headers (MP4 moov, WAV sizes, the MP3 gapless header).
            _seek = output.CanSeek ? Seek : null;
            _io = ffmpeg.avio_alloc_context(buffer, IoBufferSize, 1, null, null, _writePacket, _seek);
            _format->pb = _io;
            _format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            Check(ffmpeg.avformat_write_header(_format, null), "write the header");

            var variable = (codec->capabilities & ffmpeg.AV_CODEC_CAP_VARIABLE_FRAME_SIZE) != 0;
            _frameSize = _codec->frame_size > 0 && !variable ? _codec->frame_size : 4096;
            _padLastFrame = _codec->frame_size > 0 && !variable && (codec->capabilities & ffmpeg.AV_CODEC_CAP_SMALL_LAST_FRAME) == 0;
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

    /// <summary>The FFmpeg encoder for a format, or null when this build has none (libopus and libvorbis, not FFmpeg's experimental ones).</summary>
    internal static AVCodec* FindCodec(EncodeSettings settings) => settings.Format switch
    {
        EncodeFormat.Flac => ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_FLAC),
        EncodeFormat.Mp3 => ffmpeg.avcodec_find_encoder_by_name("libmp3lame"),
        EncodeFormat.Opus => ffmpeg.avcodec_find_encoder_by_name("libopus"),
        EncodeFormat.Aac => ffmpeg.avcodec_find_encoder_by_name("aac"),
        EncodeFormat.Alac => ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_ALAC),
        EncodeFormat.Vorbis => ffmpeg.avcodec_find_encoder_by_name("libvorbis"),
        EncodeFormat.Wav => ffmpeg.avcodec_find_encoder(settings.BitsPerSample > 16 ? AVCodecID.AV_CODEC_ID_PCM_S24LE : AVCodecID.AV_CODEC_ID_PCM_S16LE),
        _ => null,
    };

    private static string MuxerOf(EncodeFormat format) => format switch
    {
        EncodeFormat.Flac => "flac",
        EncodeFormat.Mp3 => "mp3",
        EncodeFormat.Opus => "opus",
        EncodeFormat.Aac or EncodeFormat.Alac => "ipod", // .m4a
        EncodeFormat.Vorbis => "ogg",
        _ => "wav",
    };

    /// <summary>The sample rates an encoder accepts; empty means any.</summary>
    internal static List<int> SupportedRates(AVCodec* codec)
    {
        void* configs = null;
        var count = 0;
        if (ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE, 0, &configs, &count) < 0 || configs == null)
        {
            return [];
        }
        return [.. new ReadOnlySpan<int>(configs, count).ToArray()];
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
            if (_padLastFrame)
            {
                _pending.AsSpan(_pendingFrames * _channels).Clear();
                _pendingFrames = _frameSize; // this encoder takes only whole frames
            }
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
                    s16[i] = ToS16(source[i]);
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S32:
                var s32 = new Span<int>(_frame->data[0], frames * _channels);
                for (var i = 0; i < s32.Length; i++)
                {
                    s32[i] = ToS24High(source[i]);
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                source.CopyTo(new Span<float>(_frame->data[0], frames * _channels));
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                for (var c = 0; c < _channels; c++)
                {
                    var plane = new Span<short>(_frame->extended_data[c], frames);
                    for (var i = 0; i < frames; i++)
                    {
                        plane[i] = ToS16(source[(i * _channels) + c]);
                    }
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                for (var c = 0; c < _channels; c++)
                {
                    var plane = new Span<int>(_frame->extended_data[c], frames);
                    for (var i = 0; i < frames; i++)
                    {
                        plane[i] = ToS24High(source[(i * _channels) + c]);
                    }
                }
                break;
            default: // planar float
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

    private static short ToS16(float sample) => (short)Math.Clamp(MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);

    /// <summary>A 24-bit sample in the top 24 bits of an int (how FFmpeg's FLAC, ALAC and PCM encoders take S32).</summary>
    private static int ToS24High(float sample) => (int)Math.Clamp(Math.Round(sample * 8388608.0), -8388608, 8388607) << 8;

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

    /// <summary>FFmpeg's seek callback (seekable outputs only): SEEK_SET/CUR/END, or the size for AVSEEK_SIZE.</summary>
    private long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
            {
                return _output.Length;
            }
            var origin = (whence & 3) switch
            {
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => SeekOrigin.Begin,
            };
            return _output.Seek(offset, origin);
        }
#pragma warning disable CA1031 // Rethrown on the caller's side once FFmpeg returns.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _writeError ??= ex;
            return -1;
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
