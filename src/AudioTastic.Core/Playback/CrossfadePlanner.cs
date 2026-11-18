using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Playback;

/// <summary>What the planner knows about one side of a transition.</summary>
/// <param name="Duration">Track length (s).</param>
/// <param name="Beats">AN-02 analysis, or null.</param>
/// <param name="Speed">Deck speed the track plays at (source seconds per output second).</param>
public sealed record CrossfadeTrack(double Duration, BeatInfo? Beats, double Speed = 1);

/// <summary>
/// XF-01..XF-04: one planned transition. Times are track seconds of the outgoing (<see cref="MixStart"/>) and incoming
/// (<see cref="IncomingStart"/>) track; lengths are output seconds.
/// </summary>
public sealed record CrossfadePlan(
    CrossfadeMode Mode,
    double MixStart,
    double IncomingStart,
    double IncomingSpeed,
    double Overlap,
    EnvelopeShape OutgoingShape,
    double OutgoingFadeStart,
    double OutgoingFadeLength,
    double IncomingFadeLength,
    double BassFadeLength,
    double? OutgoingBpm,
    double? IncomingBpm,
    TempoReturnMode? TempoReturn,
    double TempoReturnSeconds,
    double? TempoReturnAt,
    string? Fallback)
{
    /// <summary>Output seconds from the mix start until the outgoing track is silent.</summary>
    public double OutgoingTail => OutgoingFadeStart + OutgoingFadeLength;

    /// <summary>The incoming track's tempo during the mix (BPM), when known.</summary>
    public double? MatchedBpm => IncomingBpm is { } bpm ? bpm * IncomingSpeed : null;
}

/// <summary>
/// XF-01..XF-04: chooses how two tracks are mixed (pure; unit-tested).
/// <list type="bullet">
/// <item>Intro/Outro (XF-01): the overlap ends where the outgoing track ends; equal-power fade-out and fade-in; the
/// overlap is shortened to half of the shorter track if needed.</item>
/// <item>Beat matching (XF-02): needs both analyses (confidence ≥ 0.5). The mix starts on the outgoing track's phrase
/// boundary at or before (beat end − <c>MixBeforeBeatEndSeconds</c>); the incoming track enters on its phrase boundary at
/// or after (beat start + <c>MixInAfterBeatStartSeconds</c>), sped up or slowed down (pitch kept) to the outgoing tempo.
/// The incoming track fades in (and its bass comes up from −24 dB) over the fade-in time (Anders, 18 Nov 2026); then
/// both play at full volume for the overlap; then the outgoing track is cut (10 ms) or fades out.</item>
/// <item>Tempo limit (XF-03): the needed change, also at half or double time, must be within the limit; else Intro/Outro.</item>
/// <item>Tempo return (XF-04): cut at the incoming track's next break after the mix, or gradually; no break → gradual.</item>
/// </list>
/// </summary>
public static class CrossfadePlanner
{
    /// <summary>Beat matching needs at least this analysis confidence on both tracks.</summary>
    public const double MinConfidence = 0.5;

    /// <summary>XF-03: same tempo, double time, half time.</summary>
    private static readonly double[] TempoFactors = [1.0, 2.0, 0.5];

    /// <summary>Length of the "cut" at the end of a beat-matched overlap (avoids a click).</summary>
    public const double CutSeconds = 0.01;

    /// <summary>Plans the transition; <paramref name="now"/> is the outgoing track's position (s).</summary>
    public static CrossfadePlan Plan(CrossfadingSettings settings, CrossfadeTrack outgoing, CrossfadeTrack incoming, double now,
        bool canSeekIncoming = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        string? fallback = null;
        if (settings.Mode == CrossfadeMode.BeatMatch)
        {
            if (TryBeatMatch(settings, outgoing, incoming, now, canSeekIncoming, out var plan, out fallback))
            {
                return plan!;
            }
        }
        return IntroOutro(settings, outgoing, incoming, fallback);
    }

    private static CrossfadePlan IntroOutro(CrossfadingSettings settings, CrossfadeTrack outgoing, CrossfadeTrack incoming, string? fallback)
    {
        // The overlap is in output time; the outgoing track covers overlap × speed of its own time.
        var overlap = Math.Clamp(settings.OverlapSeconds, 0.5, 30);
        overlap = Math.Min(overlap, Math.Min(outgoing.Duration / outgoing.Speed, incoming.Duration / incoming.Speed) / 2);
        var mixStart = Math.Max(0, outgoing.Duration - (overlap * outgoing.Speed));
        return new CrossfadePlan(CrossfadeMode.IntroOutro, mixStart, 0, incoming.Speed, overlap,
            EnvelopeShape.FadeOut, 0, overlap, overlap, settings.BassFadeIn ? overlap : 0, outgoing.Beats?.Bpm, incoming.Beats?.Bpm,
            null, 0, null, fallback);
    }

