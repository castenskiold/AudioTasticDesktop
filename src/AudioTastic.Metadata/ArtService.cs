using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>MD-02: a track's cover and where it came from (null <see cref="File"/> = embedded in the audio file).</summary>
public sealed record AlbumArt(TagPicture Picture, string? File);

/// <summary>
/// MD-02: finds the cover of a track: the embedded front cover (or first picture) first, then an image in the
/// track's folder named cover, folder, front or albumart (.jpg, .jpeg, .png; any case). Thread-safe; call it off the
/// UI thread (it reads files).
/// </summary>
public sealed class ArtService(ILogger<ArtService> logger)
{
    /// <summary>Folder image names, in order of preference.</summary>
    public static readonly IReadOnlyList<string> FolderNames = ["cover", "folder", "front", "albumart"];

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>Largest folder image read (bigger files are skipped).</summary>
    private const long MaxImageBytes = 20 * 1024 * 1024;

    public AlbumArt? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }
        return Embedded(path) ?? FromFolder(path);
    }

    private AlbumArt? Embedded(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return TagWriter.Cover(file.Tag.Pictures) is { } picture ? new AlbumArt(picture, null) : null;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            logger.LogDebug(ex, "No embedded art read from {Path}", path);
            return null;
        }
    }

    private AlbumArt? FromFolder(string path)
    {
        if (Path.GetDirectoryName(path) is not { } folder)
        {
            return null;
        }
        try
        {
            var candidates = Directory.EnumerateFiles(folder)
                .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f), Extension: Path.GetExtension(f)))
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .Select(f => (f.File, f.Extension, Rank: IndexOf(f.Name)))
                .Where(f => f.Rank >= 0)
                .OrderBy(f => f.Rank)
                .ToList();
            foreach (var (image, extension, _) in candidates)
            {
                if (new FileInfo(image).Length is > 0 and <= MaxImageBytes)
                {
                    var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                    return new AlbumArt(new TagPicture(File.ReadAllBytes(image), mime), image);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "No folder art read next to {Path}", path);
        }
        return null;
    }

    private static int IndexOf(string name)
    {
        for (var i = 0; i < FolderNames.Count; i++)
        {
            if (string.Equals(FolderNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
