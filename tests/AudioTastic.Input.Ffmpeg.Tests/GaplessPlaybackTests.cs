using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// P6 exit criterion (PB-01): gapless playback for MP3, AAC, FLAC, Opus (and Vorbis) pairs and CUE tracks. The
/// engine's output must be exactly the two parts decoded on their own and joined: no gap, no overlap, no click.
/// </summary>
[Collection(nameof(RealTimeTests))] // plays in real time against a deadline: not alongside CPU-heavy tests
public sealed class GaplessPlaybackTests : IDisposable
{
    private readonly FakeAudioOutput _output = new() { Capture = true };
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;

    public GaplessPlaybackTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
        var dir = Directory.CreateTempSubdirectory("audiotastic-gapless-files-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(paths);
        _settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = 100);
        _engine = new PlaybackEngine([new FfmpegSourceFactory()], _output, _settings, NullLogger<PlaybackEngine>.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _output.Dispose();
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("aac", "m4a")]
    [InlineData("flac", "flac")]
    [InlineData("opus", "opus")]
    [InlineData("vorbis", "ogg")]
    public async Task Gapless_pairs_join_exactly(string format, string extension)
    {
        var part1 = TestMedia.Require($"gapless.{format}.part1.{extension}");
        var part2 = TestMedia.Require($"gapless.{format}.part2.{extension}");
        var expected = Decode(part1).Concat(Decode(part2)).ToArray();

        await _engine.OpenAsync([part1, part2]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 30_000);

        AssertPlayed(expected);
        Assert.Equal(1, _engine.Graph.Handovers);
    }

    [Fact]
    public async Task Cue_tracks_join_exactly()
    {
        var path = TestMedia.Require("sweep.flac.flac");
        var split = TimeSpan.FromSeconds(4.321);
        var playlist = new Playlist("cue", [new PlaylistEntry(path, TimeSpan.Zero, split), new PlaylistEntry(path, split, null)]);
        var expected = Decode(path);

        await _engine.PlayAsync(playlist, playlist.Entries[0]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 30_000);

        AssertPlayed(expected);
        Assert.Equal(1, _engine.Graph.Handovers);
    }

    private void AssertPlayed(float[] expected)
    {
        var captured = _output.Captured;
        var firstExpected = Array.FindIndex(expected, v => v != 0);
        var firstCaptured = Array.FindIndex(captured, v => v != 0);
        Assert.True(firstExpected >= 0 && firstCaptured >= 0, "No audio.");
        var start = firstCaptured - firstExpected;
        Assert.True(start >= 0 && captured.Length >= start + expected.Length, "Not enough audio captured.");
        var actual = captured.AsSpan(start, expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                Assert.Fail($"Sample {i} (frame {i / 2}) differs: expected {expected[i]}, got {actual[i]}.");
            }
        }
        // Nothing but silence after the second part.
        Assert.All(captured.AsSpan(start + expected.Length).ToArray(), v => Assert.Equal(0f, v));
    }

    private static float[] Decode(string path)
    {
        using var source = FfmpegAudioSource.Open(path);
        return Signals.ReadAll(source);
    }
}

/// <summary>Tests that play through the engine in real time; they run on their own so a busy CPU cannot starve them.</summary>
[CollectionDefinition(nameof(RealTimeTests), DisableParallelization = true)]
public sealed class RealTimeTests;
