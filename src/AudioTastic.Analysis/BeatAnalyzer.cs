using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis;

/// <summary>
/// AN-02: BPM, beat grid, phrases and breaks (own code; no GPL aubio). The audio is mixed to mono and decimated to
/// about 11 kHz; a 512-point FFT every ~5 ms gives an onset curve (log spectral flux) and the low-frequency energy
/// (below 150 Hz). <see cref="BeatDetector"/> turns those into a <see cref="BeatInfo"/>.
/// </summary>
public sealed class BeatAnalyzer : IAnalyzer
{
    public const string KindName = "beats";
    public string Kind => KindName;
    public int DataVersion => 1;

    public IAnalysisRun Start(AudioFormat format, long? lengthFrames) => new Run(format);

    /// <summary>Feature extraction, also used directly by the tests and the mix tool.</summary>
    public sealed class Run : IAnalysisRun
    {
        public const int FftSize = 512;
        private readonly int _channels;
        private readonly int _decimation;
        private readonly int _hop;
        private readonly float[] _ring = new float[FftSize];
        private readonly double[] _window = new double[FftSize];
        private readonly double[] _re = new double[FftSize];
        private readonly double[] _im = new double[FftSize];
        private readonly double[] _previous = new double[(FftSize / 2) + 1];
        private readonly int _fluxBins;
        private readonly int _lowBins;
        private readonly List<float> _onset = [];
        private readonly List<float> _lowOnset = [];
        private readonly List<float> _low = [];
        private double _accumulator;
        private int _accumulated;
        private long _written; // decimated samples
        private int _sinceHop;
        private long _inputFrames;

        public Run(AudioFormat format)
        {
            _channels = format.Channels;
            SourceRate = format.SampleRate;
            _decimation = Math.Max(1, (int)Math.Round(format.SampleRate / 11025.0));
            Rate = format.SampleRate / (double)_decimation;
            _hop = Math.Max(16, (int)Math.Round(Rate / 200));
            for (var i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / FftSize));
            }
            var binHz = Rate / FftSize;
            _fluxBins = Math.Min(FftSize / 2, (int)(Math.Min(10000, Rate * 0.45) / binHz));
            _lowBins = Math.Max(1, (int)Math.Round(150 / binHz));
        }

        public int SourceRate { get; }

        /// <summary>Rate after decimation.</summary>
        public double Rate { get; }

        /// <summary>Onset frames per second.</summary>
        public double FramesPerSecond => Rate / _hop;

        /// <summary>Time (s) of onset frame 0: the centre of its window.</summary>
        public double FrameOffset => (FftSize / 2.0 / Rate) + OnsetLatency;

        /// <summary>
        /// Onset curve latency (s): a window "sees" a transient before its centre reaches it, so beat times are moved
        /// later by this much (measured with synthetic kicks at 44.1–96 kHz).
        /// </summary>
        public const double OnsetLatency = 0.0165;

        public void Process(ReadOnlySpan<float> interleaved)
        {
            var frames = interleaved.Length / _channels;
            _inputFrames += frames;
            for (var f = 0; f < frames; f++)
            {
                double mono = 0;
                for (var c = 0; c < _channels; c++)
                {
                    mono += interleaved[(f * _channels) + c];
                }
                _accumulator += mono / _channels;
                if (++_accumulated < _decimation)
                {
                    continue;
                }
                var sample = (float)(_accumulator / _decimation);
                _accumulator = 0;
                _accumulated = 0;
                _ring[_written % FftSize] = sample;
                _written++;
                if (_written >= FftSize && ++_sinceHop >= _hop)
                {
                    _sinceHop = 0;
                    Frame();
                }
                else if (_written == FftSize)
                {
                    _sinceHop = 0;
                    Frame();
                }
            }
        }

        public byte[] Finish() => Detect().Serialize();

        public BeatInfo Detect() =>
            BeatDetector.Detect([.. _onset], [.. _lowOnset], [.. _low], FramesPerSecond, FrameOffset, _inputFrames / (double)SourceRate);

        private void Frame()
        {
            var start = _written - FftSize;
            for (var i = 0; i < FftSize; i++)
            {
                _re[i] = _ring[(start + i) % FftSize] * _window[i];
                _im[i] = 0;
            }
            Fft.Forward(_re, _im);
            double flux = 0, lowFlux = 0, low = 0;
            for (var k = 1; k <= _fluxBins; k++)
            {
                var power = (_re[k] * _re[k]) + (_im[k] * _im[k]);
                var level = Math.Log(1 + (100 * Math.Sqrt(power)));
                var rise = level - _previous[k];
                if (rise > 0)
                {
                    flux += rise;
                    if (k <= _lowBins)
                    {
                        lowFlux += rise;
                    }
                }
                _previous[k] = level;
                if (k <= _lowBins)
                {
                    low += power;
                }
            }
            _onset.Add((float)flux);
            _lowOnset.Add((float)lowFlux);
            _low.Add((float)low);
        }
    }
}

