namespace AudioTastic.Analysis;

/// <summary>
/// PB-03: integrated loudness per ITU-R BS.1770-4 / EBU R128 (own implementation; no libebur128 needed).
/// <list type="bullet">
/// <item>K-weighting: high-shelf pre-filter and RLB high-pass, recomputed for any sample rate (the coefficient
/// formulas used by libebur128).</item>
/// <item>400 ms blocks with 75 % overlap (100 ms hop); absolute gate −70 LUFS, relative gate −10 LU.</item>
/// <item>Channel weights: 1.0 for front channels, 1.41 for surrounds, LFE excluded (FFmpeg 5.1 order FL FR FC LFE BL BR).</item>
/// </list>
/// The energy of every block is kept, so an album's loudness can be computed exactly from its tracks' blocks.
/// </summary>
public sealed class LoudnessMeter
{
    private const double BlockSeconds = 0.4;
    private const double HopSeconds = 0.1;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;

    private readonly int _channels;
    private readonly double[] _weights;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly int _hopFrames;
    private readonly double[] _hopEnergy = new double[4]; // ring of the last four 100 ms sub-blocks (weighted)
    private int _hopsSeen;
    private int _framesInHop;
    private double _currentHop;
    private readonly List<float> _blocks = [];

    public LoudnessMeter(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _weights = ChannelWeights(channels);
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        var (shelf, highPass) = KWeighting(sampleRate);
        for (var c = 0; c < channels; c++)
        {
            _shelf[c] = shelf;
            _highPass[c] = highPass;
        }
        _hopFrames = (int)Math.Round(sampleRate * HopSeconds);
    }

    /// <summary>Largest absolute sample value seen.</summary>
    public float SamplePeak { get; private set; }

    /// <summary>Mean-square energy of each complete 400 ms block (K-weighted, channel-weighted).</summary>
    public IReadOnlyList<float> BlockEnergies => _blocks;

    /// <summary>Feeds interleaved samples.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        var peak = SamplePeak;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0.0;
            for (var c = 0; c < _channels; c++)
            {
                var x = interleaved[(f * _channels) + c];
                var a = Math.Abs(x);
                if (a > peak)
                {
                    peak = a;
                }
                if (_weights[c] == 0)
                {
                    continue;
                }
                var y = _highPass[c].Process(_shelf[c].Process(x));
                sum += _weights[c] * y * y;
            }
            _currentHop += sum;
            if (++_framesInHop == _hopFrames)
            {
                CompleteHop();
            }
        }
        SamplePeak = peak;
    }

    /// <summary>Integrated loudness (LUFS) of what was fed; −∞ when everything is below the absolute gate.</summary>
    public double IntegratedLoudness => Integrated(_blocks);

    /// <summary>Integrated loudness over the blocks of several tracks (album loudness).</summary>
    public static double Integrated(IEnumerable<float> blockEnergies)
    {
        var gated = 0.0;
        var count = 0;
        var blocks = blockEnergies as IReadOnlyList<float> ?? blockEnergies.ToList();
        foreach (var e in blocks)
        {
            if (Loudness(e) > AbsoluteGate)
            {
                gated += e;
                count++;
            }
        }
        if (count == 0)
        {
            return double.NegativeInfinity;
        }
        var threshold = Loudness(gated / count) + RelativeGate;
        var sum = 0.0;
        count = 0;
        foreach (var e in blocks)
        {
            var l = Loudness(e);
            if (l > AbsoluteGate && l > threshold)
            {
                sum += e;
                count++;
            }
        }
        return count == 0 ? double.NegativeInfinity : Loudness(sum / count);
    }

    /// <summary>BS.1770 loudness of a mean-square energy.</summary>
    public static double Loudness(double energy) => energy <= 0 ? double.NegativeInfinity : -0.691 + (10 * Math.Log10(energy));

    private void CompleteHop()
    {
        _hopEnergy[_hopsSeen % 4] = _currentHop / _hopFrames;
        _hopsSeen++;
        _currentHop = 0;
        _framesInHop = 0;
        if (_hopsSeen >= 4)
        {
            // A 400 ms block is the mean of its four 100 ms sub-blocks.
            _blocks.Add((float)((_hopEnergy[0] + _hopEnergy[1] + _hopEnergy[2] + _hopEnergy[3]) / 4));
        }
    }

    private static double[] ChannelWeights(int channels)
    {
        var w = Enumerable.Repeat(1.0, channels).ToArray();
        if (channels == 6)
        {
            w[3] = 0;     // LFE
            w[4] = 1.41;  // surround left
            w[5] = 1.41;  // surround right
        }
        return w;
    }

    private static (Biquad Shelf, Biquad HighPass) KWeighting(int rate)
    {
        // Stage 1: high shelf (+4 dB above ~1.7 kHz).
        const double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / rate);
        var vh = Math.Pow(10, gain / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + (k / q) + (k * k);
        var shelf = new Biquad(
            (vh + (vb * k / q) + (k * k)) / a0, 2 * ((k * k) - vh) / a0, (vh - (vb * k / q) + (k * k)) / a0,
            2 * ((k * k) - 1) / a0, (1 - (k / q) + (k * k)) / a0);

        // Stage 2: RLB high-pass (~38 Hz).
        const double f1 = 38.13547087602444, q1 = 0.5003270373238773;
        var k1 = Math.Tan(Math.PI * f1 / rate);
        var d = 1 + (k1 / q1) + (k1 * k1);
        var highPass = new Biquad(1, -2, 1, 2 * ((k1 * k1) - 1) / d, (1 - (k1 / q1) + (k1 * k1)) / d);
        return (shelf, highPass);
    }

    /// <summary>Direct form II transposed biquad (a0 normalised to 1).</summary>
    private struct Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public double Process(double x)
        {
            var y = (b0 * x) + _z1;
            _z1 = (b1 * x) - (a1 * y) + _z2;
            _z2 = (b2 * x) - (a2 * y);
            return y;
        }
    }
}
