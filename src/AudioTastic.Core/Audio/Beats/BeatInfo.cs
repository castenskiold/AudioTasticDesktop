using AudioTastic.Core.Playlists;

namespace AudioTastic.Core.Audio.Beats;

/// <summary>A section with no drums or a clear drop in low-frequency energy (AN-02), in track seconds.</summary>
public readonly record struct BeatBreak(double Start, double End);

/// <summary>
/// AN-02: a track's tempo analysis: a constant-tempo beat grid (<see cref="Bpm"/>, <see cref="FirstBeat"/>), how well
/// the grid fits (<see cref="Confidence"/>), where phrases start (every 8 beats from <see cref="PhraseOffset"/>), where
/// the beat (the drums) starts and ends (<see cref="BeatStart"/>, <see cref="BeatEnd"/>; intro and outro lie outside),
/// and the breaks in between. Times are in track seconds (from the start of the file, or of the CUE track).
/// </summary>
public sealed record BeatInfo(double Bpm, double FirstBeat, double Confidence, int PhraseOffset, double Duration, IReadOnlyList<BeatBreak> Breaks,
    double BeatStart = 0, double BeatEnd = 0)
{
    private const byte FormatVersion = 1;

    /// <summary>Beats per phrase (XF-02 mixes on multiples of 8 beats).</summary>
    public const int PhraseBeats = 8;

    /// <summary>Seconds per beat.</summary>
    public double Period => 60.0 / Bpm;

    /// <summary>Time of beat <paramref name="index"/> (0 = <see cref="FirstBeat"/>).</summary>
    public double BeatTime(long index) => FirstBeat + (index * Period);

    /// <summary>Index of the first beat at or after <paramref name="time"/>.</summary>
    public long BeatAtOrAfter(double time) => (long)Math.Ceiling(((time - FirstBeat) / Period) - 1e-9);

    /// <summary>True when beat <paramref name="index"/> starts a phrase.</summary>
    public bool IsPhraseStart(long index) => ((index - PhraseOffset) % PhraseBeats + PhraseBeats) % PhraseBeats == 0;

    /// <summary>The first phrase-start beat at or after <paramref name="time"/>.</summary>
    public long PhraseAtOrAfter(double time)
    {
        var k = Math.Max(0, BeatAtOrAfter(time));
        while (!IsPhraseStart(k))
        {
            k++;
        }
        return k;
    }

    /// <summary>The last phrase-start beat at or before <paramref name="time"/> (null when there is none).</summary>
    public long? PhraseAtOrBefore(double time)
    {
        var k = (long)Math.Floor(((time - FirstBeat) / Period) + 1e-9);
        while (k >= 0 && !IsPhraseStart(k))
        {
            k--;
        }
        return k >= 0 ? k : null;
    }

    /// <summary>The first break starting at or after <paramref name="time"/>.</summary>
    public BeatBreak? BreakAfter(double time) => Breaks.Where(b => b.Start >= time).Select(b => (BeatBreak?)b).FirstOrDefault();

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(FormatVersion);
            w.Write(Bpm);
            w.Write(FirstBeat);
            w.Write(Confidence);
            w.Write(PhraseOffset);
            w.Write(Duration);
            w.Write(BeatStart);
            w.Write(BeatEnd);
            w.Write(Breaks.Count);
            foreach (var b in Breaks)
            {
                w.Write(b.Start);
                w.Write(b.End);
            }
        }
        return stream.ToArray();
    }

    public static BeatInfo? Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            if (r.ReadByte() != FormatVersion)
            {
                return null;
            }
            var bpm = r.ReadDouble();
            var first = r.ReadDouble();
            var confidence = r.ReadDouble();
            var phrase = r.ReadInt32();
            var duration = r.ReadDouble();
            var beatStart = r.ReadDouble();
            var beatEnd = r.ReadDouble();
            var breaks = new BeatBreak[r.ReadInt32()];
            for (var i = 0; i < breaks.Length; i++)
            {
                breaks[i] = new BeatBreak(r.ReadDouble(), r.ReadDouble());
            }
            return new BeatInfo(bpm, first, confidence, phrase, duration, breaks, beatStart, beatEnd);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}

/// <summary>
/// AN-02 / XF-02: beat analysis for the engine (implemented by the analysis service). <see cref="TryGet"/> returns the
/// cached result or null (then it asks for the analysis to run soon).
/// </summary>
public interface IBeatInfoProvider
{
    BeatInfo? TryGet(PlaylistEntry entry);
}
