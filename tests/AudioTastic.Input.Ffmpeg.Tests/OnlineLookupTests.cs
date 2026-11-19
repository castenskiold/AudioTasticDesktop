using System.Net;
using AudioTastic.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>MD-03 and the P9 exit criterion: MusicBrainz requests are never more than one per second.</summary>
public sealed class OnlineLookupTests
{
    private const string Answer = """
        {
          "recordings": [
            {
              "id": "rec-1", "score": 100, "title": "Mundo Imundo", "length": 482000,
              "artist-credit": [ { "name": "Demonizz", "joinphrase": " feat. " }, { "name": "Guest" } ],
              "releases": [
                { "id": "rel-1", "title": "Mind Games", "date": "2019-05-03",
                  "artist-credit": [ { "name": "Various Artists" } ],
                  "media": [ { "position": 1, "track-count": 9, "track": [ { "number": "4" } ] } ] },
                { "id": "rel-2", "title": "Single", "date": "2018",
                  "media": [ { "position": 1, "track-count": 2, "track": [ { "number": "1" } ] } ] }
              ]
            },
            { "id": "rec-2", "score": 60, "title": "Mundo", "first-release-date": "2001-02" }
          ]
        }
        """;

    [Fact]
    public void A_musicbrainz_answer_becomes_candidates()
    {
        var candidates = OnlineLookupService.ParseRecordings(Answer);

        Assert.Equal(3, candidates.Count);
        var first = candidates[0];
        Assert.Equal(("Mundo Imundo", "Demonizz feat. Guest", "Mind Games", "Various Artists"), (first.Title, first.Artist, first.Album, first.AlbumArtist));
        Assert.Equal((2019, 4, 9, 1), (first.Year, first.Track, first.TrackCount, first.Disc));
        Assert.Equal(TimeSpan.FromSeconds(482), first.Length);
        Assert.Equal("rel-1", first.ReleaseId);
        Assert.Equal("4", first.Fields[TagField.Track]);
        Assert.Null(candidates[1].AlbumArtist); // no release credit
        Assert.Equal((60, 2001, (string?)null), (candidates[2].Score, candidates[2].Year, candidates[2].ReleaseId));
    }

    [Fact]
    public void Search_urls_quote_and_escape()
    {
        var url = OnlineLookupService.SearchUrl("Say \"Hi\"", "AC/DC", TimeSpan.FromSeconds(200));

        Assert.StartsWith(OnlineLookupService.MusicBrainzSearch + "?query=", url, StringComparison.Ordinal);
        var query = Uri.UnescapeDataString(url.Split("query=")[1].Split('&')[0]);
        Assert.Equal("recording:\"Say \\\"Hi\\\"\" AND artist:\"AC/DC\" AND dur:[190000 TO 210000]", query);
        Assert.Contains("&fmt=json", url, StringComparison.Ordinal);
        Assert.Matches(@"^AudioTastic/\d+\.\d+\.\d+ \( .+ \)$", OnlineLookupService.UserAgent);
    }

    [Fact]
    public async Task Requests_are_at_least_one_second_apart_and_answers_are_cached()
    {
        var clock = DateTimeOffset.UnixEpoch;
        var sent = new List<DateTimeOffset>();
        var handler = new FakeHandler(_ =>
        {
            sent.Add(clock);
            clock += TimeSpan.FromMilliseconds(150); // the request itself takes a while
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        });
        var spacer = new RequestSpacer(TimeSpan.FromSeconds(1), () => clock, (wait, _) =>
        {
            clock += wait;
            return Task.CompletedTask;
        });
        using var service = new OnlineLookupService(new HttpClient(handler), spacer, NullLogger<OnlineLookupService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        await service.SearchAsync("A", null, null, ct);
        await service.SearchAsync("B", null, null, ct);
        await service.SearchAsync("A", null, null, ct); // cached: no request
        await Task.WhenAll(service.SearchAsync("C", "x", null, ct), service.SearchAsync("D", "y", null, ct)); // concurrent callers queue

        Assert.Equal(4, sent.Count);
        Assert.All(sent.Zip(sent.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1), $"{pair.First:O} then {pair.Second:O}"));
        Assert.All(handler.UserAgents, ua => Assert.StartsWith("AudioTastic/", ua, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_without_a_cover_gives_none()
    {
        var handler = new FakeHandler(r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("rel-1", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } },
        });
        using var service = new OnlineLookupService(new HttpClient(handler), new RequestSpacer(TimeSpan.Zero), NullLogger<OnlineLookupService>.Instance);

        Assert.Null(await service.FrontCoverAsync("rel-1", TestContext.Current.CancellationToken));
        var cover = await service.FrontCoverAsync("rel-2", TestContext.Current.CancellationToken);
        Assert.Equal(("image/png", 3), (cover!.MimeType, cover.Data.Length));
        Assert.EndsWith("/release/rel-2/front-500", handler.Urls[^1], StringComparison.Ordinal);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Urls)
            {
                Urls.Add(request.RequestUri!.ToString());
                UserAgents.Add(request.Headers.UserAgent.ToString());
                return Task.FromResult(respond(request));
            }
        }
    }
}
