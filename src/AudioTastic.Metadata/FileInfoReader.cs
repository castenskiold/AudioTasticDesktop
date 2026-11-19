using System.Globalization;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>MD-05: one row of the file info dialog.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>MD-05: what the file info dialog shows about the file itself (the app adds ReplayGain and BPM).</summary>
public sealed record FileDetails(string Path, IReadOnlyList<InfoRow> File, IReadOnlyList<InfoRow> Audio, IReadOnlyList<InfoRow> Tags);

/// <summary>
/// MD-05: reads a file's technical details through its decoder (codec, sample rate, bit depth, channels, length,
/// bitrate, all metadata) and TagLib (codec description), plus the file size. Thread-safe; call it off the UI thread.
/// </summary>
public sealed class FileInfoReader(IEnumerable<IAudioSourceFactory> factories, ILogger<FileInfoReader> logger)
{
    /// <summary>The details, or null when no decoder can open the file.</summary>
    public FileDetails? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var isUrl = path.Contains("://", StringComparison.Ordinal);
        var file = new List<InfoRow> { new("Location", path) };
        long? size = null;
        if (!isUrl)
        {
            if (!System.IO.File.Exists(path))
            {
                return null;
            }
            var info = new FileInfo(path);
            size = info.Length;
            file.Add(new("Type", info.Extension.TrimStart('.').ToUpperInvariant() + " file"));
            file.Add(new("Size", Size(info.Length)));
            file.Add(new("Modified", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }
        if (factories.FirstOrDefault(f => f.CanOpen(path)) is not { } factory)
        {
            return null;
        }
        var audio = new List<InfoRow>();
        var tags = new List<InfoRow>();
        try
        {
            using var source = factory.Open(path);
            var s = source.Info;
            var description = isUrl ? null : TagLibDescription(path);
            audio.Add(new("Codec", description is null ? s.Codec : $"{s.Codec} ({description})"));
            audio.Add(new("Compression", s.IsLossless ? "Lossless" : "Lossy"));
            audio.Add(new("Sample rate", $"{s.SampleRate.ToString("N0", CultureInfo.InvariantCulture)} Hz"));
            if (s.BitsPerSample > 0)
            {
                audio.Add(new("Bit depth", $"{s.BitsPerSample} bit"));
            }
            audio.Add(new("Channels", Channels(s.Channels)));
            TimeSpan? duration = source.LengthFrames is { } frames ? source.Format.FramesToTime(frames) : null;
            if (duration is { } d)
            {
                audio.Add(new("Length", $"{Time(d)} ({source.LengthFrames!.Value.ToString("N0", CultureInfo.InvariantCulture)} samples)"));
            }
            // The decoder's figure, else the average over the file (lossless formats often leave it out).
            var bitrate = s.Bitrate > 0 ? s.Bitrate : size is { } bytes && duration is { TotalSeconds: > 0 } length ? (long)(bytes * 8 / length.TotalSeconds) : 0;
            if (bitrate > 0)
            {
                audio.Add(new("Bitrate", $"{(bitrate / 1000.0).ToString("N0", CultureInfo.InvariantCulture)} kbps{(s.Bitrate > 0 ? string.Empty : " (average)")}"));
            }
            if (Encoder(source.Tags) is { } encoder)
            {
                audio.Add(new("Encoder", encoder));
            }
            tags.AddRange(source.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Value))
                .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(t => new InfoRow(t.Key, t.Value.Trim())));
        }
        catch (Exception ex) when (ex is AudioSourceException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "No file info read from {Path}", path);
            audio.Add(new("Error", ex.Message));
        }
        return new FileDetails(path, file, audio, tags);
    }

    /// <summary>"12.3 MB (12,345,678 bytes)".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var shortForm = unit == 0 ? $"{bytes} bytes" : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
        return unit == 0 ? shortForm : $"{shortForm} ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
    }

    /// <summary>"1:02:03.456" / "3:04.500".</summary>
    public static string Time(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        : t.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

    public static string Channels(int channels) => channels switch
    {
        1 => "1 (mono)",
        2 => "2 (stereo)",
        6 => "6 (5.1)",
        8 => "8 (7.1)",
        _ => channels.ToString(CultureInfo.InvariantCulture),
    };

    private static string? Encoder(IReadOnlyDictionary<string, string> tags)
    {
        foreach (var key in new[] { "encoder", "encoded_by", "encoded-by", "TSSE", "TENC", "ENCODER" })
        {
            if (tags.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value)
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static string? TagLibDescription(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Properties?.Codecs.OfType<TagLib.ICodec>().Select(c => c.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))?.Trim();
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