/// <summary>
/// AN-02: tempo, beat grid, phrases and breaks from onset curves. Electronic dance music has a constant tempo, so
/// the result is a grid: tempo from the onset autocorrelation (60–200 BPM, with a prior around 130 BPM), refined by
/// folding the whole onset curve at candidate periods (the sharpest fold wins; its peak is the beat phase). The
/// onset curve weights the low band (kick drum) double, and the phase is put on the kick, not the off-beat hi-hat.
/// Confidence is the share of beats found by dynamic-programming beat tracking (Ellis 2007) that sit on the grid.
/// </summary>
public static class BeatDetector
{
    private const double MinBpm = 60;
    private const double MaxBpm = 200;

    public static BeatInfo Detect(float[] onsetRaw, float[] lowOnsetRaw, float[] low, double fps, double frameOffset, double duration)
    {
        ArgumentNullException.ThrowIfNull(onsetRaw);
        ArgumentNullException.ThrowIfNull(lowOnsetRaw);
        ArgumentNullException.ThrowIfNull(low);
        if (onsetRaw.Length < fps * 4)
        {
            return new BeatInfo(120, 0, 0, 0, duration, []);
        }
        // Full-band onsets set tempo and phase (they are transient-precise); low-band onsets build up slowly in a 46 ms
        // window and would pull the phase, so the kick only decides which half-beat is the beat (low-band energy, below).
        var onset = Normalise(onsetRaw, fps);

        // 1. Coarse tempo: autocorrelation with harmonic support and a tempo prior.
        var minLag = (int)Math.Floor(fps * 60 / MaxBpm);
        var maxLag = (int)Math.Ceiling(fps * 60 / MinBpm);
        var ac = new double[(maxLag * 4) + 2];
        for (var lag = minLag / 2; lag < ac.Length; lag++)
        {
            double sum = 0;
            for (var t = lag; t < onset.Length; t++)
            {
                sum += onset[t] * onset[t - lag];
            }
            ac[lag] = sum / (onset.Length - lag);
        }
        var bestLag = minLag;
        var bestScore = double.MinValue;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var bpm = fps * 60 / lag;
            var prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 130) / 0.8, 2));
            var score = prior * (ac[lag] + (0.5 * ac[2 * lag]) + (0.25 * ac[Math.Min(ac.Length - 1, 4 * lag)]));
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }
        var period = Parabolic(ac, bestLag);

        // 2. Refine by folding: coarse then fine steps.
        (period, var phase) = Fold(onset, period, 0.02, 0.0005, 64);
        (period, phase) = Fold(onset, period, 0.0006, 0.00002, 256);

        // The beat is where the kick is: if the low band hits harder half a beat later, the fold found the off-beat.
        // (Low-band energy, not onsets: a rolling bass on the off-beat has sharp onsets too, but the kick has the energy.)
        var lowEnergy = Array.ConvertAll(low, v => (double)v);
        if (FoldedAt(lowEnergy, period, phase + (period / 2)) > FoldedAt(lowEnergy, period, phase) * 1.1)
        {
            phase = (phase + (period / 2)) % period;
        }

        // Beat 0: the first grid beat at or after the start.
        var first = phase; // frames, 0 <= phase < period
        var bpmFinal = fps * 60 / period;

        // 3. Confidence from DP beat tracking.
        var confidence = Confidence(onset, period, first);

        // 4. Phrases and breaks from beat-synchronous low-frequency energy.
        var beats = (int)Math.Floor((onset.Length - first) / period);
        var lowPerBeat = new double[Math.Max(0, beats)];
        var accent = new double[lowPerBeat.Length];
        for (var k = 0; k < beats; k++)
        {
            var a = (int)Math.Round(first + (k * period));
            var b = Math.Min(low.Length, (int)Math.Round(first + ((k + 1) * period)));
            double sum = 0;
            for (var t = a; t < b; t++)
            {
                sum += low[t];
            }
            lowPerBeat[k] = sum / Math.Max(1, b - a);
            accent[k] = onset[Math.Clamp(a, 0, onset.Length - 1)];
        }
        var phraseOffset = PhraseOffset(lowPerBeat, accent);
        var toSeconds = new Func<double, double>(frame => (frame / fps) + frameOffset);
        var (beatStart, beatEnd, inner) = Sections(lowPerBeat);
        var breaks = inner.Select(r => new BeatBreak(toSeconds(first + (r.Start * period)), toSeconds(first + (r.End * period)))).ToList();
        return new BeatInfo(bpmFinal, toSeconds(first), confidence, phraseOffset, duration, breaks,
            toSeconds(first + (beatStart * period)), Math.Min(duration, toSeconds(first + (beatEnd * period))));
    }

    /// <summary>Removes the local mean (0.4 s) and negative values, then scales to unit standard deviation.</summary>
    private static double[] Normalise(float[] raw, double fps)
    {
        var n = raw.Length;
        var half = Math.Max(1, (int)(fps * 0.2));
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + raw[i];
        }
        var result = new double[n];
        double sq = 0;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Max(0, i - half);
            var b = Math.Min(n, i + half + 1);
            var mean = (prefix[b] - prefix[a]) / (b - a);
            result[i] = Math.Max(0, raw[i] - mean);
            sq += result[i] * result[i];
        }
        var std = Math.Sqrt(sq / n);
        if (std > 0)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] /= std;
            }
        }
        return result;
    }

    private static double Parabolic(double[] values, int i)
    {
        if (i <= 0 || i >= values.Length - 1)
        {
            return i;
        }
        var (a, b, c) = (values[i - 1], values[i], values[i + 1]);
        var denominator = a - (2 * b) + c;
        return Math.Abs(denominator) < 1e-12 ? i : i + (0.5 * (a - c) / denominator);
    }

    /// <summary>
    /// Folds the onset curve at periods around <paramref name="period"/> (±<paramref name="range"/>, relative steps of
    /// <paramref name="step"/>) into <paramref name="bins"/> phase bins; returns the period whose fold has the highest
    /// peak, and the peak's phase in frames.
    /// </summary>
    internal static (double Period, double Phase) Fold(double[] onset, double period, double range, double step, int bins)
    {
        var histogram = new double[bins];
        var bestPeak = double.MinValue;
        var bestPeriod = period;
        var bestPhase = 0.0;
        for (var rel = -range; rel <= range + 1e-12; rel += step)
        {
            var p = period * (1 + rel);
            Array.Clear(histogram);
            for (var t = 0; t < onset.Length; t++)
            {
                var v = onset[t];
                if (v <= 0)
                {
                    continue;
                }
                var position = t / p;
                var frac = (position - Math.Floor(position)) * bins;
                var bin = (int)frac;
                var w = frac - bin;
                histogram[bin % bins] += v * (1 - w);
                histogram[(bin + 1) % bins] += v * w;
            }
            // Smooth over 3 bins (a beat is not a single bin wide).
            for (var b = 0; b < bins; b++)
            {
                var s = histogram[(b + bins - 1) % bins] + (2 * histogram[b]) + histogram[(b + 1) % bins];
                if (s > bestPeak)
                {
                    bestPeak = s;
                    bestPeriod = p;
                    var left = histogram[(b + bins - 1) % bins];
                    var right = histogram[(b + 1) % bins];
                    var den = left - (2 * histogram[b]) + right;
                    var offset = Math.Abs(den) < 1e-12 ? 0 : 0.5 * (left - right) / den;
                    bestPhase = ((b + offset + bins) % bins) / bins * p;
                }
            }
        }
        return (bestPeriod, bestPhase);
    }

    /// <summary>Sum of <paramref name="curve"/> over the 30 ms after each grid beat at <paramref name="phase"/>.</summary>
    private static double FoldedAt(double[] curve, double period, double phase)
    {
        double sum = 0;
        for (var t = phase % period; t < curve.Length; t += period)
        {
            var c = (int)Math.Round(t);
            for (var j = Math.Max(0, c); j <= Math.Min(curve.Length - 1, c + 6); j++)
            {
                sum += curve[j];
            }
        }
        return sum;
    }

    /// <summary>Share of dynamic-programming beats within 30 ms (or 6 % of a beat) of the grid.</summary>
    private static double Confidence(double[] onset, double period, double first)
    {
        var n = onset.Length;
        var score = new double[n];
        var back = new int[n];
        const double tightness = 100;
        var maxGap = (int)Math.Round(2 * period);
        var minGap = (int)Math.Round(period / 2);
        var penalties = new double[maxGap + 1];
        for (var d = Math.Max(1, minGap); d <= maxGap; d++)
        {
            penalties[d] = tightness * Math.Pow(Math.Log(d / period), 2);
        }
        for (var t = 0; t < n; t++)
        {
            var best = 0.0;
            var from = -1;
            for (var tau = Math.Max(0, t - maxGap); tau <= t - minGap; tau++)
            {
                var s = score[tau] - penalties[t - tau];
                if (from < 0 || s > best)
                {
                    best = s;
                    from = tau;
                }
            }
            score[t] = onset[t] + (from >= 0 ? best : 0);
            back[t] = from;
        }
        var end = n - 1;
        for (var t = Math.Max(0, n - (int)period); t < n; t++)
        {
            if (score[t] > score[end])
            {
                end = t;
            }
        }
        var beats = new List<int>();
        for (var t = end; t >= 0; t = back[t])
        {
            beats.Add(t);
        }
        if (beats.Count == 0)
        {
            return 0;
        }
        // Tempo steadiness: the tracker may follow the off-beat (hi-hats, rolling bass), so either half of the grid counts.
        var tolerance = Math.Max(1.5, period * 0.06);
        int OnGrid(double origin) => beats.Count(b =>
        {
            var k = Math.Round((b - origin) / period);
            return Math.Abs(b - (origin + (k * period))) <= tolerance;
        });
        return Math.Max(OnGrid(first), OnGrid(first + (period / 2))) / (double)beats.Count;
    }

    /// <summary>The beat (0–7) phrases start on: where low-frequency energy changes and accents line up best.</summary>
    private static int PhraseOffset(double[] low, double[] accent)
    {
        if (low.Length < 16)
        {
            return 0;
        }
        var maxAccent = Math.Max(1e-9, accent.Max());
        var scores = new double[BeatInfo.PhraseBeats];
        for (var k = 1; k < low.Length; k++)
        {
            var change = Math.Abs(Math.Log(low[k] + 1e-9) - Math.Log(low[k - 1] + 1e-9));
            scores[k % BeatInfo.PhraseBeats] += Math.Min(change, 5) + (0.2 * accent[k] / maxAccent);
        }
        return Array.IndexOf(scores, scores.Max());
    }

    /// <summary>
    /// Beat start and end (beat indices: the first beat with drums, and the beat after the last one) and the breaks
    /// between them: runs of at least 16 beats (4 bars) whose low-frequency energy is below a quarter of the median.
    /// A quiet run at the very start (intro) or end (outro) of any length moves the beat start or end instead.
    /// </summary>
    internal static (int BeatStart, int BeatEnd, List<(int Start, int End)> Breaks) Sections(double[] low)
    {
        var result = new List<(int, int)>();
        if (low.Length < 16)
        {
            return (0, low.Length, result);
        }
        // Per-beat energy, unsmoothed (smoothing would blur the edges by a beat or two); single loud or quiet beats are
        // handled below: edges need 3 loud beats out of 4, and breaks are merged across short interruptions.
        var rawSorted = low.Where(v => v > 0).Order().ToArray();
        if (rawSorted.Length == 0)
        {
            return (0, low.Length, result);
        }
        var rawThreshold = rawSorted[rawSorted.Length / 2] * 0.25;
        // Beat start: the first beat where the drums are present for at least 3 of the next 4 beats; beat end likewise
        // from the back (so a single loud hit in an intro does not count).
        bool Loud(int j) => j >= 0 && j < low.Length && low[j] >= rawThreshold;
        var beatStart = 0;
        while (beatStart < low.Length && !(Loud(beatStart) && Enumerable.Range(beatStart, 4).Count(Loud) >= 3))
        {
            beatStart++;
        }
        var beatEnd = low.Length;
        while (beatEnd > beatStart + 1 && !(Loud(beatEnd - 1) && Enumerable.Range(beatEnd - 4, 4).Count(Loud) >= 3))
        {
            beatEnd--;
        }
        if (beatStart >= beatEnd)
        {
            return (0, low.Length, result); // no clear drums: the whole track counts
        }
        var runs = new List<(int Start, int End)>();
        var start = -1;
        for (var k = beatStart; k <= beatEnd; k++)
        {
            var quiet = k < beatEnd && !Loud(k);
            if (quiet && start < 0)
            {
                start = k;
            }
            else if (!quiet && start >= 0)
            {
                // A quiet run interrupted by less than a bar of drums (a single hit) continues the previous one.
                if (runs.Count > 0 && start - runs[^1].End < 4)
                {
                    runs[^1] = (runs[^1].Start, k);
                }
                else
                {
                    runs.Add((start, k));
                }
                start = -1;
            }
        }
        result.AddRange(runs.Where(r => r.End - r.Start >= 16));
        return (beatStart, beatEnd, result);
    }
}
