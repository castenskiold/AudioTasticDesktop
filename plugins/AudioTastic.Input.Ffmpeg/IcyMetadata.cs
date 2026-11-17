using System.Text;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>FM-10: parsing of SHOUTcast/Icecast (ICY) metadata.</summary>
internal static class IcyMetadata
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The title from a metadata block such as <c>StreamTitle='Artist - Song';StreamUrl='';</c>.
    /// Returns null when the block has no (or an empty) StreamTitle.
    /// </summary>
    public static string? ParseStreamTitle(string packet)
    {
        const string key = "StreamTitle='";
        var start = packet.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        start += key.Length;
        // The value ends at "';" (titles may contain apostrophes), or at the last quote.
        var end = packet.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = packet.LastIndexOf('\'');
        }
        if (end < start)
        {
            return null;
        }
        var title = packet[start..end].Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>A header value (e.g. icy-name) from FFmpeg's icy_metadata_headers ("name: value" lines).</summary>
    public static string? ParseHeader(string? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }
        foreach (var line in headers.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(colon + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    /// <summary>ICY text has no declared encoding: UTF-8 when valid, otherwise Latin-1 (the SHOUTcast default).</summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
