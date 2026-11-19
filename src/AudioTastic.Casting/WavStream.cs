using System.Buffers.Binary;
using AudioTastic.Core.Audio;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Casting;

/// <summary>
/// CN-04: a decoded track as a 16-bit PCM WAV file (stereo, or mono for mono sources) that can be read from any
/// position. Every cast device plays WAV, no encoder is needed, and because the size is known in advance the server
/// can answer range requests (seeking on the device) by seeking the decoder. Surround is mixed down to stereo
/// (ChannelMapper). Needs a source with a known length that can seek.
/// </summary>
public sealed class WavStream : Stream
{
    private const int HeaderSize = 44;
    private const int ChunkFrames = 4096;

    private readonly IAudioSource _source;
    private readonly int _channels;
    private readonly int _sourceChannels;
    private readonly long _frames;
    private readonly byte[] _header;
    private readonly float[] _decoded;
    private readonly float[] _mapped;
    private readonly byte[] _pcm;
    private long _position;
    private long _pcmStart = -1; // byte offset (in the data chunk) of _pcm[0]; -1 = nothing buffered
    private int _pcmLength;

    /// <param name="source">The decoder; disposed with this stream.</param>
    public WavStream(IAudioSource source)
    {
        _source = source;
        _frames = source.LengthFrames ?? throw new ArgumentException("The source has no known length.", nameof(source));
        _sourceChannels = source.Format.Channels;
        _channels = _sourceChannels == 1 ? 1 : 2;
        _header = Header(source.Format.SampleRate, _channels, DataSize);
        _decoded = new float[ChunkFrames * _sourceChannels];
        _mapped = new float[ChunkFrames * _channels];
        _pcm = new byte[ChunkFrames * _channels * 2];
    }

    private long BlockAlign => _channels * 2;

    private long DataSize => _frames * BlockAlign;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => HeaderSize + DataSize;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    /// <summary>The 44-byte RIFF/WAVE header for 16-bit PCM.</summary>
    public static byte[] Header(int sampleRate, int channels, long dataSize)
    {
        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)Math.Min(uint.MaxValue, dataSize + 36));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)Math.Min(uint.MaxValue, dataSize));
        return header;
    }

    /// <summary>A float sample as 16-bit PCM (rounded, clipped).</summary>
    public static short ToPcm16(float sample) => (short)Math.Clamp(MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length && _position < Length)
        {
            if (_position < HeaderSize)
            {
                var n = (int)Math.Min(HeaderSize - _position, buffer.Length - written);
                _header.AsSpan((int)_position, n).CopyTo(buffer[written..]);
                written += n;
                _position += n;
                continue;
            }
            var dataOffset = _position - HeaderSize;
            if (_pcmStart < 0 || dataOffset < _pcmStart || dataOffset >= _pcmStart + _pcmLength)
            {
                if (!Fill(dataOffset))
                {
                    break; // the decoder ended early: the rest stays unread
                }
            }
            var inBuffer = (int)(dataOffset - _pcmStart);
            var take = Math.Min(_pcmLength - inBuffer, buffer.Length - written);
            _pcm.AsSpan(inBuffer, take).CopyTo(buffer[written..]);
            written += take;
            _position += take;
        }
        return written;
    }

    /// <summary>Decodes the chunk that holds <paramref name="dataOffset"/>; seeks the decoder when needed.</summary>
    private bool Fill(long dataOffset)
    {
        var frame = dataOffset / BlockAlign;
        if (frame != _source.PositionFrames)
        {
            if (!_source.CanSeek)
            {
                throw new NotSupportedException("The source cannot seek.");
            }
            _source.Seek(frame);
        }
        var want = (int)Math.Min(ChunkFrames, _frames - frame);
        var got = 0;
        while (got < want)
        {
            var n = _source.Read(_decoded.AsSpan(got * _sourceChannels, (want - got) * _sourceChannels));
            if (n == 0)
            {
                break;
            }
            got += n;
        }
        if (got == 0)
        {
            return false;
        }
        if (_sourceChannels == _channels)
        {
            _decoded.AsSpan(0, got * _channels).CopyTo(_mapped);
        }
        else
        {
            ChannelMapper.Map(_decoded, _sourceChannels, _mapped, _channels, got);
        }
        for (var i = 0; i < got * _channels; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_pcm.AsSpan(i * 2), ToPcm16(_mapped[i]));
        }
        _pcmStart = frame * BlockAlign;
        _pcmLength = got * (int)BlockAlign;
        return true;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
