using System.Text;
using AudioTastic.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-04: LRC parsing, the line at a position, and where lyrics are found.</summary>
public sealed class LyricsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-lyrics-").FullName;
    private readonly LyricsService _lyrics = new(NullLogger<LyricsService>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Lrc_lines_times_offset_repeats_and_word_tags()
    {
        const string lrc = """
            [ar:Artist]
            [ti:Title]
            [offset:+500]
            [00:12.50]First line
            [00:05.00][01:05:20]Chorus <00:05.50>with <00:06.00>words
            [00:20]Last
            Untimed text is ignored
            """;
        var lines = LrcParser.Parse(lrc);

        Assert.Equal(
            [(4.5, "Chorus with words"), (12.0, "First line"), (19.5, "Last"), (64.7, "Chorus with words")],
            lines.Select(l => (Math.Round(l.Time!.Value.TotalSeconds, 2), l.Text)));
    }

    [Fact]
    public void Line_at_a_position()
    {
        var lyrics = LyricsService.FromText("[00:01.00]a\n[00:03.00]b\n[00:05.00]c", "test")!;

        Assert.True(lyrics.Synced);
        Assert.Equal(-1, lyrics.LineAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(0, lyrics.LineAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, lyrics.LineAt(TimeSpan.FromSeconds(4.9)));
        Assert.Equal(2, lyrics.LineAt(TimeSpan.FromMinutes(9)));
    }

    [Fact]
    public void Plain_text_is_plain_lyrics()
    {
        var lyrics = LyricsService.FromText("One\r\nTwo\r\n\r\nThree", "test")!;

        Assert.False(lyrics.Synced);
        Assert.Equal(["One", "Two", "", "Three"], lyrics.Lines.Select(l => l.Text));
        Assert.Equal(-1, lyrics.LineAt(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void An_lrc_file_beats_embedded_lyrics()
    {
        var track = Copy("sine1k.flac.flac");
        using (var file = TagLib.File.Create(track))
        {
            file.Tag.Lyrics = "Embedded words";
            file.Save();
        }
        Assert.Equal(("Embedded", false), (_lyrics.Find(track)!.Source, _lyrics.Find(track)!.Synced));

        File.WriteAllText(Path.ChangeExtension(track, ".lrc"), "[00:01.00]Søde ord", new UTF8Encoding(true));
        var lyrics = _lyrics.Find(track)!;

        Assert.True(lyrics.Synced);
        Assert.Equal("Søde ord", lyrics.Lines[0].Text);
        Assert.Equal("sine1k.flac.lrc", lyrics.Source);
    }

    [Fact]
    public void Legacy_code_page_lrc_files_are_read()
    {
        var track = Copy("sine1k.mp3.mp3");
        File.WriteAllBytes(Path.ChangeExtension(track, ".lrc"), [.. Encoding.ASCII.GetBytes("[00:02.00]S"), 0xF8, .. Encoding.ASCII.GetBytes("de")]); // "Søde" in Latin-1

        Assert.Equal("Søde", _lyrics.Find(track)!.Lines[0].Text);
    }

    [Fact]
    public void Synchronised_id3_lyrics_are_read()
    {
        var track = Copy("sine1k.mp3.mp3");
        using (var file = TagLib.File.Create(track))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            var sylt = new TagLib.Id3v2.SynchronisedLyricsFrame("lyrics", "eng", TagLib.Id3v2.SynchedTextType.Lyrics)
            {
                Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds,
                Text = [new TagLib.Id3v2.SynchedText(2000, "Two"), new TagLib.Id3v2.SynchedText(1000, "One")],
            };
            id3.AddFrame(sylt);
            file.Save();
        }

        var lyrics = _lyrics.Find(track)!;

        Assert.True(lyrics.Synced);
        Assert.Equal([(1.0, "One"), (2.0, "Two")], lyrics.Lines.Select(l => (l.Time!.Value.TotalSeconds, l.Text)));
    }

    [Fact]
    public void No_lyrics_for_files_without_them_and_streams()
    {
        Assert.Null(_lyrics.Find(Copy("sine1k.wav16.wav")));
        Assert.Null(_lyrics.Find("http://example.invalid/stream"));
    }

    private string Copy(string name)
    {
        var copy = Path.Combine(_dir, name);
        File.Copy(TestMedia.Require(name), copy, overwrite: true);
        return copy;
    }
}
