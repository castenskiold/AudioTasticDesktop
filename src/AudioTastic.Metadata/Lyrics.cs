using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>MD-04: one line of lyrics; <see cref="Time"/> is null for plain (unsynced) lyrics.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text);

/// <summary>MD-04: a track's lyrics. <see cref="Source"/> describes where they came from (shown as a tooltip).</summary>
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool Synced, string Source)
{
    /// <summary>The index of the line sung at <paramref name="position"/> (the last line that has started), or -1.</summary>
    public int LineAt(TimeSpan position)
    {
        if (!Synced)
        {
            return -1;
        }
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}

/// <summary>
/// MD-04: LRC parsing: <c>[mm:ss.xx]text</c> lines (several time tags per line allowed; <c>[mm:ss:xx]</c> too),
/// the <c>[offset:±ms]</c> tag (positive shows lines earlier, as in the LRC convention), metadata tags such as
/// <c>[ar:]</c> ignored, and enhanced word times (<c>&lt;mm:ss.xx&gt;</c>) removed.
/// </summary>
public static partial class LrcParser
{
    /// <summary>True when the text has at least one LRC time tag at the start of a line.</summary>
    public static bool LooksSynced(string text) => TimeTag().IsMatch(text ?? string.Empty);

    /// <summary>The timed lines, sorted by time; empty when there are none.</summary>
    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var offset = TimeSpan.Zero;
        var lines = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (OffsetTag().Match(line) is { Success: true } o &&
                int.TryParse(o.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
            {
                offset = TimeSpan.FromMilliseconds(ms);
                continue;
            }
            var times = new List<TimeSpan>();
            var rest = line.AsSpan().TrimStart();
            while (LeadingTag().Match(rest.ToString()) is { Success: true } m)
            {
                if (ParseTime(m.Groups[1].Value) is { } t)
                {
                    times.Add(t);
                }
                rest = rest[m.Length..].TrimStart();
            }
            if (times.Count == 0)
            {
                continue; // metadata ([ar:...]) or untimed text
            }
            var words = WordTag().Replace(rest.ToString(), string.Empty).Trim();
            foreach (var t in times)
            {
                var time = t - offset;
                lines.Add(new LyricLine(time < TimeSpan.Zero ? TimeSpan.Zero : time, words));
            }
        }
        return [.. lines.OrderBy(l => l.Time)];
    }

    /// <summary>"mm:ss", "mm:ss.xx" or "mm:ss:xx" (hundredths after a colon) → time; null when not a time.</summary>
    private static TimeSpan? ParseTime(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        if (parts.Length == 3)
        {
            parts = [parts[0], parts[1] + "." + parts[2]];
        }
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            && seconds < 60
            ? TimeSpan.FromSeconds((minutes * 60) + seconds)
            : null;
    }
    [GeneratedRegex(@"^\s*\[\d{1,3}:\d{1,2}(?:[.:,]\d{1,3})?\]", RegexOptions.Multiline)]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[(\d{1,3}:\d{1,2}(?:[.:,]\d{1,3})?)\]")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();
}

/// <summary>
/// MD-04: finds a track's lyrics: a .lrc file with the same name next to it (UTF-8, else the legacy code page), then
/// synchronised lyrics embedded in an ID3v2 SYLT frame, then embedded lyrics (USLT, LYRICS, ©lyr), which count as
/// synced when they carry LRC time tags. Thread-safe; call it off the UI thread.
/// </summary>
public sealed class LyricsService(ILogger<LyricsService> logger)
{
    public Lyrics? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }
        return FromLrcFile(path) ?? Embedded(path);
    }

    /// <summary>Lyrics from text: synced when it has LRC time tags, else plain lines.</summary>
    public static Lyrics? FromText(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (LrcParser.LooksSynced(text) && LrcParser.Parse(text) is { Count: > 0 } timed)
        {
            return new Lyrics(timed, true, source);
        }
        var plain = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n')
            .Select(l => new LyricLine(null, l.TrimEnd())).ToList();
        return new Lyrics(plain, false, source);
    }

    private Lyrics? FromLrcFile(string path)
    {
        var lrc = Path.ChangeExtension(path, ".lrc");
        if (!File.Exists(lrc))
        {
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(lrc);
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(bytes);
            }
            return FromText(text, Path.GetFileName(lrc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read {Path}", lrc);
            return null;
        }
    }

    private Lyrics? Embedded(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3 &&
                id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>().FirstOrDefault(f => f.Text.Length > 0) is { } sylt &&
                sylt.Format == TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds)
            {
                var lines = sylt.Text.OrderBy(t => t.Time).Select(t => new LyricLine(TimeSpan.FromMilliseconds(t.Time), t.Text.Trim())).ToList();
                return new Lyrics(lines, true, "Embedded (synchronised)");
            }
            return FromText(file.Tag.Lyrics, "Embedded");
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            logger.LogDebug(ex, "No embedded lyrics read from {Path}", path);
            return null;
        }
    }
}
