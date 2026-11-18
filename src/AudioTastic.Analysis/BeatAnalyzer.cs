using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis;

/// <summary>
/// AN-02: BPM, beat grid, phrases and breaks (own code; no GPL aubio). The audio is mixed to mono and decimated to
/// about 11 kHz. A 512-point FFT every ~5 ms gives the onset curve (log spectral flux, for the tempo) and the low-band
/// energy (below 150 Hz, for sections). Two envelopes at about 2 kHz, the high-band "click" and the low-band power,
/// locate the kick drum precisely. <see cref="BeatDetector"/> turns all of it into a <see cref="BeatInfo"/>.
/// </summary>
public sealed class BeatAnalyzer : IAnalyzer
{
    public const string KindName = "beats";
    public string Kind => KindName;
    public int DataVersion => 3; // 2: per-section phase corrections; 3: beat start/end where the groove is steady

    public IAnalysisRun Start(AudioFormat format, long? lengthFrames) => new Run(format, lengthFrames);

    /// <summary>Feature extraction, also used directly by the tests and the mix tool.</summary>
    public sealed class Run : IAnalysisRun
    {
        public const int FftSize = 512;

        /// <summary>Envelopes are kept for at most this long (the kick is located from the first hour).</summary>
        private const double MaxEnvelopeSeconds = 3600;

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
        private readonly List<float> _low = [];
        private readonly List<float> _clickEnvelope = [];
        private readonly List<float> _thumpEnvelope = [];
        private readonly int _pool;
        private readonly double _lowPass;
        private double _accumulator;
        private int _accumulated;
        private long _written; // decimated samples
        private int _sinceHop;
        private long _inputFrames;
        private float _lastSample;
        private double _lp1;
        private double _lp2;
        private int _pooled;
        private float _clickMax;
        private double _thumpSum;

        public Run(AudioFormat format, long? lengthFrames = null)
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
            _pool = Math.Max(1, (int)Math.Round(Rate / 2000));
            _lowPass = 1 - Math.Exp(-2 * Math.PI * 150 / Rate);
            if (lengthFrames is > 0 and var length)
            {
                // Sized up front: growing lists would reallocate large arrays again and again.
                var seconds = Math.Min(length / (double)format.SampleRate, MaxEnvelopeSeconds) + 1;
                var frames = (int)(seconds * FramesPerSecond);
                var envelope = (int)(seconds * EnvelopeRate);
                _onset.Capacity = frames;
                _low.Capacity = frames;
                _clickEnvelope.Capacity = envelope;
                _thumpEnvelope.Capacity = envelope;
            }
        }

        public int SourceRate { get; }

        /// <summary>Rate after decimation.</summary>
        public double Rate { get; }

        /// <summary>Onset frames per second.</summary>
        public double FramesPerSecond => Rate / _hop;

        /// <summary>Time (s) of onset frame 0: the centre of its window.</summary>
        public double FrameOffset => FftSize / 2.0 / Rate;

        /// <summary>Samples per second of <see cref="ClickEnvelope"/> and <see cref="ThumpEnvelope"/> (about 2,000).</summary>
        public double EnvelopeRate => Rate / _pool;

        /// <summary>The onset curve so far (one value per frame; see <see cref="FramesPerSecond"/>).</summary>
        public IReadOnlyList<float> Onsets => _onset;

        /// <summary>High-band envelope (the kick's click, hi-hats): peak of the first difference per envelope sample.</summary>
        public IReadOnlyList<float> ClickEnvelope => _clickEnvelope;

        /// <summary>Low-band (below 150 Hz) power envelope (the kick's thump, the bass).</summary>
        public IReadOnlyList<float> ThumpEnvelope => _thumpEnvelope;

