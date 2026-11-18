using AudioTastic.Sdk.Audio;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-05: DSD over PCM (DoP 1.1). Reads the raw 1-bit DSD stream of a DSF or DFF file (no decoding) and packs 16 DSD
/// bits per channel into each 24-bit PCM frame, with the marker byte alternating 0x05 / 0xFA, at DSD rate / 16
/// (DSD64 → 176.4 kHz). The samples are returned as floats that convert back to the exact 24-bit words
/// (value / 2^23), so a bit-perfect exclusive output (24 or 32 bit, unity gain) delivers them unchanged to a DoP DAC.
/// </summary>
public sealed unsafe class DopSource : IAudioSource
{
    private const string Codec = "dop";

    private AVFormatContext* _format;
    private AVPacket* _packet;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly int _channels;
    private readonly bool _planar;
    private readonly bool _lsbFirst;
    private readonly int _bytesPerSecond; // per channel
    private readonly byte[][] _fifo;
    private int _fifoStart;
    private int _fifoCount;
    private long _position;
    private bool _ended;
    private long _discardBytes;

    private DopSource(string path)
    {
        FfmpegLibrary.EnsureLoaded();
        try
        {
            AVFormatContext* format = null;
            if (ffmpeg.avformat_open_input(&format, path, null, null) < 0)
            {
                throw new AudioSourceException($"Could not open '{path}' for DoP.");
            }
            _format = format;
            if (ffmpeg.avformat_find_stream_info(_format, null) < 0)
            {
                throw new AudioSourceException($"Could not read the stream info of '{path}'.");
            }
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_streamIndex < 0)
            {
                throw new AudioSourceException($"No audio stream in '{path}'.");
            }
            var stream = _format->streams[_streamIndex];
            var par = stream->codecpar;
            (_planar, _lsbFirst) = par->codec_id switch
            {
                AVCodecID.AV_CODEC_ID_DSD_LSBF => (false, true),
                AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR => (true, true),
                AVCodecID.AV_CODEC_ID_DSD_MSBF => (false, false),
                AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR => (true, false),
                _ => throw new AudioSourceException($"'{path}' is not a DSD file."),
            };
            _timeBase = stream->time_base;
            _channels = par->ch_layout.nb_channels;
            _bytesPerSecond = par->sample_rate; // FFmpeg's DSD rate is in bytes per second per channel (DSD rate / 8)
            if (_channels < 1 || _bytesPerSecond < 1)
            {
                throw new AudioSourceException($"Unsupported DSD stream in '{path}'.");
            }
            DsdRate = _bytesPerSecond * 8;
            Format = new AudioFormat(DsdRate / 16, _channels);
            Info = new AudioStreamInfo(Codec, DsdRate / 16, _channels, 24, (long)DsdRate * _channels, IsLossless: true);
            if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
            {
                LengthFrames = ffmpeg.av_rescale_q(stream->duration, _timeBase, new AVRational { num = 1, den = _bytesPerSecond }) / 2;
            }
            _packet = ffmpeg.av_packet_alloc();
            _fifo = new byte[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                _fifo[c] = new byte[1 << 16];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Opens a DSF/DFF file for DoP; throws <see cref="AudioSourceException"/> when it is not DSD.</summary>
    public static DopSource Open(string path) => new(path);

    /// <summary>The 1-bit rate (2,822,400 for DSD64).</summary>
    public int DsdRate { get; }

    public AudioStreamInfo Info { get; } = null!;
    public AudioFormat Format { get; }
    public long? LengthFrames { get; }
    public long PositionFrames => _position;
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / _channels;
        if (LengthFrames is long length)
        {
            frames = (int)Math.Min(frames, Math.Max(0, length - _position));
        }
        var written = 0;
        while (written < frames)
        {
            if (_fifoCount < 2 && !Fill())
            {
                break;
            }
            var available = Math.Min(frames - written, _fifoCount / 2);
            for (var f = 0; f < available; f++)
            {
                // DoP 1.1: marker 0x05 on even frames, 0xFA on odd ones (by absolute frame number).
                var marker = ((_position + written + f) & 1) == 0 ? 0x05 : 0xFA;
                for (var c = 0; c < _channels; c++)
                {
                    var i = _fifoStart + (f * 2);
                    var word = (marker << 16) | (_fifo[c][i] << 8) | _fifo[c][i + 1];
                    buffer[((written + f) * _channels) + c] = ((word << 8) >> 8) / 8388608f;
                }
            }
            _fifoStart += available * 2;
            _fifoCount -= available * 2;
            written += available;
        }
        _position += written;
        return written;
    }

    public void Seek(long frame)
    {
        var target = Math.Max(0, LengthFrames is long length ? Math.Min(frame, length) : frame);
        var targetByte = target * 2;
        var ts = ffmpeg.av_rescale_q(targetByte, new AVRational { num = 1, den = _bytesPerSecond }, _timeBase);
        if (ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
        {
            ffmpeg.av_seek_frame(_format, _streamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD | ffmpeg.AVSEEK_FLAG_BYTE);
        }
        _fifoStart = 0;
        _fifoCount = 0;
        _ended = false;
        _discardBytes = -targetByte; // resolved by the first packet's timestamp (see Fill)
        _position = target;
    }

    public void Dispose()
    {
        if (_packet != null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
        if (_format != null)
        {
            var f = _format;
            ffmpeg.avformat_close_input(&f);
            _format = null;
        }
    }

    /// <summary>Reads the next DSD packet into the per-channel byte queues (MSB first). False at the end.</summary>
    private bool Fill()
    {
        while (!_ended)
        {
            if (ffmpeg.av_read_frame(_format, _packet) < 0)
            {
                _ended = true;
                return false;
            }
            try
            {
                if (_packet->stream_index != _streamIndex || _packet->size == 0)
                {
                    continue;
                }
                var bytesPerChannel = _packet->size / _channels;
                var skip = 0;
                if (_discardBytes < 0)
                {
                    // After a seek: skip from the packet's first byte up to the target byte.
                    var packetByte = _packet->pts != ffmpeg.AV_NOPTS_VALUE
                        ? ffmpeg.av_rescale_q(_packet->pts, _timeBase, new AVRational { num = 1, den = _bytesPerSecond })
                        : 0;
                    var targetByte = -_discardBytes;
                    _discardBytes = 0;
                    skip = (int)Math.Clamp(targetByte - packetByte, 0, bytesPerChannel);
                    if (targetByte - packetByte > bytesPerChannel)
                    {
                        _discardBytes = -(targetByte); // still before the target: keep skipping
                        continue;
                    }
                }
                Compact(bytesPerChannel - skip);
                var data = _packet->data;
                for (var c = 0; c < _channels; c++)
                {
                    var fifo = _fifo[c];
                    var at = _fifoStart + _fifoCount;
                    for (var i = skip; i < bytesPerChannel; i++)
                    {
                        var b = _planar ? data[(c * bytesPerChannel) + i] : data[(i * _channels) + c];
                        fifo[at + i - skip] = _lsbFirst ? Reverse(b) : b;
                    }
                }
                _fifoCount += bytesPerChannel - skip;
                return true;
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
        return false;
    }

    private void Compact(int incoming)
    {
        if (_fifoStart > 0)
        {
            for (var c = 0; c < _channels; c++)
            {
                Array.Copy(_fifo[c], _fifoStart, _fifo[c], 0, _fifoCount);
            }
            _fifoStart = 0;
        }
        if (_fifoCount + incoming > _fifo[0].Length)
        {
            for (var c = 0; c < _channels; c++)
            {
                Array.Resize(ref _fifo[c], Math.Max(_fifoCount + incoming, _fifo[c].Length * 2));
            }
        }
    }

    private static byte Reverse(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        return (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
    }
}
