using System.Globalization;
using AudioTastic.Data.Cache;
using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>A tag write that did not happen (locked, read-only or unsupported file, invalid number).</summary>
public sealed class TagWriteException : Exception
{
    public TagWriteException()
    {
    }

    public TagWriteException(string message)
        : base(message)
    {
    }

    public TagWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// MD-01 part 2: reads the editable tags of a file and writes changes with TagLibSharp. Formats TagLib cannot write
/// (DSD, tracker modules, MIDI, TTA, streams) are read-only. After a write the file's cached data (waveform,
/// loudness, beats, resume position, …) moves to the file's new version, because only the tags changed; the cached
/// tags are dropped so <see cref="TagService"/> reads them again. Not thread-affine; callers must not write a file
/// the player is decoding (<see cref="TagWriteService"/> waits for that).
/// </summary>
public sealed class TagWriter(TagService tags, CacheStore? cache, ILogger<TagWriter> logger)
{
    private static readonly string[] DroppedKinds = ["tags"];

    /// <summary>True when the file exists, is not read-only and TagLib can write its format.</summary>
    public static bool CanWrite(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).IsReadOnly)
        {
            return false;
        }
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Writeable;
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            return false;
        }
    }

    /// <summary>The file's editable tags, or null when TagLib cannot read it.</summary>
    public EditableTags? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var fields = new Dictionary<TagField, string>();
            void Add(TagField field, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[field] = value.Trim();
                }
            }
            void AddNumber(TagField field, uint value)
            {
                if (value > 0)
                {
                    fields[field] = value.ToString(CultureInfo.InvariantCulture);
                }
            }
            Add(TagField.Title, tag.Title);
            Add(TagField.Artist, tag.JoinedPerformers);
            Add(TagField.AlbumArtist, tag.JoinedAlbumArtists);
            Add(TagField.Album, tag.Album);
            AddNumber(TagField.Track, tag.Track);
            AddNumber(TagField.TrackCount, tag.TrackCount);
            AddNumber(TagField.Disc, tag.Disc);
            AddNumber(TagField.DiscCount, tag.DiscCount);
            AddNumber(TagField.Year, tag.Year);
            Add(TagField.Genre, tag.JoinedGenres);
            Add(TagField.Composer, tag.JoinedComposers);
            Add(TagField.Comment, tag.Comment);
            Add(TagField.Lyrics, tag.Lyrics);
            return new EditableTags(fields, Cover(tag.Pictures));
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            logger.LogDebug(ex, "No editable tags read from {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the file and returns its tags as read afterwards. Throws
    /// <see cref="TagWriteException"/> when the file cannot be written; it is then unchanged.
    /// </summary>
    public TrackTags? Write(string path, TagEdit edit)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.IsEmpty)
        {
            return tags.Read(path);
        }
        if (!CanWrite(path))
        {
            throw new TagWriteException($"Tags cannot be written to {Path.GetFileName(path)} (read-only file or format).");
        }
        // Numbers are checked before anything is written.
        var numbers = new Dictionary<TagField, uint>();
        foreach (var (field, value) in edit.Fields)
        {
            if (IsNumber(field) && !string.IsNullOrWhiteSpace(value))
            {
                if (!uint.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    throw new TagWriteException($"'{value}' is not a valid {field} number.");
                }
                numbers[field] = n;
            }
        }
        try
        {
            var oldKey = FileKey.FromFile(path);
            using (var file = TagLib.File.Create(path))
            {
                var tag = file.Tag;
                foreach (var (field, raw) in edit.Fields)
                {
                    var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                    var number = numbers.GetValueOrDefault(field);
                    switch (field)
                    {
                        case TagField.Title: tag.Title = value; break;
                        case TagField.Artist: tag.Performers = Split(value); break;
                        case TagField.AlbumArtist: tag.AlbumArtists = Split(value); break;
                        case TagField.Album: tag.Album = value; break;
                        case TagField.Track: tag.Track = number; break;
                        case TagField.TrackCount: tag.TrackCount = number; break;
                        case TagField.Disc: tag.Disc = number; break;
                        case TagField.DiscCount: tag.DiscCount = number; break;
                        case TagField.Year: tag.Year = number; break;
                        case TagField.Genre: tag.Genres = Split(value); break;
                        case TagField.Composer: tag.Composers = Split(value); break;
                        case TagField.Comment: tag.Comment = value; break;
                        case TagField.Lyrics: tag.Lyrics = value; break;
                        default: throw new ArgumentOutOfRangeException(nameof(edit), field, "Unknown tag field");
                    }
                }
                if (edit.Cover is { } cover)
                {
                    tag.Pictures = SetCover(tag.Pictures, cover.Picture);
                }
                file.Save();
            }
            cache?.Rekey(oldKey, FileKey.FromFile(path), DroppedKinds);
            logger.LogInformation("Wrote {Count} tag field(s){Cover} to {Path}", edit.Fields.Count, edit.Cover is null ? string.Empty : " and the cover", path);
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            throw new TagWriteException($"Tags could not be written to {Path.GetFileName(path)}: {ex.Message}", ex);
        }
        return tags.Read(path);
    }

    private static bool IsNumber(TagField field) =>
        field is TagField.Track or TagField.TrackCount or TagField.Disc or TagField.DiscCount or TagField.Year;

    /// <summary>"A; B" → ["A", "B"] (TagLib joins multiple values with "; ").</summary>
    private static string[] Split(string? value) =>
        value is null ? [] : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The front cover, or the first picture when none is marked as the front cover.</summary>
    private static TagPicture? Cover(TagLib.IPicture[] pictures)
    {
        var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures.FirstOrDefault();
        return picture?.Data is { Count: > 0 } data ? new TagPicture(data.Data, picture.MimeType ?? "image/jpeg") : null;
    }

    /// <summary>Replaces the front cover (keeping other pictures), or removes it.</summary>
    private static TagLib.IPicture[] SetCover(TagLib.IPicture[] pictures, TagPicture? cover)
    {
        var fronts = pictures.Where(p => p.Type == TagLib.PictureType.FrontCover).ToList();
        // A file whose only pictures are unmarked shows the first one as its cover: that one is replaced or removed.
        var replaced = fronts.Count > 0 ? fronts : pictures.Take(1).ToList();
        var kept = pictures.Except(replaced).ToList();
        if (cover is not null)
        {
            kept.Insert(0, new TagLib.Picture(new TagLib.ByteVector(cover.Data))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = cover.MimeType,
                Description = "Cover",
            });
        }
        return [.. kept];
    }

    private static bool IsTagLibFailure(Exception ex) =>
        ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException;
}
