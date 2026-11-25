using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata.Conversion;

/// <summary>
/// TL-01: copies the original's tags (and cover) onto a converted file with TagLib, then lays the item's own tags on
/// top (a CUE or CD track's title, artist and number). A part of a file loses the whole file's track gain and lyrics.
/// </summary>
internal static class TagCopier
{
    public static void Copy(ConversionItem item, string target, TrackTags? tags, TagPicture? cover, bool copyArt, ILogger logger)
    {
        try
        {
            using var file = TagLib.File.Create(target);
            var tag = file.Tag;
            if (!item.Path.Contains("://", StringComparison.Ordinal))
            {
                try
                {
                    using var original = TagLib.File.Create(item.Path);
                    original.Tag.CopyTo(tag, true);
                    tag.Pictures = copyArt ? original.Tag.Pictures : [];
                }
                catch (Exception ex) when (IsTagLibFailure(ex))
                {
                    logger.LogDebug("No tags copied from {Path}: {Message}", item.Path, ex.Message); // e.g. MOD or MIDI: the decoder's tags below
                }
            }
            if (tags is not null)
            {
                Apply(tag, tags, overwrite: item.IsPart || item.Tags is not null);
            }
            if (item.IsPart)
            {
                tag.ReplayGainTrackGain = double.NaN;
                tag.ReplayGainTrackPeak = double.NaN;
                tag.Lyrics = null;
            }
            if (copyArt && tag.Pictures.Length == 0 && cover is not null)
            {
                tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover.Data)) { Type = TagLib.PictureType.FrontCover, MimeType = cover.MimeType }];
            }
            file.Save();
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            logger.LogWarning("Tags could not be written to {Path}: {Message}", target, ex.Message);
        }
    }

    /// <summary>Writes the known fields; with <paramref name="overwrite"/> off only where the file has none.</summary>
    private static void Apply(TagLib.Tag tag, TrackTags tags, bool overwrite)
    {
        static bool Empty(string? s) => string.IsNullOrWhiteSpace(s);
        if (tags.Title is { } title && (overwrite || Empty(tag.Title)))
        {
            tag.Title = title;
        }
        if (tags.Artist is { } artist && (overwrite || Empty(tag.JoinedPerformers)))
        {
            tag.Performers = [artist];
        }
        if (tags.Album is { } album && (overwrite || Empty(tag.Album)))
        {
            tag.Album = album;
        }
        if (tags.AlbumArtist is { } albumArtist && (overwrite || Empty(tag.JoinedAlbumArtists)))
        {
            tag.AlbumArtists = [albumArtist];
        }
        if (tags.TrackNumber is int track and > 0 && (overwrite || tag.Track == 0))
        {
            tag.Track = (uint)track;
        }
        if (tags.DiscNumber is int disc and > 0 && (overwrite || tag.Disc == 0))
        {
            tag.Disc = (uint)disc;
        }
        if (tags.Year is int year and > 0 && (overwrite || tag.Year == 0))
        {
            tag.Year = (uint)year;
        }
        if (tags.Genre is { } genre && (overwrite || Empty(tag.JoinedGenres)))
        {
            tag.Genres = [genre];
        }
    }

    private static bool IsTagLibFailure(Exception ex) => TagLibErrors.IsFileProblem(ex);
}
