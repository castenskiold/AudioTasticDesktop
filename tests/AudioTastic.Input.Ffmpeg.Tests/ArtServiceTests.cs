using AudioTastic.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-02: embedded cover first, then folder images by name; nothing for streams and missing files.</summary>
public sealed class ArtServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-art-").FullName;
    private readonly ArtService _art = new(NullLogger<ArtService>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_embedded_cover_comes_first()
    {
        var track = Copy("sine1k.flac.flac");
        File.WriteAllBytes(Path.Combine(_dir, "cover.jpg"), [9, 9, 9]);
        using (var file = TagLib.File.Create(track))
        {
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector([1, 2, 3])) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
            file.Save();
        }

        var art = _art.Find(track)!;

        Assert.Equal([1, 2, 3], art.Picture.Data);
        Assert.Null(art.File);
    }

    [Fact]
    public void Folder_images_are_used_by_name_in_order()
    {
        var track = Copy("sine1k.mp3.mp3");
        File.WriteAllBytes(Path.Combine(_dir, "Front.PNG"), [3]);
        File.WriteAllBytes(Path.Combine(_dir, "folder.jpeg"), [2]);
        File.WriteAllBytes(Path.Combine(_dir, "booklet.jpg"), [7]);

        var art = _art.Find(track)!;

        Assert.Equal([2], art.Picture.Data); // folder before front
        Assert.Equal("image/jpeg", art.Picture.MimeType);
        Assert.EndsWith("folder.jpeg", art.File, StringComparison.Ordinal);
    }

    [Fact]
    public void No_art_without_a_cover_streams_or_missing_files()
    {
        Assert.Null(_art.Find(Copy("sine1k.wav16.wav")));
        Assert.Null(_art.Find("http://example.invalid/stream"));
        Assert.Null(_art.Find(Path.Combine(_dir, "missing.flac")));
    }

    private string Copy(string name)
    {
        var copy = Path.Combine(_dir, name);
        File.Copy(TestMedia.Require(name), copy, overwrite: true);
        return copy;
    }
}
