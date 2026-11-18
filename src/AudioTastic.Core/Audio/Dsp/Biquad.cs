using System.Numerics;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// EQ-01 / EQ-04: second-order filter coefficients (normalised, a0 = 1) from the RBJ Audio EQ Cookbook, and their
/// exact magnitude response (used for the drawn curve, automatic headroom and the tests).
/// </summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly Biquad Identity = new(1, 0, 0, 0, 0);

    /// <summary>Designs a filter. Frequencies at or above 0.49 × the sample rate give the identity (no effect).</summary>
    public static Biquad Design(FilterType type, double frequency, double gainDb, double q, int sampleRate)
    {
        if (frequency <= 0 || frequency >= 0.49 * sampleRate || q <= 0)
        {
            return Identity;
        }
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        var a = Math.Pow(10, gainDb / 40);
        var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case FilterType.Peak:
                b0 = 1 + (alpha * a); b1 = -2 * cos; b2 = 1 - (alpha * a);
                a0 = 1 + (alpha / a); a1 = -2 * cos; a2 = 1 - (alpha / a);
                break;
            case FilterType.LowShelf:
                b0 = a * ((a + 1) - ((a - 1) * cos) + sqrtA2Alpha);
                b1 = 2 * a * ((a - 1) - ((a + 1) * cos));
                b2 = a * ((a + 1) - ((a - 1) * cos) - sqrtA2Alpha);
                a0 = (a + 1) + ((a - 1) * cos) + sqrtA2Alpha;
                a1 = -2 * ((a - 1) + ((a + 1) * cos));
                a2 = (a + 1) + ((a - 1) * cos) - sqrtA2Alpha;
                break;
            case FilterType.HighShelf:
                b0 = a * ((a + 1) + ((a - 1) * cos) + sqrtA2Alpha);
                b1 = -2 * a * ((a - 1) + ((a + 1) * cos));
                b2 = a * ((a + 1) + ((a - 1) * cos) - sqrtA2Alpha);
                a0 = (a + 1) - ((a - 1) * cos) + sqrtA2Alpha;
                a1 = 2 * ((a - 1) - ((a + 1) * cos));
                a2 = (a + 1) - ((a - 1) * cos) - sqrtA2Alpha;
                break;
            case FilterType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            default:
                return Identity;
        }
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Magnitude in dB at <paramref name="frequency"/>.</summary>
    public double MagnitudeDb(double frequency, int sampleRate)
    {
        var w = 2 * Math.PI * frequency / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var h = (B0 + (B1 * z1) + (B2 * z2)) / (1 + (A1 * z1) + (A2 * z2));
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-12));
    }
}

/// <summary>
/// EQ-01 / EQ-04: an immutable equalizer design for one sample rate: the band filters plus the overall gain
/// (preamp minus automatic headroom). Built on the control thread and published to the audio thread as a whole.
/// </summary>
public sealed class EqualizerDesign
{
    public const int MaxBands = 10;

    /// <summary>EQ-01: bandwidth of the graphic bands (about 1.2 octaves).</summary>
    public const double GraphicQ = 1.2;

    private const int CurvePoints = 256;

    private EqualizerDesign(Biquad[] bands, double gainDb, double headroomDb, int sampleRate)
    {
        Bands = bands;
        GainDb = gainDb;
        HeadroomDb = headroomDb;
        Gain = (float)Math.Pow(10, gainDb / 20);
        SampleRate = sampleRate;
    }

    public IReadOnlyList<Biquad> Bands { get; }

    /// <summary>Overall gain in dB: preamp minus <see cref="HeadroomDb"/>.</summary>
    public double GainDb { get; }

    /// <summary>Automatic headroom: how far the response would rise above 0 dB (the overall gain is lowered by this).</summary>
    public double HeadroomDb { get; }

    public float Gain { get; }
    public int SampleRate { get; }

    public static EqualizerDesign Create(EqualizerSettings settings, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Biquad[] bands;
        if (settings.Mode == EqualizerMode.Graphic)
        {
            bands = new Biquad[EqualizerSettings.GraphicFrequencies.Length];
            for (var i = 0; i < bands.Length; i++)
            {
                var gain = i < settings.Bands.Length ? Math.Clamp(settings.Bands[i], -12, 12) : 0;
                bands[i] = Math.Abs(gain) < 1e-9
                    ? Biquad.Identity
                    : Biquad.Design(FilterType.Peak, EqualizerSettings.GraphicFrequencies[i], gain, GraphicQ, sampleRate);
            }
        }
        else
        {
            bands = [.. settings.Parametric.Where(b => b.Enabled).Take(MaxBands).Select(b =>
                Biquad.Design(b.Type, Math.Clamp(b.Frequency, 20, 20000), Math.Clamp(b.GainDb, -24, 24), Math.Clamp(b.Q, 0.1, 10), sampleRate))];
        }
        var preamp = Math.Clamp(settings.PreampDb, -12, 12);
        // Automatic headroom: lower the overall gain by how far the curve (with the preamp) would rise above 0 dB.
        var peak = double.NegativeInfinity;
        foreach (var f in CurveFrequencies(sampleRate))
        {
            peak = Math.Max(peak, preamp + bands.Sum(b => b.MagnitudeDb(f, sampleRate)));
        }
        var headroom = Math.Max(0, peak);
        return new EqualizerDesign(bands, preamp - headroom, headroom, sampleRate);
    }

    /// <summary>The response in dB at <paramref name="frequency"/>, including the overall gain.</summary>
    public double ResponseDb(double frequency) => GainDb + Bands.Sum(b => b.MagnitudeDb(frequency, SampleRate));

    /// <summary>Log-spaced frequencies from 20 Hz to 20 kHz (or 0.45 × the sample rate).</summary>
    public static IEnumerable<double> CurveFrequencies(int sampleRate, int points = CurvePoints)
    {
        var top = Math.Min(20000, 0.45 * sampleRate);
        for (var i = 0; i < points; i++)
        {
            yield return 20 * Math.Pow(top / 20, i / (double)(points - 1));
        }
    }
}
