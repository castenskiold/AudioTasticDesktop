using AudioTastic.Core.Playlists;
using AudioTastic.Sdk.Metadata;

namespace AudioTastic.Core.Tests.Playlists;

/// <summary>PL-01 editing and undo, PL-06 sorting/duplicates/dead entries, PL-05 search.</summary>
public sealed class PlaylistTests
{
    private static PlaylistEntry E(string name, string? artist = null, int? track = null, TimeSpan? duration = null) =>
        new(Path.Combine(Path.GetTempPath(), name + ".flac"), tags: new TrackTags(name, artist, TrackNumber: track, Duration: duration));

    private static string[] Names(Playlist p) => [.. p.Entries.Select(e => e.Tags!.Title!)];

    [Fact]
    public void Add_insert_remove_and_crop_with_undo_and_redo()
    {
        var a = E("a");
        var b = E("b");
        var c = E("c");
        var p = new Playlist("t", [a, c]);

        p.Add([b], 1);
        Assert.Equal(["a", "b", "c"], Names(p));
        p.Remove([a]);
        Assert.Equal(["b", "c"], Names(p));
        p.Crop([c]);
        Assert.Equal(["c"], Names(p));

        p.Undo();
        Assert.Equal(["b", "c"], Names(p));
        p.Undo();
        p.Undo();
        Assert.Equal(["a", "c"], Names(p));
        Assert.False(p.CanUndo);
        p.Redo();
        Assert.Equal(["a", "b", "c"], Names(p));
        Assert.True(p.CanRedo);
    }

    [Fact]
    public void Move_by_keeps_the_block_together_and_stops_at_the_edges()
    {
        var entries = "abcde".Select(ch => E(ch.ToString())).ToArray();
        var p = new Playlist("t", entries);

        p.MoveBy([entries[1], entries[2]], -1);
        Assert.Equal(["b", "c", "a", "d", "e"], Names(p));
        p.MoveBy([entries[1], entries[2]], -1); // already at the top
        Assert.Equal(["b", "c", "a", "d", "e"], Names(p));
        p.MoveBy([entries[0]], +1);
        Assert.Equal(["b", "c", "d", "a", "e"], Names(p));
    }

    [Fact]
    public void Move_to_inserts_the_selection_at_the_drop_position()
    {
        var entries = "abcde".Select(ch => E(ch.ToString())).ToArray();
        var p = new Playlist("t", entries);

        p.MoveTo([entries[0], entries[1]], 4); // drop before 'e'
        Assert.Equal(["c", "d", "a", "b", "e"], Names(p));
        p.MoveTo([entries[4]], 0);
        Assert.Equal(["e", "c", "d", "a", "b"], Names(p));
    }

    [Fact]
    public void Natural_sort_and_sort_of_a_selection_only()
    {
        var p = new Playlist("t", [E("Track 10"), E("track 2"), E("Track 1"), E("Ålborg"), E("Aalborg")]);

        p.Sort(PlaylistSortKey.Title);
        Assert.Equal(["Aalborg", "Ålborg", "Track 1", "track 2", "Track 10"], Names(p));

        var selection = new[] { p.Entries[2], p.Entries[4] };
        p.Sort(PlaylistSortKey.Title, descending: true, selection);
        Assert.Equal(["Aalborg", "Ålborg", "Track 10", "track 2", "Track 1"], Names(p));
    }

    [Fact]
    public void Album_sort_orders_by_disc_and_track_number()
    {
        var p = new Playlist("t",
        [
            new PlaylistEntry("c.flac", tags: new TrackTags("c", Album: "X", TrackNumber: 3)),
            new PlaylistEntry("a.flac", tags: new TrackTags("a", Album: "X", TrackNumber: 1, DiscNumber: 2)),
            new PlaylistEntry("b.flac", tags: new TrackTags("b", Album: "X", TrackNumber: 10)),
        ]);

        p.Sort(PlaylistSortKey.Album);

        Assert.Equal(["c", "b", "a"], Names(p)); // disc 1 (unknown) tracks 3, 10, then disc 2
    }

    [Fact]
    public void Reverse_and_randomise_keep_every_entry()
    {
        var p = new Playlist("t", "abcdef".Select(ch => E(ch.ToString())));

        p.Reverse();
        Assert.Equal(["f", "e", "d", "c", "b", "a"], Names(p));
        p.Randomise(new Random(1));
        Assert.Equal(6, Names(p).Distinct().Count());
    }

    [Fact]
    public void Duplicates_are_same_file_and_same_cue_range()
    {
        var t = TimeSpan.FromSeconds(10);
        var p = new Playlist("t",
        [
            new PlaylistEntry("x.flac"),
            new PlaylistEntry("X.FLAC"),
            new PlaylistEntry("x.flac", TimeSpan.Zero, t),
            new PlaylistEntry("x.flac", t, null),
            new PlaylistEntry("x.flac", t, null),
        ]);

        var removed = p.RemoveDuplicates();

        Assert.Equal(OperatingSystem.IsWindows() ? 2 : 1, removed);
        Assert.Contains(p.Entries, e => e.Start == t);
    }

    [Fact]
    public void Dead_entries_are_removed_but_urls_kept()
    {
        var p = new Playlist("t", [new PlaylistEntry("here.mp3"), new PlaylistEntry("gone.mp3"), new PlaylistEntry("http://radio/stream")]);

        Assert.Equal(1, p.RemoveDead(path => path == "here.mp3"));
        Assert.Equal(2, p.Count);
        p.Undo();
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Version_and_changed_event_follow_every_edit()
    {
        var p = new Playlist("t");
        var changes = 0;
        p.Changed += (_, e) => changes += e.Change == PlaylistChange.Entries ? 1 : 0;

        p.Add([E("a")]);
        p.Clear();
        p.Undo();

        Assert.Equal(3, p.Version);
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData("rawr", true)]
    [InlineData("horror rawr", true)]
    [InlineData("RAWR fox", true)]
    [InlineData("arhus", true)]   // accent-insensitive: Århus
    [InlineData("rawr nope", false)]
    [InlineData("", true)]
    public void Search_matches_all_words_case_and_accent_insensitively(string query, bool match)
    {
        var entry = new PlaylistEntry(@"E:\music\HorrorFox - Rawr.wav", tags: new TrackTags("Rawr", "HorrorFox", "Live i Århus"));

        Assert.Equal(match, new SearchFilter(query).Matches(entry));
    }

    [Fact]
    public void Cue_entry_duration_is_the_range()
    {
        var entry = new PlaylistEntry("album.flac", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(245));
        var last = new PlaylistEntry("album.flac", TimeSpan.FromSeconds(245), null, new TrackTags(Duration: TimeSpan.FromSeconds(300)));

        Assert.Equal(TimeSpan.FromSeconds(185), entry.Duration);
        Assert.Equal(TimeSpan.FromSeconds(55), last.Duration);
    }
}
