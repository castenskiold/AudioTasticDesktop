using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata;

/// <summary>MD-03: where a lookup match comes from.</summary>
public enum LookupSource
{
    MusicBrainz,
    Discogs,
}

/// <summary>MD-03: one match: a recording on a release (MusicBrainz), or a release with the track (Discogs).</summary>
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
    /// <summary>The service the match comes from.</summary>
    public LookupSource Source { get; init; } = LookupSource.MusicBrainz;

    /// <summary>Discogs: the release's style (more specific) or genre.</summary>
    public string? Genre { get; init; }

    /// <summary>Discogs: the cover image the search listed (the release details may name a better one).</summary>
    public string? CoverUrl { get; init; }

    /// <summary>Discogs: true once the release details (track number, disc) have been read.</summary>
    public bool IsComplete { get; init; }

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
            Add(TagField.Genre, Genre);
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
public sealed partial class OnlineLookupService : IDisposable
{
    public const string MusicBrainzSearch = "https://musicbrainz.org/ws/2/recording";
    public const string CoverArtArchive = "https://coverartarchive.org/release";
    public const string DiscogsSearch = "https://api.discogs.com/database/search";

    /// <summary>Largest cover downloaded.</summary>
    private const int MaxCoverBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly RequestSpacer _musicBrainz;
    private readonly ConcurrentDictionary<string, IReadOnlyList<LookupCandidate>> _searches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TagPicture?> _covers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TagPicture?> _discogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly RequestSpacer _discogsSpacer;
    private readonly ILogger<OnlineLookupService> _logger;
    private readonly bool _ownsClient;

