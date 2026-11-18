using AudioTastic.Core.Audio.Beats;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis.Tests;

/// <summary>
/// AN-02: BPM, beat grid, beat start/end and breaks on synthetic dance tracks with exactly known beats: a kick on every
/// beat, hi-hats on the off-beats and a bass note per bar; a hats-only intro, a break without kick in the middle and an
/// outro without kick. (The labelled real-music set is checked with tools/AudioTastic.MixLab.)
/// </summary>
[Collection(nameof(TimingTests))] // CPU-heavy: keep away from the waveform timing test
public sealed class BeatAnalysisTests
{
    [Theory]
    [InlineData(128.0, 44_100)]
    [InlineData(145.5, 44_100)]
    [InlineData(174.0, 48_000)]
    [InlineData(100.0, 96_000)]
    public void Tempo_grid_beat_start_end_and_breaks_are_found(double bpm, int rate)
    {
        var track = DanceTrack.Create(bpm, rate, introBeats: 32, mainBeats: 96, breakBeats: 32, outroBeats: 32, offset: 0.25);

        var info = Analyze(track.Samples, rate);

        Assert.InRange(info.Bpm, bpm - 0.05, bpm + 0.05);
        // The grid lands on the kicks: every analysed beat within 15 ms of a real kick (detector bias included).
        foreach (var k in new[] { 40, 100, 170, 250 })
        {
            var t = info.BeatTime(info.BeatAtOrAfter(track.KickTime(k) - 0.1));
            Assert.True(Math.Abs(t - track.KickTime(k)) < 0.015, $"beat {k}: grid {t:F4} s, kick {track.KickTime(k):F4} s");
        }
        Assert.InRange(info.BeatStart, track.KickTime(32) - (0.6 * info.Period), track.KickTime(32) + (1.1 * info.Period));
        var lastKick = track.KickTime(32 + 96 + 32 + 96 - 1);
        Assert.InRange(info.BeatEnd, lastKick, lastKick + (2.1 * info.Period));
        var brk = Assert.Single(info.Breaks);
        Assert.InRange(brk.Start, track.KickTime(128) - (1.5 * info.Period), track.KickTime(128) + (2.5 * info.Period));
        Assert.InRange(brk.End, track.KickTime(160) - (1.5 * info.Period), track.KickTime(160) + (1.5 * info.Period));
        Assert.True(info.Confidence > 0.8, $"confidence {info.Confidence:F2}");
    }

    [Fact]
    public void Phrases_start_where_the_bass_changes()
    {
        // The bass changes every 8 beats starting at beat 3 of the main part.
        var track = DanceTrack.Create(140, 44_100, introBeats: 0, mainBeats: 160, breakBeats: 0, outroBeats: 0, offset: 0.1, phraseShift: 3);

        var info = Analyze(track.Samples, 44_100);

        var k = info.PhraseAtOrAfter(track.KickTime(10));
        var expected = track.KickTime(11); // beats 3, 11, 19, ...
        Assert.True(Math.Abs(info.BeatTime(k) - expected) < 0.02 || Math.Abs(info.BeatTime(k) - track.KickTime(19)) < 0.02,
            $"phrase starts at {info.BeatTime(k):F3} s, expected {expected:F3} s");
    }

    [Fact]
    public void Beat_info_round_trips()
    {
        var info = new BeatInfo(145.25, 0.123, 0.9, 5, 300, [new BeatBreak(100, 120)], 10.5, 290.25);
        Assert.Equal(info.Serialize(), BeatInfo.Deserialize(info.Serialize())!.Serialize());
        Assert.Null(BeatInfo.Deserialize([99]));
    }

    internal static BeatInfo Analyze(float[] stereo, int rate)
    {
        var run = new BeatAnalyzer.Run(new AudioFormat(rate, 2));
        for (var offset = 0; offset < stereo.Length; offset += 8192 * 2)
        {
            run.Process(stereo.AsSpan(offset, Math.Min(8192 * 2, stereo.Length - offset)));
        }
        return run.Detect();
    }
}

/// <summary>A synthetic dance track with known kick times (stereo float).</summary>
internal sealed class DanceTrack
{
    private DanceTrack(float[] samples, double bpm, double offset)
    {
        Samples = samples;
        Bpm = bpm;
        Offset = offset;
    }

    public float[] Samples { get; }
    public double Bpm { get; }
    public double Offset { get; }

    /// <summary>Time of beat <paramref name="k"/> (counting the intro's beats too).</summary>
    public double KickTime(int k) => Offset + (k * 60 / Bpm);

    public static DanceTrack Create(double bpm, int rate, int introBeats, int mainBeats, int breakBeats, int outroBeats, double offset,
        int phraseShift = 0)
    {
        var period = 60 / bpm;
        var total = introBeats + mainBeats + breakBeats + mainBeats + outroBeats;
        var frames = (int)((offset + ((total + 2) * period)) * rate);
        var mono = new float[frames];
        var random = new Random(42);
        for (var k = 0; k < total; k++)
        {
            var t0 = offset + (k * period);
            var section = k < introBeats ? 0 : k < introBeats + mainBeats ? 1 : k < introBeats + mainBeats + breakBeats ? 2 : k < total - outroBeats ? 1 : 3;
            var drums = section == 1;
            if (drums)
            {
                // Kick: a decaying 55 Hz sine with a short click.
                Add(mono, rate, t0, 0.25, (t, i) => (float)((0.8 * Math.Sin(2 * Math.PI * 55 * t) * Math.Exp(-t * 18)) + (i < 40 ? 0.3 * (1 - (i / 40.0)) : 0)));
                // Bass note per phrase step (changes every 8 beats from phraseShift).
                var step = (((k - introBeats - phraseShift) % 8) + 8) % 8 == 0;
                var note = 80 + (20 * ((((k - introBeats - phraseShift) / 8) % 3 + 3) % 3));
                Add(mono, rate, t0 + (period * 0.5), period * 0.45, (t, _) => (float)((step ? 0.35 : 0.2) * Math.Sin(2 * Math.PI * note * t)));
            }
            // Hi-hat on the off-beat in every section (keeps time through intro and break).
            Add(mono, rate, t0 + (period * 0.5), 0.04, (t, _) => (float)(0.15 * ((random.NextDouble() * 2) - 1) * Math.Exp(-t * 80)));
            if (section != 1)
            {
                // A pad, so breaks are not silent.
                Add(mono, rate, t0, period, (t, _) => (float)(0.05 * Math.Sin(2 * Math.PI * 440 * t)));
            }
        }
        var stereo = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            stereo[i * 2] = stereo[(i * 2) + 1] = Math.Clamp(mono[i], -1, 1);
        }
        return new DanceTrack(stereo, bpm, offset);
    }

    private static void Add(float[] buffer, int rate, double start, double length, Func<double, int, float> shape)
    {
        var first = (int)Math.Round(start * rate);
        var count = (int)(length * rate);
        for (var i = 0; i < count && first + i < buffer.Length; i++)
        {
            if (first + i >= 0)
            {
                buffer[first + i] += shape(i / (double)rate, i);
            }
        }
    }
}
