using AudioTastic.Core.Audio.Beats;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis;

/// <summary>
/// XF-02: aligns a beat-matched transition from the audio of the overlap itself. Both tracks are decoded over the
/// full-volume overlap (the incoming one around its planned position) and the kick attack is found in each, per 8
/// beats, with the analyser's own kick detector (<see cref="BeatDetector.KickAttack"/>). The median difference, in
/// output time, moves the incoming entry point, so the kicks coincide where both tracks are loud, whatever small errors
/// either beat grid has there. A kick detector rather than a low-band correlation: a rolling bassline puts a bass note
/// every quarter beat, which a correlation within the search range can lock onto.
/// </summary>
public sealed class CrossfadeAligner(IEnumerable<IAudioSourceFactory> factories) : ICrossfadeAligner
{
    /// <summary>
    /// Corrections up to this size (output seconds, less than a 16th note at dance tempos) need a majority of the
    /// blocks; larger ones (up to half a beat) need three quarters.
    /// </summary>
    public const double Search = 0.060;

    /// <summary>Beats averaged per kick measurement.</summary>
    private const int BlockBeats = 8;

    /// <summary>How closely the blocks must agree with the median (output seconds) to count.</summary>
    private const double Agreement = 0.006;

    public double? Refine(CrossfadeAlignment alignment) => Measure(alignment).Shift;

    /// <summary>
    /// The per-block kick differences (output seconds, positive = incoming late) and the resulting shift (track
    /// seconds; null when the blocks do not agree). Also used by tools/MixLab.
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
        // entry point are both beats) and the number of 8-beat blocks in the full-volume overlap.
        var firstBeat = (int)Math.Ceiling((a.FadeIn / a.Period) - 1e-9);
        var blocks = (int)Math.Floor((((a.FadeIn + a.Overlap) / a.Period) - firstBeat + 1e-9) / BlockBeats);
        if (blocks < 1)
        {
            return (null, []);
        }
        var beats = blocks * BlockBeats;
        // Spans with one beat to spare on each side (a kick measurement looks half a beat either way of the grid).
        var outFrom = a.MixStart + ((firstBeat - 1) * periodOut);
        var inFrom = a.IncomingStart + ((firstBeat - 1) * periodIn);
        if (outFrom < 0 || inFrom < 0 ||
            Envelopes(a.OutgoingPath, outFrom, (beats + 2) * periodOut) is not { } outgoing ||
            Envelopes(a.IncomingPath, inFrom, (beats + 2) * periodIn) is not { } incoming)
        {
            return (null, []);
        }

        var differences = new List<double>(blocks);
        for (var b = 0; b < blocks; b++)
        {
            // Local times (from each span's start) of the block's first beat.
            var tOut = (1 + (b * BlockBeats)) * periodOut;
            var tIn = (1 + (b * BlockBeats)) * periodIn;
            var kickOut = BeatDetector.KickAttack(outgoing.Click, outgoing.Thump, outgoing.Rate, periodOut, tOut, tOut, tOut + (BlockBeats * periodOut));
            var kickIn = BeatDetector.KickAttack(incoming.Click, incoming.Thump, incoming.Rate, periodIn, tIn, tIn, tIn + (BlockBeats * periodIn));
            if (kickOut is { } ko && kickIn is { } ki)
            {
                // Positive: the incoming kicks come late (output seconds).
                differences.Add((Wrap(ki - tIn, periodIn) / a.IncomingSpeed) - (Wrap(ko - tOut, periodOut) / a.OutgoingSpeed));
            }
        }
        if (differences.Count == 0)
        {
            return (null, []);
        }
        // The answer most blocks agree on, on the beat circle (+195 and −197 ms are the same half-beat answer).
        var best = differences
            .Select(c => (Center: c, Agreeing: differences.Where(d => Math.Abs(Wrap(d - c, a.Period)) <= Agreement).ToList()))
            .OrderByDescending(x => x.Agreeing.Count).ThenBy(x => Math.Abs(x.Center)).First();
        var shift = Wrap(best.Center + best.Agreeing.Average(d => Wrap(d - best.Center, a.Period)), a.Period);
        var count = best.Agreeing.Count;
        // Most blocks must agree (a break or a kick without a clear attack gives scattered answers). A small correction
        // needs a majority; a large one (up to half a beat: a grid locked to the off-beat in that part of a track)
        // needs three blocks and three quarters of them.
        var trusted = Math.Abs(shift) <= Search
            ? count * 2 > differences.Count
            : count >= 3 && count * 4 >= differences.Count * 3;
        // Moving the entry later by s (track seconds) brings the incoming kicks s / speed earlier in output time.
        return (trusted ? shift * a.IncomingSpeed : null, differences);
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