    public OnlineLookupService(ILogger<OnlineLookupService> logger)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, new RequestSpacer(TimeSpan.FromSeconds(1)), logger, ownsClient: true)
    {
    }

    /// <summary>For tests: a client over a fake handler, and a spacer with a fake clock.</summary>
    public OnlineLookupService(HttpClient http, RequestSpacer musicBrainz, ILogger<OnlineLookupService> logger, bool ownsClient = false,
        RequestSpacer? discogs = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _musicBrainz = musicBrainz;
        _discogsSpacer = discogs ?? new RequestSpacer(TimeSpan.FromSeconds(1)); // Discogs allows 60 requests a minute with a token
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

    /// <summary>The Discogs release search URL for an album and artist (the token goes in a header, never in the URL).</summary>
    public static string DiscogsSearchUrl(string album, string? artist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(album);
        var url = $"{DiscogsSearch}?type=release&per_page=10&release_title={Uri.EscapeDataString(album.Trim())}";
        return string.IsNullOrWhiteSpace(artist) ? url : url + "&artist=" + Uri.EscapeDataString(artist.Trim());
    }

    /// <summary>
    /// MD-03 (Discogs fallback): the cover of the best Discogs release for <paramref name="album"/> by
    /// <paramref name="artist"/>, or null when Discogs has none. Needs the user's personal token, sent as an
    /// Authorization header. Requests are spaced one second apart; answers are cached for the session.
    /// </summary>
    public async Task<TagPicture?> DiscogsCoverAsync(string album, string? artist, string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var url = DiscogsSearchUrl(album, artist);
        if (_discogs.TryGetValue(url, out var cached))
        {
            return cached;
        }
        var json = await DiscogsJsonAsync(url, token, cancellationToken).ConfigureAwait(false);
        TagPicture? cover = null;
        foreach (var image in ParseDiscogsCovers(json))
        {
            cover = await DownloadCoverAsync(image, cancellationToken).ConfigureAwait(false);
            if (cover is not null)
            {
                break;
            }
        }
        _discogs[url] = cover;
        return cover;
    }

    /// <summary>One Discogs API request (spaced one second apart; the token in the Authorization header only).</summary>
    private Task<string> DiscogsJsonAsync(string url, string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return _discogsSpacer.RunAsync(async () =>
        {
            _logger.LogInformation("Discogs lookup: {Url}", url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Discogs token=" + token.Trim());
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException("Discogs did not accept the token (Settings › Metadata).");
            }
            if (response.StatusCode == (HttpStatusCode)429)
            {
                throw new HttpRequestException("Discogs is busy; try again in a minute.");
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// The Discogs search URL for releases with a track called <paramref name="title"/> (by <paramref name="artist"/>),
    /// on the album <paramref name="album"/> or, with <paramref name="albumsOnly"/>, on any album.
    /// </summary>
    public static string DiscogsTrackSearchUrl(string title, string? artist, string? album = null, bool albumsOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var url = new StringBuilder($"{DiscogsSearch}?type=release&per_page=50&track={Uri.EscapeDataString(title.Trim())}");
        if (!string.IsNullOrWhiteSpace(artist))
        {
            url.Append("&artist=").Append(Uri.EscapeDataString(artist.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            url.Append("&release_title=").Append(Uri.EscapeDataString(album.Trim()));
        }
        if (albumsOnly)
        {
            url.Append("&format=Album");
        }
        return url.ToString();
    }

    /// <summary>
    /// MD-03 (Discogs first, when the user has a token): releases with this track, best first. Discogs' own order puts
    /// singles named after a well-known song first, so the search narrows step by step and stops at the first that finds
    /// something: on the file's album (when it has an album tag), on any album, then anywhere. Track and disc numbers come
    /// with <see cref="DiscogsCompleteAsync"/> when a match is picked.
    /// </summary>
    public async Task<IReadOnlyList<LookupCandidate>> DiscogsSearchAsync(string title, string? artist, string? album, string token, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(album))
        {
            urls.Add(DiscogsTrackSearchUrl(title, artist, album));
        }
        urls.Add(DiscogsTrackSearchUrl(title, artist, albumsOnly: true));
        urls.Add(DiscogsTrackSearchUrl(title, artist));
        foreach (var url in urls)
        {
            if (!_searches.TryGetValue(url, out var candidates))
            {
                candidates = ParseDiscogsSearch(await DiscogsJsonAsync(url, token, cancellationToken).ConfigureAwait(false), title, artist);
                _searches[url] = candidates;
            }
            if (candidates.Count > 0)
            {
                return candidates;
            }
        }
        return [];
    }

    /// <summary>A Discogs match with its release details: the track's number, disc, exact title and artists.</summary>
    public async Task<LookupCandidate> DiscogsCompleteAsync(LookupCandidate candidate, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Source != LookupSource.Discogs || candidate.IsComplete || candidate.ReleaseId is null)
        {
            return candidate;
        }
        var json = await DiscogsJsonAsync($"https://api.discogs.com/releases/{Uri.EscapeDataString(candidate.ReleaseId)}", token, cancellationToken).ConfigureAwait(false);
        return CompleteFromDiscogsRelease(candidate, json);
    }

    /// <summary>Downloads a cover image (JPEG or PNG, at most 8 MB), cached for the session; null when it is not one.</summary>
    public async Task<TagPicture?> DownloadCoverAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (_covers.TryGetValue(url, out var cached))
        {
            return cached;
        }
        var cover = await DownloadImageAsync(url, cancellationToken).ConfigureAwait(false);
        _covers[url] = cover;
        return cover;
    }

    /// <summary>
    /// Discogs search JSON → candidates, best first ("Artist - Album" titles split; style before genre). Ranked by how
    /// many Discogs users own the release, albums up, singles, promos and unofficial releases down; pressings of the
    /// same album are merged (the most owned one stays).
    /// </summary>
    public static IReadOnlyList<LookupCandidate> ParseDiscogsSearch(string json, string title, string? artist)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<LookupCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            var id = result.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
            var full = String(result, "title");
            if (id is null || full is null)
            {
                continue;
            }
            var dash = full.IndexOf(" - ", StringComparison.Ordinal);
            var albumArtist = dash > 0 ? CleanDiscogsName(full[..dash]) : null;
            var album = dash > 0 ? full[(dash + 3)..].Trim() : full;
            var year = Year(String(result, "year"));
            var cover = String(result, "cover_image");
            var formats = result.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var have = result.TryGetProperty("community", out var community) && community.TryGetProperty("have", out var h) && h.TryGetInt32(out var count) ? count : 0;
            var score = 40 + Math.Min(40, 10 * Math.Log10(have + 1))
                + (formats.Contains("Album") ? 15 : 0)
                - (formats.Contains("Single") ? 20 : 0)
                - (formats.Contains("Promo") ? 10 : 0)
                - (formats.Contains("Unofficial Release") ? 30 : 0)
                - (string.Equals(album, title.Trim(), StringComparison.OrdinalIgnoreCase) ? 10 : 0);
            list.Add(new LookupCandidate(title.Trim(), string.IsNullOrWhiteSpace(artist) ? albumArtist : artist.Trim(), album, albumArtist, year,
                null, null, null, null, (int)Math.Clamp(Math.Round(score), 1, 100), $"discogs:{id}", id)
            {
                Source = LookupSource.Discogs,
                Genre = First(result, "style") ?? First(result, "genre"),
                CoverUrl = cover is not null && cover.StartsWith("https://", StringComparison.Ordinal) && !cover.Contains("spacer.gif", StringComparison.OrdinalIgnoreCase) ? cover : null,
            });
        }
        // Best first (a stable sort keeps Discogs' order for equal scores), one entry per album.
        return [.. list.OrderByDescending(c => c.Score)
            .DistinctBy(c => (c.AlbumArtist?.ToUpperInvariant(), c.Album?.ToUpperInvariant()))];
    }

    /// <summary>
    /// Discogs release JSON → the candidate with the track's number and disc (matched by title in the track list),
    /// the release's artists and year, and its primary image.
    /// </summary>
    public static LookupCandidate CompleteFromDiscogsRelease(LookupCandidate candidate, string json)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var releaseArtist = DiscogsArtists(root) ?? candidate.AlbumArtist;
        var year = root.TryGetProperty("year", out var y) && y.TryGetInt32(out var yy) && yy > 0 ? yy : candidate.Year;
        var cover = candidate.CoverUrl;
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            var best = images.EnumerateArray().OrderBy(i => String(i, "type") == "primary" ? 0 : 1).Select(i => String(i, "uri")).FirstOrDefault(u => u is not null);
            cover = best ?? cover;
        }
        var result = candidate with { AlbumArtist = releaseArtist, Year = year, CoverUrl = cover, IsComplete = true, Album = String(root, "title") ?? candidate.Album };
        if (!root.TryGetProperty("tracklist", out var tracklist) || tracklist.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        var tracks = new List<(string Title, int? Disc, int Number, string? Artist)>();
        var sequence = new Dictionary<int, int>();
        foreach (var t in tracklist.EnumerateArray())
        {
            if (String(t, "type_") is { } kind && kind != "track")
            {
                continue; // headings and index tracks
            }
            var name = String(t, "title");
            if (name is null)
            {
                continue;
            }
            var (disc, number) = Position(String(t, "position"));
            var key = disc ?? 0;
            sequence[key] = sequence.GetValueOrDefault(key) + 1;
            tracks.Add((name, disc, number ?? sequence[key], DiscogsArtists(t)));
        }
        var wanted = Normalise(candidate.Title);
        var match = tracks.FirstOrDefault(t => Normalise(t.Title) == wanted);
        if (match.Title is null)
        {
            match = tracks.FirstOrDefault(t => Normalise(t.Title).Contains(wanted, StringComparison.Ordinal) || wanted.Contains(Normalise(t.Title), StringComparison.Ordinal));
        }
        if (match.Title is null)
        {
            return result;
        }
        return result with
        {
            Title = match.Title,
            Artist = match.Artist ?? releaseArtist ?? candidate.Artist,
            AlbumArtist = match.Artist is not null && match.Artist != releaseArtist ? releaseArtist : null, // only when it differs (compilations)
            Track = match.Number,
            TrackCount = tracks.Count(t => t.Disc == match.Disc),
            Disc = match.Disc,
        };
    }

    /// <summary>"1-3" / "CD2-5" / "2.07" → (disc, track); "7" → (null, 7); vinyl sides ("A1") → (null, null).</summary>
    private static (int? Disc, int? Track) Position(string? position)
    {
        if (position is null)
        {
            return (null, null);
        }
        var m = DiscTrack().Match(position.Trim());
        if (m.Success)
        {
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return int.TryParse(position.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var track) ? (null, track) : (null, null);
    }

    [GeneratedRegex(@"^(?:CD|DVD|Disc)?\s*(\d+)[-.](\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscTrack();

    /// <summary>Lower-case letters and digits only, for comparing titles.</summary>
    private static string Normalise(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>Discogs adds " (2)" to tell same-named artists apart, and "*" for name variations: neither belongs in a tag.</summary>
    private static string CleanDiscogsName(string name) => DiscogsNumber().Replace(name.Trim().TrimEnd('*'), string.Empty).Trim();

    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex DiscogsNumber();

    /// <summary>"artists": names joined with their joins ("A & B"), cleaned.</summary>
    private static string? DiscogsArtists(JsonElement element)
    {
        if (!element.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0)
        {
            return null;
        }
        var text = new StringBuilder();
        foreach (var a in artists.EnumerateArray())
        {
            text.Append(CleanDiscogsName(String(a, "anv") ?? String(a, "name") ?? string.Empty));
            if (String(a, "join") is { } join)
            {
                text.Append(join == "," ? ", " : $" {join} ");
            }
        }
        var joined = text.ToString().Trim();
        return joined.Length > 0 ? joined : null;
    }

    private static string? First(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0
            && array[0].ValueKind == JsonValueKind.String && array[0].GetString() is { Length: > 0 } text ? text : null;

    /// <summary>Discogs search JSON → the results' cover image URLs, best match first (placeholders left out).</summary>
    public static IReadOnlyList<string> ParseDiscogsCovers(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var urls = new List<string>();
        foreach (var result in results.EnumerateArray())
        {
            var image = String(result, "cover_image") ?? String(result, "thumb");
            if (image is not null && image.StartsWith("https://", StringComparison.Ordinal)
                && !image.Contains("spacer.gif", StringComparison.OrdinalIgnoreCase) && !urls.Contains(image))
            {
                urls.Add(image);
            }
        }
        return urls;
    }

    private async Task<TagPicture?> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxCoverBytes)
        {
            return null;
        }
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var mime = response.Content.Headers.ContentType?.MediaType;
        return data.Length is > 0 and <= MaxCoverBytes && mime is "image/jpeg" or "image/png"
            ? new TagPicture(data, mime)
            : null;
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
        _discogsSpacer.Dispose();
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
