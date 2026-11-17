using System.Buffers.Binary;
using System.Text;

namespace AudioTastic.TestSupport;

/// <summary>
/// Minimal, independent PCM WAV reader used as the reference for decoder tests (it shares no code with FFmpeg).
/// Supports 16- and 24-bit integer PCM, converted to float the same way the decoder does (s / 2^(bits-1)).
/// </summary>
public static class ReferenceWav
{
    public static (int SampleRate, int Channels, float[] Samples) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }
        int rate = 0, channels = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            var body = bytes.AsSpan(pos + 8, Math.Min(size, bytes.Length - pos - 8));
            if (id == "fmt ")
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
            }
            else if (id == "data")
            {
                return (rate, channels, Convert(body, bits));
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk.");
    }

    private static float[] Convert(ReadOnlySpan<byte> data, int bits)
    {
        switch (bits)
        {
            case 16:
            {
                var result = new float[data.Length / 2];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                }
                return result;
            }
            case 24:
            {
                var result = new float[data.Length / 3];
                for (var i = 0; i < result.Length; i++)
                {
                    var v = data[i * 3] | (data[(i * 3) + 1] << 8) | ((sbyte)data[(i * 3) + 2] << 16);
                    // The decoder sees 24-bit PCM as s32 (value << 8) and divides by 2^31.
                    result[i] = (float)((v << 8) / 2147483648.0);
                }
                return result;
            }
            default:
                throw new NotSupportedException($"{bits}-bit WAV");
        }
    }
}
