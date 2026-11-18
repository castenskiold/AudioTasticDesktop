using AudioTastic.Core.Audio.Beats;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis;

/// <summary>
/// XF-02: aligns a beat-matched transition from the audio of the overlap itself. Both tracks are decoded over the
/// full-volume overlap (the incoming one around its planned position); in each, the high-band envelope is averaged
/// over all its beats there and the start of the strongest transient is found. The difference, in output time (up to
/// half a beat), moves the incoming entry point, so those transients coincide where both tracks are loud, whatever
/// errors either beat grid has there. Blind listening tests on Anders's tracks chose these transients over the kick
/// detector's attack and over the low thump. Both halves of the overlap must agree, or the grids decide.
/// </summary>
public sealed class CrossfadeAligner(IEnumerable<IAudioSourceFactory> factories) : ICrossfadeAligner
{
    /// <summary>The fewest beats measured, and the fewest per half for the halves to be compared.</summary>
    private const int MinimumBeats = 4;
    private const int HalfBeats = 8;

    /// <summary>How closely each half of the overlap must agree with the whole (output seconds).</summary>
    private const double Agreement = 0.008;

    /// <summary>
    /// How far before the detected kick the beat may lie (s). Blind listening tests on Anders's tracks put the beat
    /// 0 to 125 ms before the click the kick detector picks; an off-beat hi-hat (half a beat away) stays outside below
    /// 200 BPM.
    /// </summary>
    private const double Lead = 0.150;

    /// <summary>How far the averaged transient must stand above the median of the beat (a factor).</summary>
    private const double Prominence = 1.5;

    public double? Refine(CrossfadeAlignment alignment) => Measure(alignment).Shift;

    /// <summary>
    /// The beat differences (output seconds, positive = incoming late) over the whole full-volume overlap and, when it
    /// is long enough, each half; and the resulting shift (track seconds; null when the halves do not agree with the
    /// whole). Also used by tools/MixLab.
    /// </summary>
    public (double? Shift, IReadOnlyList<double> Differences) Measure(CrossfadeAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        var a = alignment;
        if (a.Overlap <= 0 || a.OutgoingSpeed <= 0 || a.IncomingSpeed <= 0 || a.Period <= 0)
        {
            return (null, []);
        }
        var periodOut = a.Period * a.OutgoingSpeed; // one beat in each track's own seconds
        var periodIn = a.Period * a.IncomingSpeed;
        // The first whole beat after the fade-in (output beats from the mix start; the mix start and the incoming
        // entry point are both beats) and the number of whole beats in the full-volume overlap.
        var firstBeat = (int)Math.Ceiling((a.FadeIn / a.Period) - 1e-9);
        var beats = (int)Math.Floor(((a.FadeIn + a.Overlap) / a.Period) - firstBeat + 1e-9);
        if (beats < MinimumBeats)
        {
            return (null, []);
        }
        // Spans with one beat to spare on each side (a kick measurement looks half a beat either way of the grid).
        var outFrom = a.MixStart + ((firstBeat - 1) * periodOut);
        var inFrom = a.IncomingStart + ((firstBeat - 1) * periodIn);
        if (outFrom < 0 || inFrom < 0 ||
            Envelopes(a.OutgoingPath, outFrom, (beats + 2) * periodOut) is not { } outgoing ||
            Envelopes(a.IncomingPath, inFrom, (beats + 2) * periodIn) is not { } incoming)
        {
            return (null, []);
        }

        // The whole overlap, then each half when long enough: the halves must give the same answer as the whole.
        var differences = new List<double>(3);
        var spans = beats >= 2 * HalfBeats ? new[] { (0, beats), (0, beats / 2), (beats / 2, beats - (beats / 2)) } : [(0, beats)];
        foreach (var (first, count) in spans)
        {
            var ko = Beat(outgoing, periodOut, 1 + first, count);
            var ki = Beat(incoming, periodIn, 1 + first, count);
            if (ko is null || ki is null)
            {
                return (null, differences);
            }
            // Positive: the incoming beats come late (output seconds).
            differences.Add(Wrap((ki.Value / a.IncomingSpeed) - (ko.Value / a.OutgoingSpeed), a.Period));
        }
        var shift = differences[0];
        var trusted = differences.Skip(1).All(d => Math.Abs(Wrap(d - shift, a.Period)) <= Agreement);
        // Moving the entry later by s (track seconds) brings the incoming kicks s / speed earlier in output time.
        return (trusted ? shift * a.IncomingSpeed : null, differences);
    }

