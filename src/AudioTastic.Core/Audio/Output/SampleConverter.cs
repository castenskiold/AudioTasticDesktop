using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Audio.Output;

/// <summary>
/// PB-05: converts the engine's float samples to the device's integer format, exactly inverse to how the decoders
/// make floats (integer / 2^(bits−1)), so an unprocessed 16/24-bit source reaches the device bit for bit.
/// Optional TPDF dither (±1 LSB triangular) is added before rounding when the bit depth is reduced.
/// Audio thread: no allocation; the dither state lives in the struct passed by reference.
/// </summary>
public static class SampleConverter
{
    public static int BytesPerSample(OutputSampleFormat format) => format switch
    {
        OutputSampleFormat.S16 => 2,
        OutputSampleFormat.S24 => 3,
        _ => 4,
    };

    public static int Bits(OutputSampleFormat format) => format switch
    {
        OutputSampleFormat.S16 => 16,
        OutputSampleFormat.S24 => 24,
        OutputSampleFormat.S32 => 32,
        _ => 0,
    };

    /// <summary>Writes <paramref name="source"/> to <paramref name="destination"/> in <paramref name="format"/>.</summary>
    public static void Convert(ReadOnlySpan<float> source, Span<byte> destination, OutputSampleFormat format, bool dither, ref DitherState state)
    {
        switch (format)
        {
            case OutputSampleFormat.S16:
                for (var i = 0; i < source.Length; i++)
                {
                    var v = Quantize(source[i] * 32768.0, dither, ref state, -32768, 32767);
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], (short)v);
                }
                break;
            case OutputSampleFormat.S24:
                for (var i = 0; i < source.Length; i++)
                {
                    var v = Quantize(source[i] * 8388608.0, dither, ref state, -8388608, 8388607);
                    var o = i * 3;
                    destination[o] = (byte)v;
                    destination[o + 1] = (byte)(v >> 8);
                    destination[o + 2] = (byte)(v >> 16);
                }
                break;
            case OutputSampleFormat.S32:
                for (var i = 0; i < source.Length; i++)
                {
                    // 32-bit needs no dither: a float has only 24 bits of precision.
                    var v = Quantize(source[i] * 2147483648.0, false, ref state, int.MinValue, int.MaxValue);
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)v);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Use the float buffer directly.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Quantize(double scaled, bool dither, ref DitherState state, long min, long max)
    {
        if (dither)
        {
            scaled += state.NextTriangular();
        }
        var v = (long)Math.Round(scaled, MidpointRounding.ToEven);
        return v < min ? min : v > max ? max : v;
    }
}

/// <summary>xorshift32 random source for TPDF dither (no allocation, one per output).</summary>
public struct DitherState
{
    private uint _state;

    public DitherState(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    /// <summary>Triangular noise in (−1, 1) LSB: the sum of two uniform values.</summary>
    public double NextTriangular() => Uniform() + Uniform() - 1.0;

    private double Uniform()
    {
        if (_state == 0)
        {
            _state = 0x9E3779B9u;
        }
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state / 4294967296.0;
    }
}