    private static bool TryBeatMatch(CrossfadingSettings settings, CrossfadeTrack outgoing, CrossfadeTrack incoming, double now,
        bool canSeekIncoming, out CrossfadePlan? plan, out string? fallback)
    {
        plan = null;
        if (outgoing.Beats is not { } outBeats || incoming.Beats is not { } inBeats)
        {
            fallback = "beat analysis missing";
            return false;
        }
        if (outBeats.Confidence < MinConfidence || inBeats.Confidence < MinConfidence)
        {
            fallback = "beat analysis not confident enough";
            return false;
        }
        if (!canSeekIncoming)
        {
            fallback = "the incoming track cannot seek";
            return false;
        }

        // XF-03: tempo ratio, trying half and double time; the smallest change wins.
        var outBpm = outBeats.Bpm * outgoing.Speed;
        var best = double.NaN;
        foreach (var m in TempoFactors)
        {
            var ratio = outBpm / (inBeats.Bpm * m);
            if (double.IsNaN(best) || Math.Abs(Math.Log(ratio)) < Math.Abs(Math.Log(best)))
            {
                best = ratio;
            }
        }
        var limit = Math.Clamp(settings.TempoLimitPercent, 1, 20) / 100;
        if (Math.Abs(best - 1) > limit)
        {
            fallback = $"tempo difference {Math.Abs(best - 1) * 100:0.0} % is over the {limit * 100:0} % limit";
            return false;
        }
        // The outgoing tempo already includes its deck speed, so the ratio is the incoming deck speed itself.
        var incomingSpeed = Math.Clamp(best, 0.5, 2.0);

        // Output seconds after the mix start that the outgoing track must still have: the incoming fade-in (Anders),
        // the overlap at full volume, then the cut or fade-out.
        var fadeIn = Math.Clamp(settings.FadeInSeconds, 0.5, 30);
        var overlap = Math.Clamp(settings.OverlapSeconds, 0.5, 30);
        var fadeLength = settings.EndBehaviour == CrossfadeEnd.FadeOut ? Math.Clamp(settings.FadeOutSeconds, 0.1, 60) : CutSeconds;
        var tail = (fadeIn + overlap + fadeLength) * outgoing.Speed; // outgoing track seconds

        // XF-02: mix start on the phrase at or before (beat end - setting), early enough to fit the tail.
        var beatEnd = outBeats.BeatEnd > 0 ? outBeats.BeatEnd : outgoing.Duration;
        var target = Math.Min(beatEnd - Math.Max(0, settings.MixBeforeBeatEndSeconds), outgoing.Duration - tail);
        if (outBeats.PhraseAtOrBefore(target) is not { } phrase)
        {
            fallback = "no phrase boundary before the mix point";
            return false;
        }
        var mixStart = outBeats.BeatTime(phrase);
        if (mixStart < now + 1)
        {
            // The set mix point has passed (a seek near the end): the next phrase that still fits.
            var next = outBeats.BeatTime(outBeats.PhraseAtOrAfter(now + 3));
            if (next > outgoing.Duration - tail)
            {
                fallback = "too late to mix on a phrase boundary";
                return false;
            }
            mixStart = next;
        }

        // The incoming track enters on its phrase at or after (beat start + setting).
        var entry = inBeats.BeatStart + Math.Max(0, settings.MixInAfterBeatStartSeconds);
        var incomingStart = inBeats.BeatTime(inBeats.PhraseAtOrAfter(entry));
        if (incomingStart >= incoming.Duration - ((fadeIn + overlap + 1) * incomingSpeed))
        {
            fallback = "the incoming track is too short after its entry point";
            return false;
        }

        // Align the kicks where the mix happens: each track's kick can sit a little off its grid in some sections
        // (AN-02 corrections). The incoming entry moves so its kick meets the outgoing kick at the mix start:
        // entry + its offset - outgoing offset (scaled to the incoming tempo).
        // Measured where it is heard: averaged over the overlap at full volume (after the fade-in).
        var deltaOut = outBeats.AverageOffset(mixStart + (fadeIn * outgoing.Speed), mixStart + ((fadeIn + overlap) * outgoing.Speed));
        var deltaIn = inBeats.AverageOffset(incomingStart + (fadeIn * incomingSpeed), incomingStart + ((fadeIn + overlap) * incomingSpeed));
        incomingStart = Math.Max(0, incomingStart + deltaIn - (deltaOut * incomingSpeed / outgoing.Speed));

        // XF-04: tempo return after the outgoing track is silent: at the next break, or gradually.
        TempoReturnMode? tempoReturn = Math.Abs(incomingSpeed - incoming.Speed) < 1e-9 ? null : settings.TempoReturn;
        double? returnAt = null;
        if (tempoReturn == TempoReturnMode.CutAtNextBreak)
        {
            var silentAt = incomingStart + ((fadeIn + overlap + fadeLength) * incomingSpeed);
            if (inBeats.BreakAfter(silentAt) is { } brk)
            {
                returnAt = brk.Start;
            }
            else
            {
                tempoReturn = TempoReturnMode.Gradual; // no break: return gradually (fallback agreed with Anders)
            }
        }
        plan = new CrossfadePlan(CrossfadeMode.BeatMatch, mixStart, incomingStart, incomingSpeed, overlap,
            settings.EndBehaviour == CrossfadeEnd.FadeOut ? EnvelopeShape.FadeOutDb : EnvelopeShape.FadeOut,
            fadeIn + overlap, fadeLength, fadeIn, settings.BassFadeIn ? fadeIn : 0, outBpm, inBeats.Bpm, tempoReturn, Math.Clamp(settings.TempoReturnSeconds, 1, 120), returnAt, null);
        fallback = null;
        return true;
    }
}