        public void Process(ReadOnlySpan<float> interleaved)
        {
            var frames = interleaved.Length / _channels;
            _inputFrames += frames;
            var maxEnvelope = (int)(MaxEnvelopeSeconds * EnvelopeRate);
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

                // Envelopes: click = |first difference| (a high-pass), thump = power after two 150 Hz low-passes.
                _clickMax = Math.Max(_clickMax, Math.Abs(sample - _lastSample));
                _lastSample = sample;
                _lp1 += _lowPass * (sample - _lp1);
                _lp2 += _lowPass * (_lp1 - _lp2);
                _thumpSum += _lp2 * _lp2;
                if (++_pooled == _pool)
                {
                    if (_clickEnvelope.Count < maxEnvelope)
                    {
                        _clickEnvelope.Add(_clickMax);
                        _thumpEnvelope.Add((float)(_thumpSum / _pool));
                    }
                    _pooled = 0;
                    _clickMax = 0;
                    _thumpSum = 0;
                }

                _ring[_written % FftSize] = sample;
                _written++;
                if (_written == FftSize || (_written > FftSize && ++_sinceHop >= _hop))
                {
                    _sinceHop = 0;
                    Frame();
                }
            }
        }

        public byte[] Finish() => Detect().Serialize();

        public BeatInfo Detect() =>
            BeatDetector.Detect([.. _onset], [.. _low], FramesPerSecond, FrameOffset, [.. _clickEnvelope], [.. _thumpEnvelope], EnvelopeRate,
                _inputFrames / (double)SourceRate);

        private void Frame()
        {
            var start = _written - FftSize;
            for (var i = 0; i < FftSize; i++)
            {
                _re[i] = _ring[(start + i) % FftSize] * _window[i];
                _im[i] = 0;
            }
            Fft.Forward(_re, _im);
            double flux = 0, low = 0;
            for (var k = 1; k <= _fluxBins; k++)
            {
                var power = (_re[k] * _re[k]) + (_im[k] * _im[k]);
                var level = Math.Log(1 + (100 * Math.Sqrt(power)));
                var rise = level - _previous[k];
                if (rise > 0)
                {
                    flux += rise;
                }
                _previous[k] = level;
                if (k <= _lowBins)
                {
                    low += power;
                }
            }
            _onset.Add((float)flux);
            _low.Add((float)low);
        }
    }
}

/// <summary>
/// AN-02: tempo, beat grid, phrases and breaks. Electronic dance music has a constant tempo, so the result is a grid:
/// <list type="number">
/// <item>tempo from the onset autocorrelation (60–200 BPM, with a prior around 130 BPM), refined by folding the whole
/// onset curve at candidate periods (the sharpest fold wins);</item>
/// <item>phase on the kick drum: the click and thump envelopes are averaged over the beats of the drum sections; the
/// kick is the click across which the low band rises (hi-hats have no thump, bass notes no click), and its attack is
/// the beat. Checked by ear on real tracks; averaging hundreds of beats makes it precise to a few milliseconds;</item>
/// <item>confidence: how steadily a dynamic-programming beat tracker (Ellis 2007) keeps one phase at this tempo;</item>
/// <item>phrases (every 8 beats), beat start/end and breaks from per-beat low-band energy.</item>
/// </list>
/// </summary>
public static class BeatDetector
{
    private const double MinBpm = 60;
    private const double MaxBpm = 200;

