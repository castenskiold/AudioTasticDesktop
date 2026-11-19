using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using AudioTastic.Casting;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Casting.Tests;

/// <summary>CN-04: the streaming server and the WAV stream it serves for formats a device cannot play.</summary>
public sealed class StreamServerTests
{
    private static readonly FfmpegSourceFactory Ffmpeg = new();

    [Fact]
    public void Wav_stream_of_a_16_bit_file_is_bit_exact_with_the_original_samples()
    {
        var path = TestMedia.Require("clicks.flac.flac");
        var (rate, channels, reference) = ReferenceWav.Read(TestMedia.Require("clicks.wav16.wav"));
        using var wav = new WavStream(Ffmpeg.Open(path));
        var bytes = ReadAll(wav);

        Assert.Equal(wav.Length, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(rate, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(channels, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
        var samples = (bytes.Length - 44) / 2;
        Assert.Equal(reference.Length, samples);
        for (var i = 0; i < samples; i++)
        {
            Assert.Equal(WavStream.ToPcm16(reference[i]), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (i * 2))));
        }
    }

    [Fact]
    public void Wav_stream_reads_the_same_bytes_from_any_position()
    {
        var path = TestMedia.Require("clicks.flac.flac");
        byte[] all;
        using (var whole = new WavStream(Ffmpeg.Open(path)))
        {
            all = ReadAll(whole);
        }
        using var wav = new WavStream(Ffmpeg.Open(path));
        foreach (var offset in new long[] { all.Length / 2 + 1, 10, all.Length - 100, 44, 45 })
        {
            wav.Position = offset;
            var part = new byte[Math.Min(5000, all.Length - offset)];
            var read = 0;
            while (read < part.Length)
            {
                var n = wav.Read(part, read, part.Length - read);
                Assert.True(n > 0);
                read += n;
            }
            Assert.Equal(all.AsSpan((int)offset, part.Length).ToArray(), part);
        }
    }

    [Fact]
    public async Task Server_serves_originals_and_wav_with_ranges_and_forgets_revoked_urls()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = TestMedia.Require("clicks.mp3.mp3");
        var flac = TestMedia.Require("clicks.flac.flac");
        await using var server = new StreamServer([Ffmpeg], NullLogger<StreamServer>.Instance);
        await server.StartAsync(0, IPAddress.Loopback, ct);
        using var http = new HttpClient();

        var mp3Url = server.Publish(original, StreamFormat.Original);
        Assert.EndsWith("/clicks.mp3.mp3", mp3Url.AbsolutePath, StringComparison.Ordinal);
        using (var response = await http.GetAsync(mp3Url, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(await File.ReadAllBytesAsync(original, ct), await response.Content.ReadAsByteArrayAsync(ct));
        }

        var wavUrl = server.Publish(flac, StreamFormat.Wav);
        byte[] expected;
        using (var wav = new WavStream(Ffmpeg.Open(flac)))
        {
            expected = ReadAll(wav);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, wavUrl))
        {
            request.Headers.Range = new RangeHeaderValue(1000, 1999);
            using var response = await http.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expected.Length, response.Content.Headers.ContentRange?.Length);
            Assert.Equal(expected.AsSpan(1000, 1000).ToArray(), await response.Content.ReadAsByteArrayAsync(ct));
        }

        using (var head = new HttpRequestMessage(HttpMethod.Head, wavUrl))
        using (var response = await http.SendAsync(head, ct))
        {
            Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        }

        server.Revoke(mp3Url);
        using (var response = await http.GetAsync(mp3Url, ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var response = await http.GetAsync(new Uri(server.BaseAddress!, "media/0123456789abcdef0123456789abcdef/x.wav"), ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.32.0.5", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.2", true)]
    public void Only_local_network_addresses_are_served(string address, bool local) =>
        Assert.Equal(local, StreamServer.IsLocalNetwork(IPAddress.Parse(address)));

    [Theory]
    [InlineData("a.MP3", "audio/mpeg")]
    [InlineData("a.flac", "audio/flac")]
    [InlineData("a.m4a", "audio/mp4")]
    [InlineData("a.opus", "audio/ogg; codecs=opus")]
    [InlineData("a.ape", "application/octet-stream")]
    public void Content_types_follow_the_extension(string file, string type) => Assert.Equal(type, StreamServer.ContentTypeOf(file));

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
