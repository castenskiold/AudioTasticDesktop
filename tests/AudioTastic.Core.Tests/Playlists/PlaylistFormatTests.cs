using System.Text;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Playlists.Formats;
using AudioTastic.Data;
using AudioTastic.Sdk.Playlists;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playlists;

/// <summary>PL-03 formats (round trip), FM-09 CUE parsing, PL-01/PL-02 persistence, PL-04 resolving.</summary>
public sealed class PlaylistFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-playlists-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Music(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, "music", relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private PlaylistItem[] SampleItems() =>
    [
        new(Music("Artist/01 Første sang.flac"), "Første sang", "Ærø Band", TimeSpan.FromSeconds(185)),
        new(Music("Artist/02 Second.mp3"), "Second", null, TimeSpan.FromSeconds(61)),
        new(Music("album.flac"), "Cue one", "Performer", TimeSpan.FromSeconds(120), TimeSpan.Zero, TimeSpan.FromSeconds(120)),
        new(Music("album.flac"), "Cue two", "Performer", null, TimeSpan.FromSeconds(120), null),
        new("http://radio.example/stream", "Radio", null, null),
    ];

    public static TheoryData<string, bool> Formats => new()
    {
        { ".m3u8", false }, { ".m3u8", true }, { ".m3u", true }, { ".pls", false }, { ".pls", true },
        { ".xspf", false }, { ".xspf", true }, { ".wpl", false }, { ".wpl", true }, { ".cue", true },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Import_export_import_gives_the_same_list(string extension, bool relative)
    {
        var format = PlaylistFormats.For("x" + extension)!;
        var items = SampleItems();
        if (extension == ".cue")
        {
            items = [.. items.Where(i => !i.Path.Contains("://", StringComparison.Ordinal))]; // CUE cannot hold URLs
        }
        var first = Path.Combine(_dir, "first" + extension);
        format.Write(first, items, relative);
        var imported = format.Read(first);

        var second = Path.Combine(_dir, "second" + extension);
        format.Write(second, imported, relative);
        var again = format.Read(second);

        Assert.Equal(imported, again);
        Assert.Equal(items.Select(i => i.Path), imported.Select(i => i.Path));
        if (extension is ".m3u8" or ".m3u" or ".xspf" or ".cue")
        {
            Assert.Equal(items.Select(i => i.Start), imported.Select(i => i.Start)); // CUE ranges survive
        }
    }

    [Fact]
    public void Relative_paths_are_written_relative_and_resolved_against_the_playlist_folder()
    {
        var items = SampleItems()[..2];
        var file = Path.Combine(_dir, "music", "list.m3u8");
        new M3uFormat().Write(file, items, relativePaths: true);

        var text = File.ReadAllText(file);
        Assert.DoesNotContain(_dir, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(items.Select(i => i.Path), new M3uFormat().Read(file).Select(i => i.Path));
    }

    [Fact]
    public void M3u_in_the_legacy_code_page_is_read_correctly()
    {
        var file = Path.Combine(_dir, "old.m3u");
        File.WriteAllBytes(file, PlaylistText.Legacy.GetBytes("#EXTM3U\n#EXTINF:10,Æble\nÆble.mp3\n"));

        var item = new M3uFormat().Read(file).Single();

        if (PlaylistText.Legacy.GetString(PlaylistText.Legacy.GetBytes("Æ")) == "Æ")
        {
            Assert.Equal("Æble", item.Title);
            Assert.EndsWith("Æble.mp3", item.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cue_sheet_parses_tracks_times_and_performers()
    {
        var cue = """
            REM GENRE Electronic
            REM DATE 2026
            PERFORMER "Album Artist"
            TITLE "The Album"
            FILE "album.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Intro"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE Unquoted Title
                PERFORMER "Guest"
                INDEX 00 03:58:50
                INDEX 01 04:00:37
              TRACK 03 AUDIO
                TITLE "Outro"
                INDEX 01 07:30:74
            """;

        var sheet = CueParser.Parse(cue);
        var items = CueParser.ToItems(sheet, _dir);

        Assert.Equal("The Album", sheet.Title);
        Assert.Equal("Electronic", sheet.Genre);
        Assert.Equal(3, items.Count);
        Assert.Equal("Unquoted Title", items[1].Title);
        Assert.Equal("Guest", items[1].Artist);
        Assert.Equal("Album Artist", items[0].Artist);
        Assert.Equal(TimeSpan.Zero, items[0].Start);
        Assert.Equal(items[1].Start, items[0].End);
        // 04:00:37 = 240 s + 37/75 s; at 44.1 kHz that is exactly 240*44100 + 37*588 samples.
        Assert.Equal((240 * 44_100) + (37 * 588), (long)Math.Round(items[1].Start!.Value.Ticks * 44_100.0 / TimeSpan.TicksPerSecond));
        Assert.Null(items[2].End);
        Assert.Equal(2, items[1].TrackNumber);
    }

    [Fact]
    public void Cue_sheet_falls_back_to_the_same_name_with_another_audio_extension()
    {
        var flac = Music("rip.flac");
        File.WriteAllText(Path.Combine(_dir, "music", "rip.cue"), "FILE \"rip.wav\" WAVE\n TRACK 01 AUDIO\n  INDEX 01 00:00:00\n TRACK 02 AUDIO\n  INDEX 01 01:00:00\n");

        var items = new CueFormat().Read(Path.Combine(_dir, "music", "rip.cue"));

        Assert.All(items, i => Assert.Equal(flac, i.Path));
    }

    [Fact]
    public void Cue_sheet_in_the_legacy_code_page_is_decoded()
    {
        var bytes = Encoding.Latin1.GetBytes("TITLE \"Kunsten i Århus\"\n");
        var text = PlaylistText.Decode(bytes);

        Assert.Contains(PlaylistText.Legacy.GetString(Encoding.Latin1.GetBytes("Å")), text, StringComparison.Ordinal);
        Assert.Equal("Ærø", PlaylistText.Decode(Encoding.UTF8.GetBytes("Ærø")));
    }

    [Fact]
    public void Single_track_cue_from_zero_is_a_whole_file()
    {
        var items = CueParser.ToItems(CueParser.Parse("FILE \"x.flac\" WAVE\n TRACK 01 AUDIO\n  INDEX 01 00:00:00\n"), _dir);

        Assert.False(items.Single().IsVirtual);
    }

    [Fact]
    public async Task Service_saves_tabs_and_reloads_them()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        Guid secondId;
        using (var service = new PlaylistService(paths, NullLogger<PlaylistService>.Instance))
        {
            service.Active.Add(SampleItems().Select(PlaylistEntry.FromItem));
            var second = service.New("Chill", [new PlaylistEntry(Music("chill.mp3"))]);
            secondId = second.Id;
            service.SetScroll(second, 7);
            service.Flush();
        }

        using var reloaded = new PlaylistService(paths, NullLogger<PlaylistService>.Instance);
        reloaded.Load();

        Assert.Equal(["Main", "Chill"], reloaded.Playlists.Select(p => p.Name));
        Assert.Equal(secondId, reloaded.Active.Id);
        Assert.Equal(7, reloaded.Active.ScrollIndex);
        var main = reloaded.Playlists[0];
        Assert.Equal(5, main.Count);
        Assert.Equal("Ærø Band – Første sang", main.Entries[0].DisplayName); // titles come back without reading tags
        Assert.Equal(TimeSpan.FromSeconds(120), main.Entries[3].Start);

        var resolved = await reloaded.ResolveAsync([Path.Combine(_dir, "music")], p => p.EndsWith(".flac", StringComparison.Ordinal) || p.EndsWith(".mp3", StringComparison.Ordinal),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["album", "01 Første sang", "02 Second", "chill"], resolved.Select(e => e.FileName)); // natural path order
    }

    [Fact]
    public void Closing_the_last_tab_empties_it()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        using var service = new PlaylistService(paths, NullLogger<PlaylistService>.Instance);
        service.Active.Add([new PlaylistEntry(Music("a.mp3"))]);

        service.Close(service.Active);

        Assert.Single(service.Playlists);
        Assert.Equal(0, service.Active.Count);
    }

    [Fact]
    public void A_100000_entry_playlist_saves_and_loads_in_under_3_seconds()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        using (var service = new PlaylistService(paths, NullLogger<PlaylistService>.Instance))
        {
            service.Active.Add(Enumerable.Range(0, 100_000).Select(i => new PlaylistEntry(
                Path.Combine(_dir, "music", $"Artist {i % 500}", $"{i:000000} Track number {i}.flac"),
                tags: new Sdk.Metadata.TrackTags($"Track number {i}", $"Artist {i % 500}", Duration: TimeSpan.FromSeconds(180 + (i % 120))))));
            service.Flush();
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var reloaded = new PlaylistService(paths, NullLogger<PlaylistService>.Instance);
        reloaded.Load();
        clock.Stop();

        Assert.Equal(100_000, reloaded.Active.Count);
        Assert.Equal("Artist 7 – Track number 7", reloaded.Active.Entries[7].DisplayName);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Loading took {clock.Elapsed.TotalSeconds:0.00} s");
    }
}