    public static BeatInfo Detect(float[] onsetRaw, float[] low, double fps, double frameOffset, float[] click, float[] thump,
        double envelopeRate, double duration)
    {
        ArgumentNullException.ThrowIfNull(onsetRaw);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(click);
        ArgumentNullException.ThrowIfNull(thump);
        if (onsetRaw.Length < fps * 4)
        {
            return new BeatInfo(120, 0, 0, 0, duration, []);
        }
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

        // 2. Refine the period by folding: coarse then fine steps (the phase found here is only an anchor).
        (period, var anchor) = Fold(onset, period, 0.02, 0.0005, 64);
        (period, anchor) = Fold(onset, period, 0.0006, 0.00002, 256);
        var periodSeconds = period / fps;
        var bpmFinal = 60 / periodSeconds;

        // 3. Phase, pass 1: the kick's attack averaged over the whole track (intro and breaks included).
        var anchorSeconds = (anchor / fps) + frameOffset;
        var attack = KickAttack(click, thump, envelopeRate, periodSeconds, anchorSeconds) ?? anchorSeconds;
        var firstBeat = ((attack % periodSeconds) + periodSeconds) % periodSeconds;
        var (lowPerBeat, accent) = PerBeat(low, onset, fps, frameOffset, firstBeat, periodSeconds, duration);
        var (beatStart, beatEnd, inner) = Sections(lowPerBeat);

        // Pass 2: only where the drums play. The kick attack per 32 beats inside the drum sections (no intro, outro or
        // breaks, whose off-beat clicks and bass would pull the average); the most common offset wins.
        if (DrumPhase(click, thump, envelopeRate, periodSeconds, firstBeat, beatStart, beatEnd, inner) is { } offset)
        {
            firstBeat = (((firstBeat + offset) % periodSeconds) + periodSeconds) % periodSeconds;
            (lowPerBeat, accent) = PerBeat(low, onset, fps, frameOffset, firstBeat, periodSeconds, duration);
            (beatStart, beatEnd, inner) = Sections(lowPerBeat);
        }

        // Beat start and end where the groove is steady: an intro whose low hits wander (not yet 4/4) is not a place to mix.
        (beatStart, beatEnd) = SteadyEdges(thump, envelopeRate, periodSeconds, firstBeat, beatStart, beatEnd);

        // Per-section corrections: where the kick sits against the final grid in each 32-beat drum section. Only clear
        // measurements (within ±50 ms) are kept; bigger ones are another event (a 16th, the off-beat), not the kick.
        var corrections = SectionOffsets(click, thump, envelopeRate, periodSeconds, firstBeat, beatStart, beatEnd, inner)
            .Where(c => Math.Abs(c.Offset) <= 0.050)
            .Select(c => new PhaseCorrection(c.Start, c.Offset))
            .ToList();

        // 4. Confidence.
        var confidence = Confidence(onset, period);

        // 5. Phrases, beat start/end and breaks.
        double BeatSeconds(double k) => firstBeat + (k * periodSeconds);
        var phraseOffset = PhraseOffset(lowPerBeat, accent);
        var breaks = inner.Select(r => new BeatBreak(BeatSeconds(r.Start), BeatSeconds(r.End))).ToList();
        return new BeatInfo(bpmFinal, firstBeat, confidence, phraseOffset, duration, breaks,
            BeatSeconds(beatStart), Math.Min(duration, BeatSeconds(beatEnd)), corrections);
    }

    /// <summary>The kick attack relative to the grid (s, folded to ±half a beat) per 32-beat drum section (no breaks).</summary>
    private static List<(double Start, double Offset)> SectionOffsets(float[] click, float[] thump, double envelopeRate, double period, double firstBeat,
        int beatStart, int beatEnd, List<(int Start, int End)> breaks)
    {
        var window = BeatInfo.CorrectionBeats;
        var result = new List<(double, double)>();
        for (var k = beatStart; k + window <= beatEnd; k += window)
        {
            var start = k;
            if (breaks.Any(b => b.Start < start + window && b.End > start))
            {
                continue;
            }
            var t = firstBeat + (k * period);
            if (KickAttack(click, thump, envelopeRate, period, t, t, t + (window * period)) is { } attack)
            {
                var d = attack - t;
                result.Add((t, d - (Math.Round(d / period) * period)));
            }
        }
        return result;
    }

