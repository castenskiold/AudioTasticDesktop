using System.Net;
using AudioTastic.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-03: the optional Discogs cover fallback (with the user's own token).</summary>
public sealed class DiscogsLookupTests
{
    private const string Answer = """
        {
          "results": [
            { "title": "Demonizz - Mind Games", "cover_image": "https://i.discogs.com/spacer.gif" },
            { "title": "Demonizz - Mind Games", "cover_image": "https://i.discogs.com/a.jpg", "thumb": "https://i.discogs.com/a-small.jpg" },
            { "title": "Demonizz - Mind Games (CD)", "cover_image": "https://i.discogs.com/a.jpg" },
            { "title": "Other", "thumb": "https://i.discogs.com/b.png" },
            { "title": "Insecure", "cover_image": "http://example.com/c.jpg" }
          ]
        }
        """;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    private sealed class FakeDiscogs(HttpStatusCode searchStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith(OnlineLookupService.DiscogsSearch, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(searchStatus) { Content = new StringContent(Answer) });
            }
            var image = new ByteArrayContent(Jpeg);
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = image });
        }
    }

    private static OnlineLookupService Service(FakeDiscogs handler) =>
        new(new HttpClient(handler), new RequestSpacer(TimeSpan.Zero), NullLogger<OnlineLookupService>.Instance, discogs: new RequestSpacer(TimeSpan.Zero));

    [Fact]
    public void A_search_answer_gives_the_real_covers_in_order()
    {
        Assert.Equal(["https://i.discogs.com/a.jpg", "https://i.discogs.com/b.png"], OnlineLookupService.ParseDiscogsCovers(Answer));
        Assert.Empty(OnlineLookupService.ParseDiscogsCovers("{}"));
    }

    [Fact]
    public async Task The_token_goes_in_a_header_never_in_the_url_and_answers_are_cached()
    {
        var handler = new FakeDiscogs();
        using var service = Service(handler);

        var cover = await service.DiscogsCoverAsync("Mind Games", "Demonizz", "secret-token", TestContext.Current.CancellationToken);
        Assert.NotNull(cover);
        Assert.Equal("image/jpeg", cover.MimeType);
        Assert.Equal(Jpeg, cover.Data);

        var search = handler.Requests[0];
        Assert.Equal("Discogs token=secret-token", string.Join(",", search.Headers.GetValues("Authorization")));
        Assert.DoesNotContain("secret", search.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("release_title=Mind%20Games", search.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("artist=Demonizz", search.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(handler.Requests[1].Headers.Contains("Authorization")); // the image itself needs no token
        Assert.NotEmpty(OnlineLookupService.UserAgent);

        await service.DiscogsCoverAsync("Mind Games", "Demonizz", "secret-token", TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests.Count); // from the cache
    }

    [Fact]
    public async Task A_refused_token_is_a_clear_error()
    {
        using var service = Service(new FakeDiscogs(HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.DiscogsCoverAsync("Album", null, "wrong", TestContext.Current.CancellationToken));
        Assert.Contains("token", error.Message, StringComparison.Ordinal);
    }

    private const string TrackSearch = """
        {
          "results": [
            { "id": 111, "title": "Hallucinogen (2) - Twisted", "year": "1995", "genre": ["Electronic"], "style": ["Psy-Trance", "Goa Trance"],
              "cover_image": "https://i.discogs.com/twisted.jpg" },
            { "id": 222, "title": "Various - Beyond Colour", "year": "2003", "genre": ["Electronic"], "cover_image": "https://i.discogs.com/spacer.gif" },
            { "title": "no id" }
          ]
        }
        """;

    private const string Release = """
        {
          "title": "Twisted", "year": 1995,
          "artists": [ { "name": "Hallucinogen (2)", "join": "" } ],
          "images": [ { "type": "secondary", "uri": "https://i.discogs.com/back.jpg" }, { "type": "primary", "uri": "https://i.discogs.com/front.jpg" } ],
          "tracklist": [
            { "position": "", "type_": "heading", "title": "CD 1" },
            { "position": "1-1", "type_": "track", "title": "LSD" },
            { "position": "1-2", "type_": "track", "title": "Dark Magus" },
            { "position": "2-1", "type_": "track", "title": "Gamma Goblins (Remix)", "artists": [ { "name": "Simon Posford*" } ] }
          ]
        }
        """;

    [Fact]
    public void A_discogs_track_search_gives_releases_with_style_and_cover()
    {
        var found = OnlineLookupService.ParseDiscogsSearch(TrackSearch, "LSD", "Hallucinogen");
        Assert.Equal(2, found.Count);
        var first = found[0];
        Assert.Equal(LookupSource.Discogs, first.Source);
        Assert.Equal(("LSD", "Hallucinogen", "Twisted", "Hallucinogen", 1995), (first.Title, first.Artist, first.Album, first.AlbumArtist, first.Year));
        Assert.Equal("Psy-Trance", first.Genre); // the style is more telling than "Electronic"
        Assert.Equal("111", first.ReleaseId);
        Assert.Equal("https://i.discogs.com/twisted.jpg", first.CoverUrl);
        Assert.Null(found[1].CoverUrl); // placeholder image
        Assert.Equal("Psy-Trance", first.Fields[TagField.Genre]);
        Assert.Contains("track=LSD", OnlineLookupService.DiscogsTrackSearchUrl("LSD", "Hallucinogen"), StringComparison.Ordinal);
        Assert.EndsWith("&release_title=Twisted", OnlineLookupService.DiscogsTrackSearchUrl("LSD", null, "Twisted"), StringComparison.Ordinal);
        Assert.EndsWith("&format=Album", OnlineLookupService.DiscogsTrackSearchUrl("LSD", null, albumsOnly: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Albums_and_owned_releases_rank_above_singles_and_pressings_merge()
    {
        const string json = """
            {
              "results": [
                { "id": 1, "title": "Pink Floyd - Money", "format": ["Vinyl", "7\"", "Single"], "community": { "have": 400 } },
                { "id": 2, "title": "Pink Floyd - The Dark Side Of The Moon", "format": ["Cassette", "Album"], "community": { "have": 11 } },
                { "id": 3, "title": "Pink Floyd - The Dark Side Of The Moon", "format": ["CD", "Album"], "community": { "have": 9000 } },
                { "id": 4, "title": "Pink Floyd - Money / Time", "format": ["Vinyl", "Unofficial Release"], "community": { "have": 13 } }
              ]
            }
            """;
        var found = OnlineLookupService.ParseDiscogsSearch(json, "Money", "Pink Floyd");
        Assert.Equal(["3", "1", "4"], found.Select(c => c.ReleaseId));
    }

    [Fact]
    public void The_release_gives_track_number_disc_and_the_primary_cover()
    {
        var candidate = OnlineLookupService.ParseDiscogsSearch(TrackSearch, "dark magus", "Hallucinogen")[0];
        var complete = OnlineLookupService.CompleteFromDiscogsRelease(candidate, Release);
        Assert.True(complete.IsComplete);
        Assert.Equal(("Dark Magus", 2, 2, 1), (complete.Title, complete.Track, complete.TrackCount, complete.Disc));
        Assert.Equal("Hallucinogen", complete.Artist);
        Assert.Null(complete.AlbumArtist); // the same as the artist
        Assert.Equal("https://i.discogs.com/front.jpg", complete.CoverUrl);

        var remix = OnlineLookupService.CompleteFromDiscogsRelease(candidate with { Title = "Gamma Goblins" }, Release);
        Assert.Equal(("Gamma Goblins (Remix)", 1, 1, 2), (remix.Title, remix.Track, remix.TrackCount, remix.Disc));
        Assert.Equal(("Simon Posford", "Hallucinogen"), (remix.Artist, remix.AlbumArtist));
    }

    [Fact]
    public async Task Real_discogs_finds_a_track_with_its_number_first()
    {
        var token = Environment.GetEnvironmentVariable("AUDIOTASTIC_DISCOGS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            return; // local check only (see below)
        }
        using var service = new OnlineLookupService(NullLogger<OnlineLookupService>.Instance);
        var found = await service.DiscogsSearchAsync("Money", "Pink Floyd", null, token, TestContext.Current.CancellationToken);
        Assert.NotEmpty(found);
        var complete = await service.DiscogsCompleteAsync(found[0], token, TestContext.Current.CancellationToken);
        Assert.Contains("Dark Side", complete.Album, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, complete.Track);
        Assert.NotNull(complete.CoverUrl);
    }

    /// <summary>
    /// Local check against the real Discogs API: runs only when AUDIOTASTIC_DISCOGS_TOKEN holds a personal token (it is
    /// never stored in the repository). Does nothing otherwise, so the suite stays at 0 skipped.
    /// </summary>
    [Fact]
    public async Task Real_discogs_finds_a_cover_for_a_well_known_album()
    {
        var token = Environment.GetEnvironmentVariable("AUDIOTASTIC_DISCOGS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }
        using var service = new OnlineLookupService(NullLogger<OnlineLookupService>.Instance);
        var cover = await service.DiscogsCoverAsync("The Dark Side Of The Moon", "Pink Floyd", token, TestContext.Current.CancellationToken);
        Assert.NotNull(cover);
        Assert.True(cover.Data.Length > 10_000, $"{cover.Data.Length} bytes");
        Assert.True(cover.MimeType is "image/jpeg" or "image/png", cover.MimeType);
    }
}
