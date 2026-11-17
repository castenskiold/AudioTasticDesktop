using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-01: decodes one audio file with FFmpeg (avformat demuxing, avcodec decoding) into interleaved float.
/// <list type="bullet">
/// <item>Gapless: FFmpeg applies the encoder delay/padding signalled by the container (LAME/Xing header for MP3,
/// edit lists for AAC in MP4) through skip-samples side data, so the first sample returned is the first real sample.</item>
/// <item>Seeking: <c>av_seek_frame</c> to a point before the target (with pre-roll for lossy codecs), then decode and
/// discard up to the exact target frame. Positions come from frame timestamps relative to the first decoded frame.</item>
/// </list>
/// Not thread-safe; used from one decoder thread.
/// </summary>
public sealed unsafe class FfmpegAudioSource : IAudioSource
{
    /// <summary>Decode this much audio before a seek target so lossy decoders settle (MDCT overlap, MP3 bit reservoir).</summary>
    private const double SeekPrerollSeconds = 0.1;

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly int _channels;
    private readonly int _sampleRate;

    private float[] _pending = new float[8192];
    private int _pendingOffset;   // in samples (not frames)
    private int _pendingCount;    // in samples
    private long _position;
    private long _originPts = ffmpeg.AV_NOPTS_VALUE;
    private long _firstPacketPts = ffmpeg.AV_NOPTS_VALUE; // earliest seekable point (AAC priming packets are negative)
    private bool _demuxEnded;
    private bool _decoderEnded;
    private long? _length;
    private bool _trimToLength;

