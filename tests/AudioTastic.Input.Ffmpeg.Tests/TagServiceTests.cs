using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-01 part 1: tag reading, caching, embedded cue sheets; TagLoader for playlists.</summary>
public sealed class TagServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-tags-").FullName;
    private readonly CacheStore _cache;
    private readonly TagService _service;

    public TagServiceTests()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _cache = new CacheStore(paths);
        _service = new TagService(_cache, [new FfmpegSourceFactory()], NullLogger<TagService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string TaggedCopy(string source, Action<TagLib.File> tag)
    {
        var copy = Path.Combine(_dir, Path.GetFileName(source));
        File.Copy(source, copy, overwrite: true);
        using (var file = TagLib.File.Create(copy))
        {
            tag(file);
            file.Save();
        }
        return copy;
    }

    [Fact]
    public void Reads_tags_and_duration_and_caches_them()
    {
        var path = TaggedCopy(TestMedia.Require("sine1k.flac.flac"), f =>
        {
            f.Tag.Title = "Første sang";
            f.Tag.Performers = ["Ærø Band"];
            f.Tag.Album = "Album";
            f.Tag.Track = 3;
            f.Tag.Year = 2026;
            f.Tag.Genres = ["Electronic"];
        });

        var tags = _service.Read(path)!;

        Assert.Equal("Første sang", tags.Title);
        Assert.Equal("Ærø Band", tags.Artist);
        Assert.Equal(3, tags.TrackNumber);
        Assert.Equal(2026, tags.Year);
        Assert.Equal(5, tags.Duration!.Value.TotalSeconds, 2);
        Assert.Equal(1, _cache.FileCount());
        Assert.Equal(tags, _service.Read(path)); // from the cache
    }

    [Fact]
    public void Missing_files_have_no_tags()
    {
        Assert.Null(_service.Read(Path.Combine(_dir, "missing.flac")));
    }

    [Fact]
    public void Embedded_cue_sheet_is_read_from_flac()
    {
        var cue = "FILE \"x.flac\" WAVE\n TRACK 01 AUDIO\n  TITLE \"A\"\n  INDEX 01 00:00:00\n TRACK 02 AUDIO\n  TITLE \"B\"\n  INDEX 01 00:02:00\n";
        var path = TaggedCopy(TestMedia.Require("sine1k.flac.flac"),
            f => ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, create: true)).SetField("CUESHEET", cue));

        Assert.Equal(cue, _service.ReadEmbeddedCueSheet(path));
    }

    [Fact]
    public async Task Resolving_a_file_with_an_embedded_cue_sheet_gives_virtual_tracks()
    {
        var cue = "FILE \"x.flac\" WAVE\n TRACK 01 AUDIO\n  TITLE \"A\"\n  INDEX 01 00:00:00\n TRACK 02 AUDIO\n  TITLE \"B\"\n  INDEX 01 00:02:00\n";
        var path = TaggedCopy(TestMedia.Require("sine1k.flac.flac"),
            f => ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, create: true)).SetField("CUESHEET", cue));
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch]));
        using var playlists = new PlaylistService(paths, NullLogger<PlaylistService>.Instance);

        var entries = await playlists.ResolveAsync([path], _ => true, _service.ReadEmbeddedCueSheet, TestContext.Current.CancellationToken);

        Assert.Equal(["A", "B"], entries.Select(e => e.Tags!.Title));
        Assert.All(entries, e => Assert.Equal(path, e.Path));
        Assert.Equal(TimeSpan.FromSeconds(2), entries[1].Start);
    }

    [Fact]
    public async Task Tag_loader_fills_entries_and_keeps_cue_titles()
    {
        var path = TaggedCopy(TestMedia.Require("sine1k.flac.flac"), f =>
        {
            f.Tag.Title = "File title";
            f.Tag.Album = "File album";
        });
        var whole = new PlaylistEntry(path, tags: new Sdk.Metadata.TrackTags("Old playlist text"));
        var cueTrack = new PlaylistEntry(path, TimeSpan.FromSeconds(2), null, new Sdk.Metadata.TrackTags("Cue title", "Cue artist"));
        var missing = new PlaylistEntry(Path.Combine(_dir, "gone.flac"));
        var playlist = new Playlist("p", [whole, cueTrack, missing]);
        using var loader = new TagLoader(_service, NullLogger<TagLoader>.Instance);
        var updated = new TaskCompletionSource();
        loader.Updated += (_, batch) =>
        {
            if (playlist.Entries.All(e => e.TagsLoaded))
            {
                updated.TrySetResult();
            }
        };

        loader.ProbePlaylist(playlist);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("File title", whole.Tags!.Title);
        Assert.Equal("Cue title", cueTrack.Tags!.Title);
        Assert.Equal("File album", cueTrack.Tags.Album);
        Assert.Equal(3, cueTrack.Duration!.Value.TotalSeconds, 1); // 5 s file from 2 s
        Assert.True(missing.IsMissing);
    }
}
