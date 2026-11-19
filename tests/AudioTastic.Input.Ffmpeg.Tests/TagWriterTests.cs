using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// MD-01 part 2 and the P9 exit criterion: tag edits round-trip on every writable format without changing the audio
/// (the decoded samples are bit-identical before and after).
/// </summary>
public sealed class TagWriterTests : IDisposable
{
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0xF8, 0xFF, 0xFF, 0x3F, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0xA7, 0x35, 0x81, 0x84, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-tagwrite-").FullName;
    private readonly CacheStore _cache;
    private readonly TagService _tags;
    private readonly TagWriter _writer;

    public TagWriterTests()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _cache = new CacheStore(paths);
        _tags = new TagService(_cache, [new FfmpegSourceFactory()], NullLogger<TagService>.Instance);
        _writer = new TagWriter(_tags, _cache, NullLogger<TagWriter>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<string> WritableFormats =>
    [
        "sine1k.wav16.wav", "sine1k.aiff.aiff", "sine1k.flac.flac", "sine1k.mp3.mp3", "sine1k.opus.opus", "sine1k.vorbis.ogg",
        "sine1k.aac.m4a", "sine1k.alac.m4a", "sine1k.wavpack.wv", "sine1k.wma.wma",
    ];

    [Theory]
    [MemberData(nameof(WritableFormats))]
    public void Tag_edits_round_trip_and_leave_the_audio_unchanged(string name)
    {
        var path = Copy(name);
        var audioBefore = AudioHash(path);
        Assert.True(TagWriter.CanWrite(path));

        var fields = new Dictionary<TagField, string?>
        {
            [TagField.Title] = "Første sang",
            [TagField.Artist] = "Ærø Band",
            [TagField.AlbumArtist] = "Various",
            [TagField.Album] = "Prøve",
            [TagField.Track] = "3",
            [TagField.TrackCount] = "12",
            [TagField.Disc] = "1",
            [TagField.DiscCount] = "2",
            [TagField.Year] = "2026",
            [TagField.Genre] = "Psytrance",
            [TagField.Composer] = "Anders",
            [TagField.Comment] = "Tagged by a test",
            [TagField.Lyrics] = "La la la",
        };
        _writer.Write(path, new TagEdit(fields, new CoverChange(new TagPicture(Png, "image/png"))));

        var read = _writer.Read(path)!;
        foreach (var (field, value) in fields)
        {
            Assert.Equal(value, read[field]);
        }
        Assert.Equal(Png, read.Cover!.Data);
        Assert.Equal(audioBefore, AudioHash(path));
        Assert.Equal("Første sang", _tags.Read(path)!.Title); // the cached tags were dropped

        // Clearing fields and the cover round-trips too.
        _writer.Write(path, new TagEdit(new Dictionary<TagField, string?> { [TagField.Comment] = null, [TagField.Lyrics] = "" }, CoverChange.Remove));
        read = _writer.Read(path)!;
        Assert.Null(read[TagField.Comment]);
        Assert.Null(read[TagField.Lyrics]);
        Assert.Null(read.Cover);
        Assert.Equal("Prøve", read[TagField.Album]);
        Assert.Equal(audioBefore, AudioHash(path));
    }

    [Theory]
    [InlineData("sine1k.tta.tta")]
    [InlineData("sine1k.dsd64.dff")]
    [InlineData("arpeggio.mid")]
    public void Formats_taglib_cannot_write_are_read_only(string name)
    {
        var path = Copy(name);
        Assert.False(TagWriter.CanWrite(path));
        Assert.Throws<TagWriteException>(() => _writer.Write(path, new TagEdit(new Dictionary<TagField, string?> { [TagField.Title] = "x" })));
    }

    [Fact]
    public void An_invalid_number_writes_nothing()
    {
        var path = Copy("sine1k.flac.flac");
        var before = File.ReadAllBytes(path);
        var edit = new TagEdit(new Dictionary<TagField, string?> { [TagField.Title] = "New", [TagField.Year] = "twenty" });

        Assert.Throws<TagWriteException>(() => _writer.Write(path, edit));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Cached_analysis_moves_to_the_written_file_and_the_cached_tags_are_dropped()
    {
        var path = Copy("sine1k.flac.flac");
        Assert.NotNull(_tags.Read(path)); // caches "tags"
        _cache.Put(FileKey.FromFile(path), "waveform", 1, [1, 2, 3]);

        _writer.Write(path, new TagEdit(new Dictionary<TagField, string?> { [TagField.Title] = "A longer title than before, so the file changes" }));

        var key = FileKey.FromFile(path);
        Assert.Equal([1, 2, 3], _cache.TryGet(key, "waveform", 1));
        Assert.Equal("A longer title than before, so the file changes", _tags.Read(path)!.Title);
    }

    [Fact]
    public void Common_value_of_several_files()
    {
        EditableTags Tags(string? album, string? year) => new(
            new Dictionary<TagField, string>(new[] { (TagField.Album, album), (TagField.Year, year) }
                .Where(p => p.Item2 is not null).Select(p => KeyValuePair.Create(p.Item1, p.Item2!))), null);
        var files = new[] { Tags("A", "2020"), Tags("A", "2021"), Tags("A", null) };

        Assert.Equal(("A", false), EditableTags.Common(files, TagField.Album));
        Assert.Equal(((string?)null, true), EditableTags.Common(files, TagField.Year));
        Assert.Equal(((string?)null, false), EditableTags.Common(files, TagField.Title));
    }

    private string Copy(string name)
    {
        var copy = Path.Combine(_dir, name);
        File.Copy(TestMedia.Require(name), copy, overwrite: true);
        return copy;
    }

    /// <summary>SHA-256 of every decoded sample.</summary>
    private static string AudioHash(string path)
    {
        using var source = new FfmpegSourceFactory().Open(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new float[4096 * source.Format.Channels];
        int frames;
        while ((frames = source.Read(buffer)) > 0)
        {
            sha.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan(0, frames * source.Format.Channels)));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
