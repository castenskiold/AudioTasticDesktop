using System.Globalization;
using System.Text;

namespace AudioTastic.Core.Playlists.Formats;

/// <summary>PL-03 / FM-09: text encoding and path handling shared by the playlist formats.</summary>
public static class PlaylistText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static PlaylistText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The system's legacy ("ANSI") code page, e.g. Windows-1252; Latin-1 where there is none.</summary>
    public static Encoding Legacy
    {
        get
        {
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try
            {
                return codePage is 0 or 65001 ? Encoding.Latin1 : Encoding.GetEncoding(codePage);
            }
            catch (NotSupportedException)
            {
                return Encoding.Latin1;
            }
        }
    }

    /// <summary>Reads text: BOM if present, else UTF-8 when the bytes are valid UTF-8, else the legacy code page.</summary>
    public static string Read(string file) => Decode(File.ReadAllBytes(file));

    public static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.AsSpan().StartsWith((byte[])[0xFF, 0xFE]) || bytes.AsSpan().StartsWith((byte[])[0xFE, 0xFF]))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Legacy.GetString(bytes);
        }
    }

    /// <summary>The legacy code page if it can represent <paramref name="text"/> exactly, else UTF-8 (for .m3u).</summary>
    public static Encoding BestLegacyOrUtf8(string text)
    {
        var legacy = Legacy;
        return legacy.GetString(legacy.GetBytes(text)) == text ? legacy : new UTF8Encoding(false);
    }

    /// <summary>Longest length a playlist may state (ten years); anything beyond is treated as unknown.</summary>
    private const double MaxSeconds = 10 * 365.25 * 24 * 3600;

    /// <summary>
    /// A length read from a playlist, or null when it is missing, negative, not a number or absurdly large (P14: a
    /// damaged PLS with an enormous Length overflowed TimeSpan and failed the import).
    /// </summary>
    public static TimeSpan? Duration(double seconds) =>
        double.IsFinite(seconds) && seconds is >= 0 and <= MaxSeconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>A position in ticks from our own #AT-RANGE lines, or null when it is negative or beyond <see cref="MaxSeconds"/>.</summary>
    public static TimeSpan? Ticks(long ticks) =>
        ticks >= 0 && ticks <= MaxSeconds * TimeSpan.TicksPerSecond ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>Resolves a playlist path (absolute, relative, file:// URI, URL) against <paramref name="baseFolder"/>.</summary>
    public static string Resolve(string path, string baseFolder)
    {
        try
        {
            return ResolveCore(path.Replace("\0", string.Empty, StringComparison.Ordinal), baseFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            // P14 (found by fuzzing): a damaged playlist line must not fail the whole import; it stays as written and
            // shows as a missing entry.
            return path.Trim();
        }
    }

    private static string ResolveCore(string path, string baseFolder)
    {
        path = path.Trim();
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var fileUri))
        {
            return fileUri.LocalPath;
        }
        if (path.Contains("://", StringComparison.Ordinal))
        {
            return path; // stream URL
        }
        if (!OperatingSystem.IsWindows())
        {
            path = path.Replace('\\', '/');
        }
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path));
    }

    /// <summary>The path written to a playlist in <paramref name="baseFolder"/>: relative when asked and possible.</summary>
    public static string Write(string path, string baseFolder, bool relative)
    {
        if (!relative || path.Contains("://", StringComparison.Ordinal))
        {
            return path;
        }
        var rel = Path.GetRelativePath(baseFolder, path);
        // Different drive (Windows) or outside the folder tree: keep absolute.
        return Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
    }

    /// <summary>
    /// Splits the Winamp-style "Artist - Title" used in #EXTINF and PLS titles at the first " - ".
    /// Without a separator the whole text is the title.
    /// </summary>
    public static (string? Artist, string? Title) SplitArtistTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }
        var at = text.IndexOf(" - ", StringComparison.Ordinal);
        return at > 0 && at + 3 < text.Length ? (text[..at].Trim(), text[(at + 3)..].Trim()) : (null, text.Trim());
    }

    public static string FolderOf(string file) => Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();
}
