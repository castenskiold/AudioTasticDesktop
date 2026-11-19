using System.Net;
using System.Security.Cryptography;

namespace AudioTastic.Input.Midi.Tests;

/// <summary>FM-08: the SoundFont is downloaded on request, checked against its SHA-256, and saved with its licence.</summary>
public sealed class SoundFontDownloadTests : IDisposable
{
    private static readonly byte[] Content = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-sf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_matching_download_is_saved_with_its_licence()
    {
        var progress = new List<double>();
        var download = new SoundFontDownload(new HttpClient(new FakeHandler()));

        await download.DownloadAsync(_dir, new SyncProgress(progress.Add), TestContext.Current.CancellationToken,
            "https://example.invalid/sf2", Hash(Content), "https://example.invalid/licence");

        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(_dir, SoundFontDownload.FileName)));
        Assert.Equal("licence text", File.ReadAllText(Path.Combine(_dir, SoundFontDownload.LicenseFileName)));
        Assert.Equal(1.0, progress[^1]);
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task A_different_file_is_rejected_and_nothing_is_left()
    {
        var download = new SoundFontDownload(new HttpClient(new FakeHandler()));

        await Assert.ThrowsAsync<InvalidDataException>(() => download.DownloadAsync(_dir, null, TestContext.Current.CancellationToken,
            "https://example.invalid/sf2", new string('0', 64), null));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void The_release_constants_describe_generaluser_gs_2_0_3()
    {
        Assert.StartsWith("https://github.com/castenskiold/AudioTasticDesktop/releases/download/soundfont-generaluser-gs-2.0.3/", SoundFontDownload.ReleaseUrl, StringComparison.Ordinal);
        Assert.Equal(64, SoundFontDownload.Sha256.Length);
        Assert.Equal(32_319_396, SoundFontDownload.Size);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("licence", StringComparison.Ordinal)
                    ? new StringContent("licence text")
                    : new ByteArrayContent(Content),
            });
    }
}