    /// <summary>
    /// Where the ear hears the beat (track seconds from the grid beat) over <paramref name="count"/> beats from local beat
    /// <paramref name="first"/>. The analyser's kick detector (<see cref="BeatDetector.KickAttack"/>: a click across which
    /// the low band rises) finds the kick; the beat is then the strongest high-band transient from 70 ms before it to
    /// 20 ms after it, where that transient starts. Blind listening tests on Anders's tracks: one kick has an attack about
    /// 45 ms ahead of the click the detector picks, and the ear follows it; the window keeps off-beat hi-hats out.
    /// </summary>
    private static double? Beat((IReadOnlyList<float> Click, IReadOnlyList<float> Thump, double Rate) e, double period, int first, int count)
    {
        var from = first * period;
        if (BeatDetector.KickAttack(e.Click, e.Thump, e.Rate, period, from, from, from + (count * period)) is not { } attack ||
            Average(e.Click, e.Rate, period, first, count, 0.0005) is not { } high)
        {
            return null;
        }
        var width = high.Length;
        int At(int i) => ((i % width) + width) % width;
        // Index 0 of the average is half a beat before the grid beat.
        var kick = (int)Math.Round((Wrap(attack - from, period) + (period / 2)) * e.Rate);
        var median = high.Order().ElementAt(width / 2);
        var peak = kick;
        for (var i = kick - (int)(Lead * e.Rate); i <= kick + (int)(0.020 * e.Rate); i++)
        {
            if (high[At(i)] > high[At(peak)])
            {
                peak = i;
            }
        }
        var beat = kick;
        if (high[At(peak)] >= Prominence * median)
        {
            // Where the transient starts: half way from the median up to its peak.
            var level = median + ((high[At(peak)] - median) / 2);
            beat = peak;
            while (beat > peak - (int)(0.030 * e.Rate) && high[At(beat - 1)] >= level)
            {
                beat--;
            }
        }
        return Wrap((beat / e.Rate) - (period / 2), period);
    }

    /// <summary>An envelope averaged over the beats (one beat from half a beat before each), smoothed by ±<paramref name="smooth"/> s.</summary>
    private static double[]? Average(IReadOnlyList<float> envelope, double rate, double period, int first, int count, double smooth)
    {
        var width = (int)Math.Floor(period * rate);
        if (width < 32 || count < 1)
        {
            return null;
        }
        var sum = new double[width];
        var used = 0;
        for (var k = first; k < first + count; k++)
        {
            var origin = ((k * period) - (period / 2)) * rate;
            var i0 = (int)origin;
            var frac = origin - i0;
            if (i0 < 0 || i0 + width + 1 >= envelope.Count)
            {
                continue;
            }
            for (var i = 0; i < width; i++)
            {
                sum[i] += (envelope[i0 + i] * (1 - frac)) + (envelope[i0 + i + 1] * frac);
            }
            used++;
        }
        if (used == 0)
        {
            return null;
        }
        var half = Math.Max(1, (int)Math.Round(smooth * rate));
        var result = new double[width];
        for (var i = 0; i < width; i++)
        {
            for (var j = -half; j <= half; j++)
            {
                result[i] += sum[((i + j) % width + width) % width];
            }
        }
        return result;
    }

    /// <summary><paramref name="value"/> wrapped into (−period/2, period/2].</summary>
    private static double Wrap(double value, double period) => value - (Math.Round(value / period) * period);

    /// <summary>The click and thump envelopes of [from, from + length) seconds of a file, and their rate.</summary>
    private (IReadOnlyList<float> Click, IReadOnlyList<float> Thump, double Rate)? Envelopes(string path, double from, double length)
    {
        var factory = factories.FirstOrDefault(f => f.CanOpen(path));
        if (factory is null || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var source = factory.Open(path);
            if (!source.CanSeek)
            {
                return null;
            }
            var format = source.Format;
            source.Seek((long)Math.Round(from * format.SampleRate));
            var frames = (long)(length * format.SampleRate);
            var run = new BeatAnalyzer.Run(format, frames);
            var buffer = new float[4096 * format.Channels];
            long read = 0;
            while (read < frames)
            {
                var n = source.Read(buffer.AsSpan(0, (int)Math.Min(4096, frames - read) * format.Channels));
                if (n == 0)
                {
                    break;
                }
                run.Process(buffer.AsSpan(0, n * format.Channels));
                read += n;
            }
            return read < frames / 2 ? null : (run.ClickEnvelope, run.ThumpEnvelope, run.EnvelopeRate);
        }
        catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
