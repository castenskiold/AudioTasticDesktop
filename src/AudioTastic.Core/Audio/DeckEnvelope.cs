namespace AudioTastic.Core.Audio;

/// <summary>
/// XF-01 / XF-02 (Anders, 18 Nov 2026): the incoming deck's bass comes in gradually. A low shelf (below
/// <see cref="Frequency"/>) at <see cref="FromDb"/> before <see cref="Start"/> opens linearly in dB to 0 dB over
/// <see cref="Length"/> frames (the deck's own output frames, like <see cref="DeckEnvelope"/>).
/// </summary>
public sealed record BassRamp(long Start, long Length, double FromDb = -24, double Frequency = 200)
{
    public long End => Start + Math.Max(0, Length);

    /// <summary>Shelf gain (dB) at <paramref name="frame"/>.</summary>
    public double GainDbAt(long frame)
    {
        if (frame <= Start)
        {
            return FromDb;
        }
        return frame >= End ? 0 : FromDb * (1 - ((frame - Start) / (double)Length));
    }
}

/// <summary>XF-01 / XF-02: the shape of a deck's crossfade envelope.</summary>
public enum EnvelopeShape
{
    /// <summary>0 → 1 on an equal-power (sine) curve.</summary>
    FadeIn,

    /// <summary>1 → 0 on an equal-power (cosine) curve.</summary>
    FadeOut,

    /// <summary>1 → 0 linear in dB (to −60 dB, then silence).</summary>
    FadeOutDb,
}

/// <summary>
/// XF-01 / XF-02: a gain curve for one deck, in the deck's own output frames (frames it has handed to the mixer since
/// it was loaded, which does not depend on its speed). Before <see cref="Start"/> the gain is the curve's start value,
/// after <see cref="Start"/> + <see cref="Length"/> its end value. Immutable: published to the audio thread as a whole.
/// </summary>
public sealed record DeckEnvelope(long Start, long Length, EnvelopeShape Shape)
{
    public long End => Start + Math.Max(0, Length);

    /// <summary>True when the deck is silent from <see cref="End"/> on.</summary>
    public bool EndsSilent => Shape is EnvelopeShape.FadeOut or EnvelopeShape.FadeOutDb;

    public float GainAt(long frame)
    {
        if (frame < Start)
        {
            return Shape == EnvelopeShape.FadeIn ? 0f : 1f;
        }
        if (frame >= End)
        {
            return Shape == EnvelopeShape.FadeIn ? 1f : 0f;
        }
        var x = (frame - Start) / (double)Length;
        return Shape switch
        {
            EnvelopeShape.FadeIn => (float)Math.Sin(x * Math.PI / 2),
            EnvelopeShape.FadeOut => (float)Math.Cos(x * Math.PI / 2),
            _ => (float)Math.Pow(10, -60 * x / 20),
        };
    }
}
