using System.IO.Compression;

namespace AudioTastic.Analysis;

/// <summary>
/// CO-07: a track's waveform as min/max pairs per bucket and channel (values −1…1, stored as 16-bit, Deflate
/// compressed in the cache). Up to two channels are kept (more are folded into two).
/// </summary>
public sealed class Waveform
{
    private const byte FormatVersion = 1;

    public Waveform(int channels, float[][] min, float[][] max, TimeSpan duration)
    {
        Channels = channels;
        Min = min;
        Max = max;
        Duration = duration;
    }

    public int Channels { get; }

    /// <summary>Number of buckets (about 2,000 for a whole track).</summary>
    public int Buckets => Min[0].Length;

    /// <summary>Min[channel][bucket].</summary>
    public float[][] Min { get; }

    /// <summary>Max[channel][bucket].</summary>
    public float[][] Max { get; }

    public TimeSpan Duration { get; }

    public byte[] Serialize()
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(deflate))
        {
            w.Write(FormatVersion);
            w.Write((byte)Channels);
            w.Write(Buckets);
            w.Write(Duration.Ticks);
            for (var c = 0; c < Channels; c++)
            {
                for (var i = 0; i < Buckets; i++)
                {
                    w.Write(Quantize(Min[c][i]));
                    w.Write(Quantize(Max[c][i]));
                }
            }
        }
        return output.ToArray();
    }

    public static Waveform? Deserialize(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new DeflateStream(new MemoryStream(data), CompressionMode.Decompress));
            if (r.ReadByte() != FormatVersion)
            {
                return null;
            }
            var channels = r.ReadByte();
            var buckets = r.ReadInt32();
            var duration = TimeSpan.FromTicks(r.ReadInt64());
            var min = new float[channels][];
            var max = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                min[c] = new float[buckets];
                max[c] = new float[buckets];
                for (var i = 0; i < buckets; i++)
                {
                    min[c][i] = r.ReadInt16() / 32767f;
                    max[c][i] = r.ReadInt16() / 32767f;
                }
            }
            return new Waveform(channels, min, max, duration);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static short Quantize(float v) => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767);
}

/// <summary>PB-03: loudness of a track: integrated loudness, sample peak and the 400 ms block energies.</summary>
public sealed class LoudnessResult
{
    private const byte FormatVersion = 1;

    /// <summary>ReplayGain 2.0 reference loudness.</summary>
    public const double ReferenceLufs = -18.0;

    public LoudnessResult(double integratedLufs, float samplePeak, float[] blockEnergies)
    {
        IntegratedLufs = integratedLufs;
        SamplePeak = samplePeak;
        BlockEnergies = blockEnergies;
    }

    /// <summary>Integrated loudness (LUFS); −∞ for silence.</summary>
    public double IntegratedLufs { get; }

    public float SamplePeak { get; }

    public float[] BlockEnergies { get; }

    /// <summary>ReplayGain 2.0 track gain (dB), 0 for silence.</summary>
    public double GainDb => double.IsFinite(IntegratedLufs) ? ReferenceLufs - IntegratedLufs : 0;

    /// <summary>Album gain and peak over several tracks' blocks.</summary>
    public static (double GainDb, float Peak) Album(IReadOnlyCollection<LoudnessResult> tracks)
    {
        var lufs = LoudnessMeter.Integrated(tracks.SelectMany(t => t.BlockEnergies));
        return (double.IsFinite(lufs) ? ReferenceLufs - lufs : 0, tracks.Count == 0 ? 0 : tracks.Max(t => t.SamplePeak));
    }

    public byte[] Serialize()
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(deflate))
        {
            w.Write(FormatVersion);
            w.Write(IntegratedLufs);
            w.Write(SamplePeak);
            w.Write(BlockEnergies.Length);
            foreach (var e in BlockEnergies)
            {
                w.Write(e);
            }
        }
        return output.ToArray();
    }

    public static LoudnessResult? Deserialize(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new DeflateStream(new MemoryStream(data), CompressionMode.Decompress));
            if (r.ReadByte() != FormatVersion)
            {
                return null;
            }
            var lufs = r.ReadDouble();
            var peak = r.ReadSingle();
            var blocks = new float[r.ReadInt32()];
            for (var i = 0; i < blocks.Length; i++)
            {
                blocks[i] = r.ReadSingle();
            }
            return new LoudnessResult(lufs, peak, blocks);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
