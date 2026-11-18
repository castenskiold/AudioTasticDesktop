namespace AudioTastic.Core.Audio.Dsp;

/// <summary>UI-07: in-place radix-2 FFT (own code: no allocations, no extra dependency).</summary>
public static class Fft
{
    /// <summary>Forward transform of <paramref name="re"/>/<paramref name="im"/> (length a power of two).</summary>
    public static void Forward(Span<double> re, Span<double> im)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("Length must be a power of two and equal for both parts.");
        }
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + (len / 2);
                    var tr = (re[b] * cr) - (im[b] * ci);
                    var ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = ((cr * wr) - (ci * wi), (cr * wi) + (ci * wr));
                }
            }
        }
    }
}

/// <summary>
/// UI-07: turns the newest audio into log-spaced spectrum bars (0–1 over <see cref="FloorDb"/>..0 dBFS).
/// 2048-point FFT with a Hann window over the mono mix. Not thread-safe: one instance per view.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int Size = 2048;
    public const double FloorDb = -72;

    private readonly double[] _window = new double[Size];
    private readonly double[] _re = new double[Size];
    private readonly double[] _im = new double[Size];
    private readonly double[] _magnitude = new double[(Size / 2) + 1];

    public SpectrumAnalyzer()
    {
        for (var i = 0; i < Size; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)));
        }
    }

    /// <summary>
    /// Fills <paramref name="bars"/> from <paramref name="stereo"/> (interleaved, at least <see cref="Size"/> frames;
    /// the newest are used). Bars span 40 Hz to 16 kHz (or 0.45 × the sample rate), log-spaced.
    /// </summary>
    public void Analyze(ReadOnlySpan<float> stereo, int sampleRate, Span<float> bars)
    {
        var frames = stereo.Length / 2;
        var offset = Math.Max(0, frames - Size);
        for (var i = 0; i < Size; i++)
        {
            var f = offset + i;
            var mono = f < frames ? (stereo[f * 2] + stereo[(f * 2) + 1]) * 0.5 : 0;
            _re[i] = mono * _window[i];
            _im[i] = 0;
        }
        Fft.Forward(_re, _im);
        // Hann window coherent gain is 0.5: a full-scale sine reads 0 dB.
        var scale = 2.0 / (Size * 0.5);
        for (var k = 0; k < _magnitude.Length; k++)
        {
            _magnitude[k] = Math.Sqrt((_re[k] * _re[k]) + (_im[k] * _im[k])) * scale;
        }
        var low = 40.0;
        var high = Math.Min(16000, 0.45 * sampleRate);
        var binHz = sampleRate / (double)Size;
        for (var b = 0; b < bars.Length; b++)
        {
            var f0 = low * Math.Pow(high / low, b / (double)bars.Length);
            var f1 = low * Math.Pow(high / low, (b + 1) / (double)bars.Length);
            var k0 = Math.Clamp((int)Math.Floor(f0 / binHz), 0, _magnitude.Length - 1);
            var k1 = Math.Clamp((int)Math.Ceiling(f1 / binHz), k0, _magnitude.Length - 1);
            var peak = 0.0;
            for (var k = k0; k <= k1; k++)
            {
                peak = Math.Max(peak, _magnitude[k]);
            }
            var db = 20 * Math.Log10(Math.Max(peak, 1e-9));
            bars[b] = (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }
    }
}
