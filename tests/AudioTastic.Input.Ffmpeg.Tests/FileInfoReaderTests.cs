using AudioTastic.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-05: the file info dialog's file and audio details.</summary>
public sealed class FileInfoReaderTests
{
    private readonly FileInfoReader _reader = new([new FfmpegSourceFactory()], NullLogger<FileInfoReader>.Instance);

    [Fact]
    public void Lossless_details()
    {
        var path = TestMedia.Require("sine1k.flac.flac");

        var details = _reader.Read(path)!;
        var audio = details.Audio.ToDictionary(r => r.Label, r => r.Value);
        var file = details.File.ToDictionary(r => r.Label, r => r.Value);

        Assert.StartsWith("flac", audio["Codec"], StringComparison.Ordinal);
        Assert.Equal("Lossless", audio["Compression"]);
        Assert.Equal("44,100 Hz", audio["Sample rate"]);
        Assert.Equal("16 bit", audio["Bit depth"]);
        Assert.Equal("2 (stereo)", audio["Channels"]);
        Assert.StartsWith("0:05.000 (220,500 samples)", audio["Length"], StringComparison.Ordinal);
        Assert.Contains("kbps", audio["Bitrate"], StringComparison.Ordinal);
        Assert.Equal("FLAC file", file["Type"]);
        Assert.Contains("bytes", file["Size"], StringComparison.Ordinal);
    }

    [Fact]
    public void Lossy_details_and_tags()
    {
        var details = _reader.Read(TestMedia.Require("sine1k.mp3.mp3"))!;
        var audio = details.Audio.ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal("Lossy", audio["Compression"]);
        Assert.DoesNotContain("Bit depth", audio.Keys);
        Assert.Contains("MPEG", audio["Codec"], StringComparison.Ordinal); // TagLib's description
        Assert.Contains(details.Tags, t => t.Label.Equals("encoder", StringComparison.OrdinalIgnoreCase));
        Assert.True(audio.ContainsKey("Encoder"));
    }

    [Fact]
    public void Missing_files_have_no_details() => Assert.Null(_reader.Read(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".flac")));

    [Theory]
    [InlineData(512L, "512 bytes")]
    [InlineData(1_536L, "1.5 KB (1,536 bytes)")]
    [InlineData(12_345_678L, "11.8 MB (12,345,678 bytes)")]
    public void Sizes(long bytes, string expected) => Assert.Equal(expected, FileInfoReader.Size(bytes));
}
