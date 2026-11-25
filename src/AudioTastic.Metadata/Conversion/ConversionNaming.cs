using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;

namespace AudioTastic.Metadata.Conversion;

/// <summary>
/// TL-01: output file names from a pattern such as <c>%albumartist%/%album%/%track% - %title%</c>. Fields:
/// %artist%, %albumartist% (falls back to the artist), %album%, %title%, %track% (two digits), %disc%, %year%,
/// %genre% and %filename% (the original's name without extension). '/' or '\' starts a subfolder. Characters that
/// file systems refuse are replaced by '_'.
/// </summary>
public static partial class ConversionNaming
{
    public const string DefaultPattern = "%albumartist%/%album%/%track% - %title%";

    public static readonly IReadOnlyList<string> Fields = ["%artist%", "%albumartist%", "%album%", "%title%", "%track%", "%disc%", "%year%", "%genre%", "%filename%"];

    /// <summary>The file extension a format is written with.</summary>
    public static string ExtensionOf(EncodeFormat format) => format switch
    {
        EncodeFormat.Flac => ".flac",
        EncodeFormat.Mp3 => ".mp3",
        EncodeFormat.Opus => ".opus",
        EncodeFormat.Aac or EncodeFormat.Alac => ".m4a",
        EncodeFormat.Vorbis => ".ogg",
        EncodeFormat.Wav => ".wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The relative path (without extension) for a track: folders separated by the platform separator.</summary>
    public static string Render(string pattern, TrackTags? tags, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "%filename%";
        }
        var fileName = Path.GetFileNameWithoutExtension(sourcePath.Contains("://", StringComparison.Ordinal) ? sourcePath.Replace(':', '_') : sourcePath);
        string Value(string field) => field.ToLowerInvariant() switch
        {
            "%artist%" => Text(tags?.Artist) ?? "Unknown artist",
            "%albumartist%" => Text(tags?.AlbumArtist) ?? Text(tags?.Artist) ?? "Unknown artist",
            "%album%" => Text(tags?.Album) ?? "Unknown album",
            "%title%" => Text(tags?.Title) ?? fileName,
            "%track%" => tags?.TrackNumber is int t and > 0 ? t.ToString("00", CultureInfo.InvariantCulture) : "00",
            "%disc%" => tags?.DiscNumber is int d and > 0 ? d.ToString(CultureInfo.InvariantCulture) : "1",
            "%year%" => tags?.Year is int y and > 0 ? y.ToString(CultureInfo.InvariantCulture) : "Unknown year",
            "%genre%" => Text(tags?.Genre) ?? "Unknown genre",
            "%filename%" => fileName,
            _ => field, // not a field: kept as written
        };
        var parts = pattern.Split('/', '\\')
            .Select(part => Clean(FieldPattern().Replace(part, m => Value(m.Value))))
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count == 0 ? Clean(fileName) : Path.Combine([.. parts]);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>One folder or file name: no separators or characters Windows, macOS or Linux refuse; no trailing dots or spaces.</summary>
    public static string Clean(string part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var builder = new StringBuilder(part.Length);
        foreach (var c in part)
        {
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        var text = builder.ToString().Trim().TrimEnd('.', ' ');
        if (text.Length == 0)
        {
            return part.Trim().Length == 0 ? string.Empty : "_"; // ".." or "...": not a usable name
        }
        return text.Length > 120 ? text[..120].TrimEnd('.', ' ') : text;
    }

    [GeneratedRegex("%[a-zA-Z]+%")]
    private static partial Regex FieldPattern();
}
