using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists.Formats;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>P3 exit criterion: CUE album rips play as separate tracks with correct times (FM-09).</summary>
public sealed class CueTrackTests
{
    public CueTrackTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    [Theory]
    [InlineData("sweep.flac.flac")]
    [InlineData("sweep.wavpack.wv")]
    public void Cue_tracks_have_exact_lengths_and_join_to_the_whole_file(string file)
    {
        var path = TestMedia.Require(file);
        var cue = $"""
            FILE "{Path.GetFileName(path)}" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 00:04:24
              TRACK 03 AUDIO
                TITLE "Three"
                INDEX 01 00:07:00
            """;
        var items = CueParser.ToItems(CueParser.Parse(cue), Path.GetDirectoryName(path)!);
        float[] whole;
        using (var source = FfmpegAudioSource.Open(path))
        {
            whole = Signals.ReadAll(source);
        }

        var joined = new List<float>();
        var lengths = new List<long>();
        foreach (var item in items)
        {
            using var track = new RangeAudioSource(FfmpegAudioSource.Open(path), item.Start!.Value, item.End);
            var samples = Signals.ReadAll(track);
            lengths.Add(samples.Length / 2);
            Assert.Equal(samples.Length / 2, track.LengthFrames);
            joined.AddRange(samples);
        }

        // 00:04:24 = 4 s + 24/75 s = 4.32 s = 190,512 samples at 44.1 kHz; 00:07:00 = 308,700.
        Assert.Equal([190_512L, 308_700 - 190_512, (whole.Length / 2) - 308_700], lengths);
        Assert.Equal(Signals.Sha256(whole), Signals.Sha256(joined.ToArray()));
    }

    [Fact]
    public void Seeking_inside_a_cue_track_is_relative_to_the_track()
    {
        var path = TestMedia.Require("sweep.flac.flac");
        float[] whole;
        using (var source = FfmpegAudioSource.Open(path))
        {
            whole = Signals.ReadAll(source);
        }
        using var track = new RangeAudioSource(FfmpegAudioSource.Open(path), TimeSpan.FromSeconds(4.32), TimeSpan.FromSeconds(7));

        track.Seek(1000);
        var got = Signals.ReadFrames(track, 500);

        Assert.Equal(1000 + 500, track.PositionFrames);
        Assert.True(whole.AsSpan((190_512 + 1000) * 2, got.Length).SequenceEqual(got));
    }
}