    /// <summary>Per-beat low-band energy and the onset accent on each beat, for the grid (firstBeat, period).</summary>
    private static (double[] Low, double[] Accent) PerBeat(float[] low, double[] onset, double fps, double frameOffset, double firstBeat,
        double period, double duration)
    {
        int FrameAt(double seconds) => (int)Math.Round((seconds - frameOffset) * fps);
        var beats = Math.Max(0, (int)Math.Floor((duration - firstBeat) / period));
        var perBeat = new double[beats];
        var accent = new double[beats];
        for (var k = 0; k < beats; k++)
        {
            var a = Math.Clamp(FrameAt(firstBeat + (k * period)), 0, low.Length);
            var b = Math.Clamp(FrameAt(firstBeat + ((k + 1) * period)), 0, low.Length);
            double sum = 0;
            for (var t = a; t < b; t++)
            {
                sum += low[t];
            }
            perBeat[k] = sum / Math.Max(1, b - a);
            accent[k] = onset[Math.Clamp(a, 0, onset.Length - 1)];
        }
        return (perBeat, accent);
    }

    /// <summary>
    /// The correction (s, within ±half a beat) of the grid's phase from the kick attacks in 32-beat windows inside the
    /// drum sections: the most common offset (1 ms bins, ±2 ms), refined by the mean of the offsets within 3 ms of it.
    /// Null with fewer than 3 windows.
    /// </summary>
    private static double? DrumPhase(float[] click, float[] thump, double envelopeRate, double period, double firstBeat,
        int beatStart, int beatEnd, List<(int Start, int End)> breaks)
    {
        var offsets = SectionOffsets(click, thump, envelopeRate, period, firstBeat, beatStart, beatEnd, breaks).Select(s => s.Offset).ToList();
        if (offsets.Count < 3)
        {
            return null;
        }
        var half = (int)Math.Ceiling(period * 500);
        var histogram = new int[(2 * half) + 1];
        foreach (var o in offsets)
        {
            histogram[Math.Clamp((int)Math.Round(o * 1000) + half, 0, histogram.Length - 1)]++;
        }
        var mode = 0;
        var bestCount = -1;
        for (var i = 0; i < histogram.Length; i++)
        {
            var count = 0;
            for (var j = Math.Max(0, i - 2); j <= Math.Min(histogram.Length - 1, i + 2); j++)
            {
                count += histogram[j];
            }
            if (count > bestCount)
            {
                bestCount = count;
                mode = i - half;
            }
        }
        var near = offsets.Where(o => Math.Abs((o * 1000) - mode) <= 3).ToList();
        return near.Count > 0 ? near.Average() : mode / 1000.0;
    }

