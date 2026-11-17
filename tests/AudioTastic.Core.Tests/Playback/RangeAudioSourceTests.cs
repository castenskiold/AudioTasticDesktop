using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>FM-09: CUE virtual tracks.</summary>
public sealed class RangeAudioSourceTests
{
    [Fact]
    public void Reads_exactly_the_range_with_positions_relative_to_its_start()
    {
        var inner = new SyntheticSource(44_100, 2, 44_100 * 10);
        using var range = new RangeAudioSource(inner, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var buffer = new float[1000 * 2];
        long total = 0;
        int n;
        var first = true;
        while ((n = range.Read(buffer)) > 0)
        {
            if (first)
            {
                Assert.Equal(SyntheticSource.ValueAt(88_200, 0), buffer[0]);
                first = false;
            }
            total += n;
        }

        Assert.Equal(44_100, total);
        Assert.Equal(44_100, range.LengthFrames);
        Assert.Equal(44_100, range.PositionFrames);
    }

    [Fact]
    public void Seek_is_relative_to_the_range_and_clamped()
    {
        var inner = new SyntheticSource(44_100, 2, 44_100 * 10);
        using var range = new RangeAudioSource(inner, TimeSpan.FromSeconds(2), null);

        range.Seek(100);
        Assert.Equal(100, range.PositionFrames);
        Assert.Equal(88_300, inner.PositionFrames);
        Assert.Equal(44_100 * 8, range.LengthFrames);

        range.Seek(long.MaxValue / 2);
        Assert.Equal(range.LengthFrames, range.PositionFrames);
    }

    [Fact]
    public async Task Engine_plays_cue_tracks_as_separate_tracks_with_their_own_length()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-cue-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var factory = new SyntheticFactory();
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([factory], output, new SettingsStore(paths), NullLogger<PlaybackEngine>.Instance);
        var file = TempFiles.Touch(SyntheticFactory.PathFor("album", seconds: 1));
        var playlist = new Playlist("cue",
        [
            new PlaylistEntry(file, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)),
            new PlaylistEntry(file, TimeSpan.FromSeconds(0.4), null),
        ]);
        var durations = new List<TimeSpan?>();
        engine.TrackChanged += (_, e) => { lock (durations) { durations.Add(e.Track?.Duration); } };

        await engine.PlayAsync(playlist, playlist.Entries[0]);
        await Wait.UntilAsync(() => engine.State == PlaybackState.Stopped);

        lock (durations)
        {
            Assert.Equal([TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.6)], durations.Where(d => d is not null));
        }
        Assert.Same(playlist, engine.PlayingPlaylist);
        Assert.Same(playlist.Entries[1], engine.CurrentEntry);
    }

    [Fact]
    public async Task Next_follows_the_current_entry_after_the_playlist_is_reordered()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-order-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([new SyntheticFactory()], output, new SettingsStore(paths), NullLogger<PlaybackEngine>.Instance);
        var entries = Enumerable.Range(0, 4).Select(i => new PlaylistEntry(TempFiles.Touch(SyntheticFactory.PathFor($"t{i}", seconds: 30)))).ToArray();
        var playlist = new Playlist("p", entries);

        await engine.PlayAsync(playlist, entries[1]);
        playlist.MoveTo([entries[1]], 4); // the playing entry moves to the end
        playlist.MoveTo([entries[3]], 0);
        await engine.PreviousAsync(); // under 3 s: previous entry in the new order

        Assert.Same(entries[2], engine.CurrentEntry);
    }
}