    private FfmpegAudioSource(string path)
    {
        FfmpegLibrary.EnsureLoaded();
        Path = path;
        try
        {
            AVFormatContext* format = null;
            Check(ffmpeg.avformat_open_input(&format, path, null, null), "open");
            _format = format;
            Check(ffmpeg.avformat_find_stream_info(_format, null), "read stream info");

            AVCodec* decoder = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (_streamIndex < 0 || decoder == null)
            {
                throw new AudioSourceException($"No playable audio stream in '{path}'.");
            }
            var stream = _format->streams[_streamIndex];
            _timeBase = stream->time_base;

            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "configure decoder");
            _codec->pkt_timebase = stream->time_base;
            Check(ffmpeg.avcodec_open2(_codec, decoder, null), "open decoder");

            _channels = _codec->ch_layout.nb_channels;
            _sampleRate = _codec->sample_rate;
            if (_channels < 1 || _sampleRate < 1)
            {
                throw new AudioSourceException($"Unsupported audio parameters in '{path}' ({_channels} ch, {_sampleRate} Hz).");
            }

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();

            Format = new AudioFormat(_sampleRate, _channels);
            Info = BuildInfo(stream);
            Tags = ReadTags(_format->metadata, stream->metadata);
            _length = ReadLength(stream);
            // Gapless end: FFmpeg removes encoder delay but not always the end padding (AAC in MP4 keeps it).
            // When the container states an exact duration (edit list, LAME/Xing header), stop there.
            _trimToLength = _length is not null && !Info.IsLossless &&
                _format->duration_estimation_method == AVDurationEstimationMethod.AVFMT_DURATION_FROM_STREAM;

            // Decode the first frame now: its timestamp is the origin for all positions (after any encoder delay).
            if (FillPending(out var firstPts))
            {
                _originPts = firstPts;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Opens <paramref name="path"/>; throws <see cref="AudioSourceException"/> on failure.</summary>
    public static FfmpegAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FfmpegAudioSource(path);
    }

    public string Path { get; }
    public AudioStreamInfo Info { get; }
    public AudioFormat Format { get; }
    public long? LengthFrames => _length;
    public long PositionFrames => _position;
    public bool CanSeek => _format != null && (_format->pb == null || _format->pb->seekable != 0);
    public IReadOnlyDictionary<string, string> Tags { get; }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        if (buffer.Length % _channels != 0)
        {
            throw new ArgumentException($"Buffer length must be a multiple of the channel count ({_channels}).", nameof(buffer));
        }
        if (_trimToLength && _length is long length)
        {
            var remaining = Math.Max(0, length - _position) * _channels;
            if (remaining < buffer.Length)
            {
                buffer = buffer[..(int)remaining];
            }
        }
        var written = 0;
        while (written < buffer.Length)
        {
            if (_pendingCount == 0 && !FillPending(out _))
            {
                break;
            }
            var n = Math.Min(_pendingCount, buffer.Length - written);
            _pending.AsSpan(_pendingOffset, n).CopyTo(buffer[written..]);
            _pendingOffset += n;
            _pendingCount -= n;
            written += n;
        }
        var frames = written / _channels;
        _position += frames;
        if (frames == 0 && _decoderEnded && !_trimToLength)
        {
            _length = _position; // exact length is known once the end has been decoded
        }
        return frames;
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("This source cannot seek.");
        }
        var target = Math.Max(0, frame);
        var preroll = (long)(SeekPrerollSeconds * _sampleRate);
        // Widen the pre-roll on each retry; the last attempt starts before the first packet. The start may be
        // negative on purpose: codecs with priming packets (AAC: pts -1024) must decode them to settle.
        for (var attempt = 0; attempt <= 4; attempt++)
        {
            var start = attempt < 4 ? target - (preroll << (2 * attempt)) : StartOfStream;
            if (SeekAndDecodeTo(start, target))
            {
                return;
            }
        }
        // Timestamps unusable: decode from the start and count samples.
        SeekByCounting(target);
    }

    /// <summary>Demuxers without an index (WavPack, ...) cannot seek before their first packet.</summary>
    private long ClampToFirstPacket(long ts) =>
        _firstPacketPts == ffmpeg.AV_NOPTS_VALUE ? ts : Math.Max(ts, _firstPacketPts);

    /// <summary>A start offset (in frames) safely before any priming packets.</summary>
    private long StartOfStream => -10L * _sampleRate;

    public void Dispose()
    {
        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
        if (_packet != null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
        if (_codec != null)
        {
            var c = _codec;
            ffmpeg.avcodec_free_context(&c);
            _codec = null;
        }
        if (_format != null)
        {
            var f = _format;
            ffmpeg.avformat_close_input(&f);
            _format = null;
        }
    }

    /// <summary>Seeks the demuxer to <paramref name="start"/> and decodes up to <paramref name="target"/>. False when the landing point is after the target.</summary>
    private bool SeekAndDecodeTo(long start, long target)
    {
        var ts = _originPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _originPts;
        ts += ffmpeg.av_rescale_q(start, new AVRational { num = 1, den = _sampleRate }, _timeBase);
        ts = ClampToFirstPacket(ts);
        if (ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0 &&
            ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, ts, ts, 0) < 0)
        {
            return false;
        }
        ResetDecoder();

        if (!FillPending(out var pts))
        {
            // Seeked past the end.
            _position = Math.Min(target, _length ?? target);
            return true;
        }
        if (pts == ffmpeg.AV_NOPTS_VALUE || _originPts == ffmpeg.AV_NOPTS_VALUE)
        {
            return false;
        }
        var index = ffmpeg.av_rescale_q(pts - _originPts, _timeBase, new AVRational { num = 1, den = _sampleRate });
        if (index > target)
        {
            return false;
        }
        DiscardUntil(index, target);
        return true;
    }

    private void SeekByCounting(long target)
    {
        var ts = (_originPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _originPts) +
            ffmpeg.av_rescale_q(StartOfStream, new AVRational { num = 1, den = _sampleRate }, _timeBase);
        ts = ClampToFirstPacket(ts);
        if (ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
        {
            Check(ffmpeg.avformat_seek_file(_format, -1, long.MinValue, 0, 0, ffmpeg.AVSEEK_FLAG_BYTE), "seek");
        }
        ResetDecoder();
        if (!FillPending(out _))
        {
            _position = 0;
            return;
        }
        DiscardUntil(0, target);
    }

    /// <summary>The pending buffer holds a frame starting at <paramref name="index"/>; drop samples until <paramref name="target"/>.</summary>
    private void DiscardUntil(long index, long target)
    {
        while (true)
        {
            var framesInPending = _pendingCount / _channels;
            if (index + framesInPending > target)
            {
                var skip = (int)(target - index) * _channels;
                _pendingOffset += skip;
                _pendingCount -= skip;
                _position = target;
                return;
            }
            index += framesInPending;
            _pendingCount = 0;
            if (!FillPending(out _))
            {
                _position = index; // target is past the end
                _length = index;
                return;
            }
        }
    }

    private void ResetDecoder()
    {
        ffmpeg.avcodec_flush_buffers(_codec);
        _demuxEnded = false;
        _decoderEnded = false;
        _pendingOffset = 0;
        _pendingCount = 0;
    }

    /// <summary>Decodes the next frame into the pending buffer. False at end of stream.</summary>
    private bool FillPending(out long pts)
    {
        while (true)
        {
            if (!DecodeNextFrame())
            {
                pts = ffmpeg.AV_NOPTS_VALUE;
                return false;
            }
            var samples = _frame->nb_samples * _channels;
            if (samples == 0)
            {
                continue;
            }
            if (_pending.Length < samples)
            {
                _pending = new float[samples];
            }
            ConvertFrame(_frame, _channels, _pending);
            _pendingOffset = 0;
            _pendingCount = samples;
            pts = _frame->best_effort_timestamp;
            return true;
        }
    }

    private bool DecodeNextFrame()
    {
        while (true)
        {
            var r = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (r == 0)
            {
                return true;
            }
            if (r == ffmpeg.AVERROR_EOF)
            {
                _decoderEnded = true;
                return false;
            }
            if (r != ErrorAgain)
            {
                throw Error(r, "decode");
            }
            if (_demuxEnded)
            {
                _decoderEnded = true;
                return false;
            }

            r = ffmpeg.av_read_frame(_format, _packet);
            if (r < 0)
            {
                if (r != ffmpeg.AVERROR_EOF && (_format->pb == null || _format->pb->eof_reached == 0))
                {
                    throw Error(r, "read");
                }
                _demuxEnded = true;
                ffmpeg.avcodec_send_packet(_codec, null); // enter draining mode
                continue;
            }
            if (_packet->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }
            if (_firstPacketPts == ffmpeg.AV_NOPTS_VALUE)
            {
                _firstPacketPts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : _packet->dts;
            }
            r = ffmpeg.avcodec_send_packet(_codec, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (r < 0 && r != ErrorAgain && r != ffmpeg.AVERROR_INVALIDDATA)
            {
                throw Error(r, "decode");
            }
            // AVERROR_INVALIDDATA: a damaged packet is skipped, playback continues.
        }
    }

    /// <summary>Converts any FFmpeg sample format (packed or planar) to interleaved float.</summary>
    internal static void ConvertFrame(AVFrame* frame, int channels, float[] destination)
    {
        var count = frame->nb_samples;
        var format = (AVSampleFormat)frame->format;
        var planar = ffmpeg.av_sample_fmt_is_planar(format) != 0;
        var data = frame->extended_data;
        fixed (float* dst = destination)
        {
            for (var c = 0; c < (planar ? channels : 1); c++)
            {
                var src = data[c];
                var stride = planar ? channels : 1;
                var n = planar ? count : count * channels;
                var d = dst + c;
                switch (ffmpeg.av_get_packed_sample_fmt(format))
                {
                    case AVSampleFormat.AV_SAMPLE_FMT_U8:
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = (src[i] - 128) / 128f;
                        }
                        break;
                    case AVSampleFormat.AV_SAMPLE_FMT_S16:
                        var s16 = (short*)src;
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = s16[i] / 32768f;
                        }
                        break;
                    case AVSampleFormat.AV_SAMPLE_FMT_S32:
                        var s32 = (int*)src;
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = (float)(s32[i] / 2147483648.0);
                        }
                        break;
                    case AVSampleFormat.AV_SAMPLE_FMT_S64:
                        var s64 = (long*)src;
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = (float)(s64[i] / 9223372036854775808.0);
                        }
                        break;
                    case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                        var f32 = (float*)src;
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = f32[i];
                        }
                        break;
                    case AVSampleFormat.AV_SAMPLE_FMT_DBL:
                        var f64 = (double*)src;
                        for (var i = 0; i < n; i++)
                        {
                            d[i * stride] = (float)f64[i];
                        }
                        break;
                    default:
                        throw new AudioSourceException($"Unsupported sample format {format}.");
                }
            }
        }
    }

    private AudioStreamInfo BuildInfo(AVStream* stream)
    {
        var par = stream->codecpar;
        var descriptor = ffmpeg.avcodec_descriptor_get(par->codec_id);
        var lossless = descriptor != null && (descriptor->props & ffmpeg.AV_CODEC_PROP_LOSSLESS) != 0;
        var bits = par->bits_per_raw_sample;
        if (bits == 0 && lossless)
        {
            bits = ffmpeg.av_get_bits_per_sample(par->codec_id);
        }
        if (bits == 0 && lossless)
        {
            bits = ffmpeg.av_get_bytes_per_sample(_codec->sample_fmt) * 8;
        }
        var bitrate = par->bit_rate > 0 ? par->bit_rate : _format->bit_rate;
        return new AudioStreamInfo(
            ffmpeg.avcodec_get_name(par->codec_id),
            _sampleRate,
            _channels,
            lossless ? bits : 0,
            Math.Max(0, bitrate),
            lossless);
    }

    private long? ReadLength(AVStream* stream)
    {
        var rate = new AVRational { num = 1, den = _sampleRate };
        if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            return ffmpeg.av_rescale_q(stream->duration, _timeBase, rate);
        }
        if (_format->duration > 0 && _format->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            return ffmpeg.av_rescale_q(_format->duration, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, rate);
        }
        return null;
    }

    private static Dictionary<string, string> ReadTags(params AVDictionary*[] dictionaries)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dictionary in dictionaries)
        {
            AVDictionaryEntry* entry = null;
            while ((entry = ffmpeg.av_dict_get(dictionary, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Marshal.PtrToStringUTF8((IntPtr)entry->key);
                var value = Marshal.PtrToStringUTF8((IntPtr)entry->value);
                if (key is not null && value is not null)
                {
                    tags.TryAdd(key, value);
                }
            }
        }
        return tags;
    }

    /// <summary>AVERROR(EAGAIN); EAGAIN is 35 on macOS and 11 elsewhere.</summary>
    private static readonly int ErrorAgain = -(OperatingSystem.IsMacOS() ? 35 : 11);

    private void Check(int result, string action)
    {
        if (result < 0)
        {
            throw Error(result, action);
        }
    }

    private AudioSourceException Error(int result, string action)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(result, buffer, size);
        var message = Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {result}";
        return new AudioSourceException($"FFmpeg could not {action} '{Path}': {message}");
    }
}