    /// <summary>
    /// The kick drum's attack time (s) near <paramref name="anchor"/> (any beat time of the grid, ± half a beat): the
    /// click and thump envelopes are averaged over the beats in [<paramref name="from"/>, <paramref name="to"/>) (default:
    /// all). The kick is the click across which the low band rises most; its attack (where the averaged click first
    /// reaches half its peak) is the beat. Null when there is nothing to average. Also used by tools/MixLab
    /// to measure the alignment of two decks, so detection and measurement agree.
    /// </summary>
    public static double? KickAttack(IReadOnlyList<float> click, IReadOnlyList<float> thump, double envelopeRate, double period, double anchor,
        double from = 0, double to = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(click);
        ArgumentNullException.ThrowIfNull(thump);
        var samplesPerBeat = period * envelopeRate;
        var width = (int)Math.Floor(samplesPerBeat);
        if (width < 16)
        {
            return null;
        }
        var length = Math.Min(click.Count, thump.Count);
        var end = Math.Min(to, length / envelopeRate);
        // Windows of one beat starting half a beat before each grid beat.
        var windowStart = anchor - (period / 2);
        var k0 = (long)Math.Ceiling((from - windowStart) / period);
        var avgClick = new double[width];
        var avgThump = new double[width];
        var count = 0;
        for (var k = k0; windowStart + ((k + 1) * period) <= end; k++)
        {
            var origin = (windowStart + (k * period)) * envelopeRate;
            if (origin < 0)
            {
                continue;
            }
            var first = (int)origin;
            var frac = origin - first;
            for (var i = 0; i < width && first + i + 1 < length; i++)
            {
                // Linear interpolation keeps sub-sample alignment across beats.
                avgClick[i] += (click[first + i] * (1 - frac)) + (click[first + i + 1] * frac);
                avgThump[i] += (thump[first + i] * (1 - frac)) + (thump[first + i + 1] * frac);
            }
            count++;
        }
        if (count == 0)
        {
            return null;
        }
        // Candidates are real click peaks (local maxima within ±1 ms, at least 30 % of the strongest click); the score is
        // the click × how much the low band rises across it (the 80 ms after against the 40 ms before), so a hi-hat (no
        // thump), the end of a bass note (low band falls) and the kick's own body (no click) do not win (circular: the
        // beat repeats). Checked by ear on Anders's tracks (4 of 5 exact; a kick without an attack click can come out
        // a few tens of ms late).
        var after = (int)Math.Round(0.080 * envelopeRate);
        var before = (int)Math.Round(0.040 * envelopeRate);
        var near = Math.Max(1, (int)Math.Round(0.001 * envelopeRate));
        var strongest = avgClick.Max();
        var best = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < width; i++)
        {
            if (avgClick[i] < 0.3 * strongest)
            {
                continue;
            }
            var isPeak = true;
            for (var j = 1; j <= near && isPeak; j++)
            {
                isPeak = avgClick[i] >= avgClick[(i + j) % width] && avgClick[i] >= avgClick[((i - j) % width + width) % width];
            }
            if (!isPeak)
            {
                continue;
            }
            double rise = 0;
            for (var j = 0; j < after; j++)
            {
                rise += avgThump[(i + j) % width] / after;
            }
            for (var j = 1; j <= before; j++)
            {
                rise -= avgThump[((i - j) % width + width) % width] / before;
            }
            var score = avgClick[i] * Math.Max(0, rise);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        // Attack: walk back from the click's peak (within 5 ms) to where it first reaches half the peak.
        var peak = best;
        for (var i = 1; i <= (int)(0.003 * envelopeRate); i++)
        {
            var j = (best + i) % width;
            if (avgClick[j] > avgClick[peak])
            {
                peak = j;
            }
        }
        var half = avgClick[peak] / 2;
        var attack = (double)peak;
        for (var i = 1; i <= (int)(0.005 * envelopeRate); i++)
        {
            var j = ((peak - i) % width + width) % width;
            var next = ((j + 1) % width + width) % width;
            if (avgClick[j] < half)
            {
                // Interpolate the crossing between j and j + 1.
                var span = avgClick[next] - avgClick[j];
                attack = peak - i + (span > 0 ? (half - avgClick[j]) / span : 0);
                break;
            }
        }
        return windowStart + (attack / envelopeRate);
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

    /// <summary>
    /// Tempo steadiness: the share of dynamic-programming beats (Ellis 2007) within 30 ms (or 6 % of a beat) of their
    /// own most common phase. Independent of which sub-beat the tracker follows (kick, hi-hat or bass).
    /// </summary>
    private static double Confidence(double[] onset, double period)
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
        var tolerance = Math.Max(1.5, period * 0.06);
        var bins = Math.Max(1, (int)Math.Ceiling(period));
        var histogram = new int[bins];
        foreach (var b in beats)
        {
            histogram[(int)(b % period) % bins]++;
        }
        var bestPhase = 0.0;
        var bestCount = -1;
        for (var i = 0; i < bins; i++)
        {
            var count = 0;
            for (var j = -(int)tolerance; j <= (int)tolerance; j++)
            {
                count += histogram[((i + j) % bins + bins) % bins];
            }
            if (count > bestCount)
            {
                bestCount = count;
                bestPhase = i;
            }
        }
        var onGrid = beats.Count(b =>
        {
            var d = Math.Abs((b % period) - bestPhase);
            return Math.Min(d, period - d) <= tolerance;
        });
        return onGrid / (double)beats.Count;
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
            // Rises count fully (new elements come in on the "one"), falls half (the beat after a one-beat accent).
            var change = Math.Log(low[k] + 1e-9) - Math.Log(low[k - 1] + 1e-9);
            var weight = change > 0 ? Math.Min(change, 5) : 0.5 * Math.Min(-change, 5);
            scores[k % BeatInfo.PhraseBeats] += weight + (0.2 * accent[k] / maxAccent);
        }
        return Array.IndexOf(scores, scores.Max());
    }

    /// <summary>Beats compared when deciding whether the groove is steady, and how many of them must match.</summary>
    private const int SteadyBeats = 16;
    private const int SteadyMatches = 14;

    /// <summary>
    /// Moves the beat start later and the beat end earlier to where the groove is steady: each beat's low-band pattern
    /// (the strongest low-band power in each of its four 16ths, relative to the beat's own maximum) must match the
    /// median pattern of the next (or previous) 16 beats in at least 14 of them. Dance intros and outros often play
    /// low hits that wander across the 16ths before the 4/4 kick starts (heard on Anders's tracks), which energy alone
    /// counts as drums. Unchanged when no steady stretch is found near the edge.
    /// </summary>
    internal static (int BeatStart, int BeatEnd) SteadyEdges(IReadOnlyList<float> thump, double envelopeRate, double period, double firstBeat,
        int beatStart, int beatEnd)
    {
        var count = beatEnd - beatStart;
        if (count < 4 * SteadyBeats)
        {
            return (beatStart, beatEnd);
        }
        var patterns = new double[count][];
        for (var i = 0; i < count; i++)
        {
            patterns[i] = Pattern(thump, envelopeRate, period, firstBeat + ((beatStart + i) * period));
        }
        bool Steady(int from)
        {
            var window = patterns.Skip(from).Take(SteadyBeats).ToArray();
            var median = Enumerable.Range(0, 4).Select(q => window.Select(p => p[q]).Order().ElementAt(window.Length / 2)).ToArray();
            return window.Count(p => Similar(p, median)) >= SteadyMatches;
        }
        // At most a quarter of the drum section from each edge (the middle is the track itself).
        var limit = count / 4;
        var start = 0;
        while (start < limit && !Steady(start))
        {
            start++;
        }
        var end = count;
        while (count - end < limit && !Steady(end - SteadyBeats))
        {
            end--;
        }
        if (start >= limit)
        {
            start = 0;
        }
        if (count - end >= limit)
        {
            end = count;
        }
        return (beatStart + start, beatStart + end);
    }

    /// <summary>The strongest low-band power in each 16th of the beat at <paramref name="beat"/> (s), relative to the largest.</summary>
    private static double[] Pattern(IReadOnlyList<float> thump, double envelopeRate, double period, double beat)
    {
        var result = new double[4];
        for (var q = 0; q < 4; q++)
        {
            var from = (int)Math.Round((beat + (q * period / 4)) * envelopeRate);
            var to = (int)Math.Round((beat + ((q + 1) * period / 4)) * envelopeRate);
            for (var i = Math.Max(0, from); i < Math.Min(thump.Count, to); i++)
            {
                result[q] = Math.Max(result[q], thump[i]);
            }
        }
        var max = result.Max();
        if (max > 0)
        {
            for (var q = 0; q < 4; q++)
            {
                result[q] /= max;
            }
        }
        return result;
    }

    /// <summary>Two 16th patterns match: every 16th within 0.35 of the other's (relative to each beat's maximum).</summary>
    private static bool Similar(double[] a, double[] b) => Enumerable.Range(0, 4).All(q => Math.Abs(a[q] - b[q]) <= 0.35);

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
        var sorted = low.Where(v => v > 0).Order().ToArray();
        if (sorted.Length == 0)
        {
            return (0, low.Length, result);
        }
        var threshold = sorted[sorted.Length / 2] * 0.25;
        bool Loud(int j) => j >= 0 && j < low.Length && low[j] >= threshold;
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
