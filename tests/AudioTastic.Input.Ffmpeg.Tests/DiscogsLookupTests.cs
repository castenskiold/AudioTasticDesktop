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
