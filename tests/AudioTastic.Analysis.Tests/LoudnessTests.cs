using System.Text.Json;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.TestSupport;

namespace AudioTastic.Analysis.Tests;

/// <summary>PB-03: EBU R128 integrated loudness, checked on synthetic signals and against FFmpeg's ebur128 filter.</summary>
public sealed class LoudnessTests
{
    [Theory]
    [InlineData(48_000, -23.0)]
    [InlineData(44_100, -23.0)]
    [InlineData(48_000, -33.0)]
    [InlineData(96_000, -23.0)]
    public void Stereo_1k_sine_at_minus_23_dBFS_measures_minus_23_LUFS(int rate, double dbfs)
    {
        // EBU Tech 3341 case 1/2: a 1 kHz stereo sine at -23 (-33) dBFS reads -23 (-33) LUFS.
        var meter = new LoudnessMeter(rate, 2);
        meter.Process(Sine(rate, 2, 1000, Math.Pow(10, dbfs / 20), 20));

        Assert.InRange(meter.IntegratedLoudness, dbfs - 0.1, dbfs + 0.1);
    }

    [Fact]
    public void Silence_is_gated_out_and_quiet_parts_below_the_relative_gate_do_not_count()
    {
        var meter = new LoudnessMeter(48_000, 2);
        meter.Process(new float[48_000 * 2 * 5]);
        Assert.Equal(double.NegativeInfinity, meter.IntegratedLoudness);

        // 10 s at -23 dBFS plus 10 s at -50 dBFS: the quiet half is below the relative gate (-33 LU), so I stays -23.
        meter.Process(Sine(48_000, 2, 1000, Math.Pow(10, -23 / 20.0), 10));
        meter.Process(Sine(48_000, 2, 1000, Math.Pow(10, -50 / 20.0), 10));
        // (Blocks straddling a level change count with their mixed energy, so allow a little below -23.)
        Assert.InRange(meter.IntegratedLoudness, -23.2, -22.9);
    }

    [Fact]
    public void Peak_and_album_loudness()
    {
        var loud = new LoudnessMeter(48_000, 2);
        loud.Process(Sine(48_000, 2, 1000, 0.5, 10));
        var quiet = new LoudnessMeter(48_000, 2);
        quiet.Process(Sine(48_000, 2, 1000, 0.25, 10)); // 6 dB down: above the relative gate, so it counts
        var both = new LoudnessMeter(48_000, 2);
        both.Process(Sine(48_000, 2, 1000, 0.5, 10));
        both.Process(Sine(48_000, 2, 1000, 0.25, 10));

        Assert.InRange(loud.SamplePeak, 0.499f, 0.5f);
        var a = new LoudnessResult(loud.IntegratedLoudness, loud.SamplePeak, [.. loud.BlockEnergies]);
        var b = new LoudnessResult(quiet.IntegratedLoudness, quiet.SamplePeak, [.. quiet.BlockEnergies]);
        var (albumGain, albumPeak) = LoudnessResult.Album([a, b]);

        // The album is measured over both tracks' blocks, like one long track (gates apply across the album); only the
        // blocks that straddle the join differ.
        Assert.InRange(albumGain, LoudnessResult.ReferenceLufs - both.IntegratedLoudness - 0.1, LoudnessResult.ReferenceLufs - both.IntegratedLoudness + 0.1);
        Assert.True(albumGain > a.GainDb, "The quiet track makes the album quieter, so its gain is higher.");
        Assert.Equal(a.SamplePeak, albumPeak);
        Assert.Equal(LoudnessResult.ReferenceLufs - a.IntegratedLufs, a.GainDb, 6);
    }

    [Fact]
    public void Result_round_trips_through_the_cache_format()
    {
        var meter = new LoudnessMeter(44_100, 2);
        meter.Process(Sine(44_100, 2, 440, 0.3, 3));
        var result = new LoudnessResult(meter.IntegratedLoudness, meter.SamplePeak, [.. meter.BlockEnergies]);

        var copy = LoudnessResult.Deserialize(result.Serialize())!;

        Assert.Equal(result.IntegratedLufs, copy.IntegratedLufs);
        Assert.Equal(result.SamplePeak, copy.SamplePeak);
        Assert.Equal(result.BlockEnergies, copy.BlockEnergies);
        Assert.Null(LoudnessResult.Deserialize([1, 2, 3]));
    }

    public static TheoryData<string> ReferenceFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "sine1k.wav16.wav", "sweep.wav16.wav", "clicks.wav16.wav", "sine1k.mp3.mp3", "sine1k.aac.m4a",
                     "sweep.opus.opus", "gapless.reference.wav", "long5min.flac", "surround51.flac", "sine1k.hires-24-96.flac" })
        {
            data.Add(name);
        }
        return data;
    }

    /// <summary>P5 exit criterion: within 0.1 LU of a reference tool (FFmpeg's ebur128 filter, values in loudness.json).</summary>
    [Theory]
    [MemberData(nameof(ReferenceFiles))]
    public void Matches_ffmpeg_ebur128_within_0_1_LU(string file)
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
        var references = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(TestMedia.Require("loudness.json")))!;
        using var source = FfmpegAudioSource.Open(TestMedia.Require(file));
        var meter = new LoudnessMeter(source.Format.SampleRate, source.Format.Channels);
        meter.Process(Signals.ReadAll(source));

        Assert.InRange(meter.IntegratedLoudness, references[file] - 0.1, references[file] + 0.1);
    }

    internal static float[] Sine(int rate, int channels, double hz, double amplitude, double seconds)
    {
        var frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / rate));
            for (var c = 0; c < channels; c++)
            {
                samples[(f * channels) + c] = v;
            }
        }
        return samples;
    }
}
