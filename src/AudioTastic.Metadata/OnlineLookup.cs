using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>MD-03: one MusicBrainz match: a recording on a release.</summary>
public sealed record LookupCandidate(
    string Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    int? Year,
    int? Track,
    int? TrackCount,
    int? Disc,
    TimeSpan? Length,
    int Score,
    string RecordingId,
    string? ReleaseId)
{
    /// <summary>The candidate as tag fields (only those it has).</summary>
    public IReadOnlyDictionary<TagField, string> Fields
    {
        get
        {
            var c = CultureInfo.InvariantCulture;
            var fields = new Dictionary<TagField, string> { [TagField.Title] = Title };
            void Add(TagField key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[key] = value;
                }
            }
            Add(TagField.Artist, Artist);
            Add(TagField.Album, Album);
            Add(TagField.AlbumArtist, AlbumArtist);
            Add(TagField.Year, Year?.ToString(c));
            Add(TagField.Track, Track?.ToString(c));
            Add(TagField.TrackCount, TrackCount?.ToString(c));
            Add(TagField.Disc, Disc?.ToString(c));
            return fields;
        }
    }
}

/// <summary>
/// MD-03: keeps requests to one host at least <see cref="Interval"/> apart (MusicBrainz allows one request per second
/// per client). Thread-safe: concurrent callers queue up.
/// </summary>
public sealed class RequestSpacer(TimeSpan interval, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private DateTimeOffset? _last;

    public TimeSpan Interval { get; } = interval;

    /// <summary>Runs <paramref name="request"/> once the interval since the previous request has passed.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is { } last && _now() - last is var since && since < Interval)
            {
                await _delay(Interval - since, cancellationToken).ConfigureAwait(false);
            }
            _last = _now();
            return await request().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// MD-03: looks up tags on MusicBrainz (recording search by title, artist and length; WS/2 JSON) and front covers on
/// the Cover Art Archive. MusicBrainz requests are spaced at least one second apart and carry a descriptive
/// User-Agent; answers are cached for the session. Nothing is written here: the tag editor shows the results for the
/// user to accept.
/// </summary>
public sealed class OnlineLookupService : IDisposable
{
    public const string MusicBrainzSearch = "https://musicbrainz.org/ws/2/recording";
    public const string CoverArtArchive = "https://coverartarchive.org/release";

    /// <summary>Largest cover downloaded.</summary>
    private const int MaxCoverBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly RequestSpacer _musicBrainz;
    private readonly ConcurrentDictionary<string, IReadOnlyList<LookupCandidate>> _searches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TagPicture?> _covers = new(StringComparer.Ordinal);
    private readonly ILogger<OnlineLookupService> _logger;
    private readonly bool _ownsClient;

    public OnlineLookupService(ILogger<OnlineLookupService> logger)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, new RequestSpacer(TimeSpan.FromSeconds(1)), logger, ownsClient: true)
    {
    }

    /// <summary>For tests: a client over a fake handler, and a spacer with a fake clock.</summary>
    public OnlineLookupService(HttpClient http, RequestSpacer musicBrainz, ILogger<OnlineLookupService> logger, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _musicBrainz = musicBrainz;
        _logger = logger;
        _ownsClient = ownsClient;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
    }

    /// <summary>MusicBrainz asks for "Application/version ( contact )"; the contact URL is Anders's (19 Nov 2026).</summary>
    public static string UserAgent { get; } =
        $"AudioTastic/{typeof(OnlineLookupService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} ( https://the-end.dk )";

    /// <summary>The MusicBrainz search URL for a title, artist and length (±10 s).</summary>
    public static string SearchUrl(string title, string? artist, TimeSpan? length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var query = new StringBuilder($"recording:\"{Escape(title)}\"");
        if (!string.IsNullOrWhiteSpace(artist))
        {
            query.Append(CultureInfo.InvariantCulture, $" AND artist:\"{Escape(artist)}\"");
        }
        if (length is { TotalSeconds: > 0 } l)
        {
            var ms = (long)l.TotalMilliseconds;
            query.Append(CultureInfo.InvariantCulture, $" AND dur:[{Math.Max(0, ms - 10_000)} TO {ms + 10_000}]");
        }
        return $"{MusicBrainzSearch}?query={Uri.EscapeDataString(query.ToString())}&fmt=json&limit=15";
    }

    /// <summary>Recordings matching the title (and artist and length), best first; one candidate per release.</summary>
    public async Task<IReadOnlyList<LookupCandidate>> SearchAsync(string title, string? artist, TimeSpan? length, CancellationToken cancellationToken)
    {
        var url = SearchUrl(title, artist, length);
        if (_searches.TryGetValue(url, out var cached))
        {
            return cached;
        }
        var json = await _musicBrainz.RunAsync(() => GetStringAsync(url, cancellationToken), cancellationToken).ConfigureAwait(false);
        var candidates = ParseRecordings(json);
        _searches[url] = candidates;
        return candidates;
    }

    /// <summary>The release's front cover (about 500 px) from the Cover Art Archive, or null when it has none.</summary>
    public async Task<TagPicture?> FrontCoverAsync(string releaseId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        if (_covers.TryGetValue(releaseId, out var cached))
        {
            return cached;
        }
        using var response = await _http.GetAsync($"{CoverArtArchive}/{Uri.EscapeDataString(releaseId)}/front-500", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        TagPicture? cover = null;
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxCoverBytes)
            {
                throw new HttpRequestException("The cover is too large.");
            }
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (data.Length is > 0 and <= MaxCoverBytes)
            {
                var mime = response.Content.Headers.ContentType?.MediaType is "image/png" ? "image/png" : "image/jpeg";
                cover = new TagPicture(data, mime);
            }
        }
        _covers[releaseId] = cover;
        return cover;
    }

    /// <summary>MusicBrainz recording search JSON → candidates (one per recording and release), best score first.</summary>
    public static IReadOnlyList<LookupCandidate> ParseRecordings(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var result = new List<LookupCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var rec in recordings.EnumerateArray())
        {
            var title = String(rec, "title");
            var id = String(rec, "id");
            if (title is null || id is null)
            {
                continue;
            }
            var score = rec.TryGetProperty("score", out var s) && s.TryGetInt32(out var sc) ? sc : 0;
            var artist = Credit(rec);
            TimeSpan? length = rec.TryGetProperty("length", out var len) && len.TryGetInt64(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
            if (!rec.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
            {
                result.Add(new LookupCandidate(title, artist, null, null, Year(String(rec, "first-release-date")), null, null, null, length, score, id, null));
                continue;
            }
            foreach (var release in releases.EnumerateArray())
            {
                int? track = null, trackCount = null, disc = null;
                if (release.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array && media.GetArrayLength() > 0)
                {
                    var medium = media[0];
                    disc = medium.TryGetProperty("position", out var p) && p.TryGetInt32(out var pos) ? pos : null;
                    trackCount = medium.TryGetProperty("track-count", out var tc) && tc.TryGetInt32(out var count) ? count : null;
                    if (medium.TryGetProperty("track", out var tracks) && tracks.ValueKind == JsonValueKind.Array && tracks.GetArrayLength() > 0
                        && int.TryParse(String(tracks[0], "number"), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        track = number;
                    }
                }
                var albumArtist = Credit(release);
                result.Add(new LookupCandidate(title, artist, String(release, "title"), albumArtist != artist ? albumArtist : null,
                    Year(String(release, "date")), track, trackCount, disc, length, score, id, String(release, "id")));
            }
        }
        return [.. result.OrderByDescending(c => c.Score)];
    }

    public void Dispose()
    {
        _musicBrainz.Dispose();
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MusicBrainz lookup: {Url}", url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == (HttpStatusCode)503)
        {
            throw new HttpRequestException("MusicBrainz is busy; try again in a moment.");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lucene special characters escaped for a quoted phrase.</summary>
    private static string Escape(string value) => value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>"artist-credit": names joined with their join phrases ("A feat. B").</summary>
    private static string? Credit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            text.Append(String(credit, "name") ?? (credit.TryGetProperty("artist", out var a) ? String(a, "name") : null));
            text.Append(String(credit, "joinphrase"));
        }
        return text.Length > 0 ? text.ToString().Trim() : null;
    }

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0 ? y : null;
}
