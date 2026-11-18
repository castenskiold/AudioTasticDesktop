using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>XF-01..XF-04: the transition planner.</summary>
public sealed class CrossfadePlannerTests
{
    // 120 BPM (0.5 s beats), first beat at 0.1 s, phrases on beats 0, 8, 16, ...; drums from 16.1 s to 280.1 s.
    private static BeatInfo Beats(double bpm = 120, double first = 0.1, double beatStart = 16.1, double beatEnd = 280.1, params BeatBreak[] breaks) =>
        new(bpm, first, 0.9, 0, 300, breaks, beatStart, beatEnd);

    private static CrossfadingSettings BeatMatch() => new()
    {
        Enabled = true,
        Mode = CrossfadeMode.BeatMatch,
        OverlapSeconds = 16,
        FadeInSeconds = 8,
        EndBehaviour = CrossfadeEnd.FadeOut,
        FadeOutSeconds = 4,
        MixBeforeBeatEndSeconds = 30,
        MixInAfterBeatStartSeconds = 0,
        TempoLimitPercent = 8,
        TempoReturn = TempoReturnMode.Gradual,
        TempoReturnSeconds = 16,
    };

    [Fact]
    public void Intro_outro_ends_with_the_track_and_shortens_for_short_tracks()
    {
        var settings = new CrossfadingSettings { Enabled = true, OverlapSeconds = 10 };
        var plan = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, null), new CrossfadeTrack(200, null), 0);
        Assert.Equal(CrossfadeMode.IntroOutro, plan.Mode);
        Assert.Equal(290, plan.MixStart, 9);
        Assert.Equal(10, plan.IncomingFadeLength);
        Assert.Equal(EnvelopeShape.FadeOut, plan.OutgoingShape);
        Assert.Equal(10, plan.BassFadeLength); // bass comes in gradually by default

        var shortTrack = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, null), new CrossfadeTrack(12, null), 0);
        Assert.Equal(6, shortTrack.Overlap, 9); // half of the shorter track
    }

    [Fact]
    public void Beat_matching_mixes_on_phrases_from_the_beat_end_and_beat_start()
    {
        var plan = CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats(bpm: 125, beatStart: 20.1)), 0);

        Assert.Equal(CrossfadeMode.BeatMatch, plan.Mode);
        // Beat end 280.1 - 30 = 250.1, phrase at or before (and the fade-in + overlap + fade-out fit): beat 496 -> 248.1 s.
        Assert.Equal(248.1, plan.MixStart, 6);
        Assert.Equal(0, (int)Math.Round((plan.MixStart - 0.1) / 0.5) % 8);
        // The incoming track enters on its phrase at or after its beat start (20.1 s at 125 BPM: beat 41.875 -> 48).
        Assert.Equal(0.1 + (48 * 0.48), plan.IncomingStart, 6);
        Assert.Equal(120.0 / 125, plan.IncomingSpeed, 9);
        Assert.Equal(8, plan.IncomingFadeLength);
        Assert.Equal(8 + 16, plan.OutgoingFadeStart);
        Assert.Equal(EnvelopeShape.FadeOutDb, plan.OutgoingShape);
        Assert.Equal(TempoReturnMode.Gradual, plan.TempoReturn);
    }

    [Fact]
    public void Mix_in_after_the_beat_start_moves_the_entry_point()
    {
        var settings = BeatMatch();
        settings.MixInAfterBeatStartSeconds = 10;
        var plan = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats()), 0);
        // 16.1 + 10 = 26.1 s: beat 52 -> next phrase start, beat 56 = 28.1 s.
        Assert.Equal(28.1, plan.IncomingStart, 6);
    }

    [Theory]
    [InlineData(70, 2.0)]   // half time: 70 BPM plays as 140
    [InlineData(275, 0.5)]  // double time: 275 BPM plays as 137.5
    public void Half_and_double_time_count_for_the_tempo_limit(double incomingBpm, double factor)
    {
        var plan = CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, Beats(bpm: 140)), new CrossfadeTrack(300, Beats(bpm: incomingBpm)), 0);
        Assert.Equal(CrossfadeMode.BeatMatch, plan.Mode);
        Assert.Equal(140 / (incomingBpm * factor), plan.IncomingSpeed, 9);
    }

    [Fact]
    public void Too_big_a_tempo_change_low_confidence_or_missing_analysis_falls_back_to_intro_outro()
    {
        var tooFar = CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, Beats(bpm: 120)), new CrossfadeTrack(300, Beats(bpm: 135)), 0);
        Assert.Equal(CrossfadeMode.IntroOutro, tooFar.Mode);
        Assert.Contains("limit", tooFar.Fallback, StringComparison.Ordinal);

        var unsure = Beats() with { Confidence = 0.3 };
        Assert.Equal(CrossfadeMode.IntroOutro, CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, unsure), new CrossfadeTrack(300, Beats()), 0).Mode);
        Assert.Equal(CrossfadeMode.IntroOutro, CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, null), new CrossfadeTrack(300, Beats()), 0).Mode);
        Assert.Equal(CrossfadeMode.IntroOutro, CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats()), 0, canSeekIncoming: false).Mode);
    }

    [Fact]
    public void Tempo_returns_at_the_next_break_or_gradually_without_one()
    {
        var settings = BeatMatch();
        settings.TempoReturn = TempoReturnMode.CutAtNextBreak;
        var withBreak = Beats(bpm: 124, breaks: [new BeatBreak(10, 15), new BeatBreak(120, 140)]);
        var plan = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, withBreak), 0);
        Assert.Equal(TempoReturnMode.CutAtNextBreak, plan.TempoReturn);
        Assert.Equal(120, plan.TempoReturnAt); // the first break after the outgoing track is silent

        var noBreak = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats(bpm: 124)), 0);
        Assert.Equal(TempoReturnMode.Gradual, noBreak.TempoReturn);

        var sameTempo = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats()), 0);
        Assert.Null(sameTempo.TempoReturn); // nothing to return
    }

    [Fact]
    public void Kicks_are_aligned_with_the_local_corrections_at_the_mix_point()
    {
        // The outgoing kick sits 12 ms late around the mix point; the incoming kick 20 ms early at its entry.
        var outgoing = Beats() with { Corrections = [new PhaseCorrection(240.1, 0.012)] };
        var incoming = Beats(bpm: 125, beatStart: 20.1) with { Corrections = [new PhaseCorrection(20.1, -0.020)] };
        var plain = CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats(bpm: 125, beatStart: 20.1)), 0);
        var plan = CrossfadePlanner.Plan(BeatMatch(), new CrossfadeTrack(300, outgoing), new CrossfadeTrack(300, incoming), 0);

        Assert.Equal(plain.MixStart, plan.MixStart, 9);
        // entry + incoming offset - outgoing offset × (incoming speed / outgoing speed)
        Assert.Equal(plain.IncomingStart - 0.020 - (0.012 * plan.IncomingSpeed), plan.IncomingStart, 9);
        // A grid nudge moves everything.
        Assert.Equal(Beats().FirstBeat - 0.03, Beats().Nudged(-0.03).FirstBeat, 9);
    }

    [Fact]
    public void Cut_ends_the_outgoing_track_after_the_overlap()
    {
        var settings = BeatMatch();
        settings.EndBehaviour = CrossfadeEnd.Cut;
        var plan = CrossfadePlanner.Plan(settings, new CrossfadeTrack(300, Beats()), new CrossfadeTrack(300, Beats()), 0);
        Assert.Equal(CrossfadePlanner.CutSeconds, plan.OutgoingFadeLength);
        Assert.Equal(EnvelopeShape.FadeOut, plan.OutgoingShape);
        Assert.Equal(24, plan.OutgoingFadeStart);
    }

    [Fact]
    public void Envelopes_and_bass_ramp_follow_their_curves()
    {
        var fadeIn = new DeckEnvelope(100, 100, EnvelopeShape.FadeIn);
        Assert.Equal(0f, fadeIn.GainAt(50));
        Assert.Equal((float)Math.Sin(Math.PI / 4), fadeIn.GainAt(150), 5);
        Assert.Equal(1f, fadeIn.GainAt(250));
        var fadeOut = new DeckEnvelope(0, 100, EnvelopeShape.FadeOut);
        // Equal power: in² + out² = 1 at every point of a crossfade.
        Assert.Equal(1, Math.Pow(fadeOut.GainAt(37), 2) + Math.Pow(new DeckEnvelope(0, 100, EnvelopeShape.FadeIn).GainAt(37), 2), 5);
        Assert.Equal(0.001f, new DeckEnvelope(0, 100, EnvelopeShape.FadeOutDb).GainAt(99), 3);
        var bass = new BassRamp(0, 100);
        Assert.Equal(-24, bass.GainDbAt(0));
        Assert.Equal(-12, bass.GainDbAt(50), 9);
        Assert.Equal(0, bass.GainDbAt(100));
    }
}
