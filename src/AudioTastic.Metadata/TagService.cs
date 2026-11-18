using System.Globalization;
using System.Text.Json;
using AudioTastic.Data.Cache;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>
/// MD-01 part 1: reads the tags the player shows (title, artist, album, track, year, genre, duration). Order:
/// the cache (keyed by path + size + modified time, AN-01), TagLibSharp, then FFmpeg metadata for formats TagLib
/// cannot read. Thread-safe; call from background threads (reading a file can take milliseconds).
/// Editing tags comes in P9.
/// </summary>
public sealed class TagService
{
    private const string CacheKind = "tags";

    /// <summary>Bump when <see cref="TrackTags"/> reading changes, so cached results are read again.</summary>
    private const int CacheDataVersion = 2; // 2: ReplayGain values (PB-03)

    private static readonly string[] CueSheetExtensions = [".flac", ".ape", ".wv"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CacheStore? _cache;
    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly ILogger<TagService> _logger;

    public TagService(CacheStore? cache, IEnumerable<IAudioSourceFactory> factories, ILogger<TagService> logger)
    {
        _cache = cache;
        _factories = factories; // PG-01: live view of the enabled input plugins
        _logger = logger;
    }

    /// <summary>The file's tags, or null when the file is missing or unreadable.</summary>
    public TrackTags? Read(string path)
    {
        if (path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }
        FileKey key;
        try
        {
            key = FileKey.FromFile(path);
        }
        catch (IOException)
        {
            return null;
        }
        if (_cache?.TryGet(key, CacheKind, CacheDataVersion) is { } cached)
        {
            try
            {
                return JsonSerializer.Deserialize<TrackTags>(cached, JsonOptions);
            }
            catch (JsonException)
            {
                // fall through and read again
            }
        }

        var tags = ReadWithTagLib(path) ?? ReadWithDecoder(path);
        if (tags is not null)
        {
            _cache?.Put(key, CacheKind, CacheDataVersion, JsonSerializer.SerializeToUtf8Bytes(tags, JsonOptions));
        }
        return tags;
    }

    /// <summary>FM-09: the CUESHEET embedded in a FLAC (Vorbis comment) or APE/WV (APEv2) file, or null.</summary>
    public string? ReadEmbeddedCueSheet(string path)
    {
        if (!CueSheetExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var file = TagLib.File.Create(path);
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph && xiph.GetFirstField("CUESHEET") is { Length: > 0 } flacCue)
            {
                return flacCue;
            }
            if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape && ape.GetItem("Cuesheet")?.ToString() is { Length: > 0 } apeCue)
            {
                return apeCue;
            }
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            _logger.LogDebug(ex, "No embedded cue sheet read from {Path}", path);
        }
        return null;
    }

    private TrackTags? ReadWithTagLib(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var duration = file.Properties?.Duration is { } d && d > TimeSpan.Zero ? d : (TimeSpan?)null;
            var replayGain = ReadReplayGain(file);
            return new TrackTags(
                Clean(tag.Title),
                Clean(tag.JoinedPerformers) ?? Clean(tag.FirstAlbumArtist),
                Clean(tag.Album),
                Clean(tag.FirstAlbumArtist),
                tag.Track > 0 ? (int)tag.Track : null,
                tag.Disc > 0 ? (int)tag.Disc : null,
                tag.Year > 0 ? (int)tag.Year : null,
                Clean(tag.FirstGenre),
                duration,
                replayGain);
        }
        catch (Exception ex) when (IsTagLibFailure(ex))
        {
            _logger.LogDebug(ex, "TagLib could not read {Path}; trying the decoder", path);
            return null;
        }
    }

    private TrackTags? ReadWithDecoder(string path)
    {
        var factory = _factories.FirstOrDefault(f => f.CanOpen(path));
        if (factory is null)
        {
            return null;
        }
        try
        {
            using var source = factory.Open(path);
            var t = source.Tags;
            string? Get(params string[] keys) => keys.Select(k => t.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
            TimeSpan? duration = source.LengthFrames is long frames ? source.Format.FramesToTime(frames) : null;
            return new TrackTags(
                Get("title"),
                Get("artist", "album_artist"),
                Get("album"),
                Get("album_artist"),
                Number(Get("track")),
                Number(Get("disc")),
                Number(Get("date", "year") is { Length: >= 4 } year ? year[..4] : null),
                Get("genre"),
                duration,
                new ReplayGainInfo(ParseGain(Get("replaygain_track_gain")), ParseGain(Get("replaygain_track_peak")),
                    ParseGain(Get("replaygain_album_gain")), ParseGain(Get("replaygain_album_peak"))) is { IsEmpty: false } rg ? rg : null);
        }
        catch (Exception ex) when (ex is AudioSourceException or IOException or NotSupportedException)
        {
            _logger.LogDebug(ex, "No tags read from {Path}", path);
            return null;
        }
    }

    /// <summary>"3/12" → 3.</summary>
    /// <summary>PB-03: REPLAYGAIN_* through TagLib (all tag types), or Opus R128_* gains (Q7.8 dB relative to -23 LUFS).</summary>
    private static ReplayGainInfo? ReadReplayGain(TagLib.File file)
    {
        var tag = file.Tag;
        static double? Value(double v) => double.IsNaN(v) ? null : v;
        var info = new ReplayGainInfo(Value(tag.ReplayGainTrackGain), Value(tag.ReplayGainTrackPeak),
            Value(tag.ReplayGainAlbumGain), Value(tag.ReplayGainAlbumPeak));
        if (info.IsEmpty && file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            static double? R128(string? v) => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var q)
                ? (q / 256.0) + 5.0 // R128 reference -23 LUFS -> ReplayGain reference -18 LUFS
                : null;
            info = new ReplayGainInfo(R128(xiph.GetFirstField("R128_TRACK_GAIN")), null, R128(xiph.GetFirstField("R128_ALBUM_GAIN")), null);
        }
        return info.IsEmpty ? null : info;
    }

    /// <summary>PB-03: "-6.54 dB" / "0.988" style values from FFmpeg metadata.</summary>
    internal static double? ParseGain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = value.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? Number(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return int.TryParse(slash > 0 ? value[..slash] : value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTagLibFailure(Exception ex) =>
        ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or ArgumentException or IndexOutOfRangeException or OverflowException;
}
