using System.Net;
using System.Net.Sockets;
using System.Text;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// FM-10: internet radio against a local SHOUTcast-style server (ICY metadata every 8 KiB): stream info, now-playing
/// titles, station name, reconnects after drops and the end after the last reconnect fails.
/// </summary>
public sealed class StreamTests
{
    public StreamTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
    }

    [Theory]
    [InlineData("StreamTitle='Artist - Song';StreamUrl='';", "Artist - Song")]
    [InlineData("StreamTitle='Guns N' Roses - Don't Cry';", "Guns N' Roses - Don't Cry")]
    [InlineData("StreamTitle='';", null)]
    [InlineData("StreamUrl='x';", null)]
    public void Stream_titles_are_parsed(string packet, string? expected) =>
        Assert.Equal(expected, IcyMetadata.ParseStreamTitle(packet));

    [Fact]
    public void Headers_and_text_encodings_are_parsed()
    {
        Assert.Equal("Test Radio", IcyMetadata.ParseHeader("icy-br: 128\nicy-name: Test Radio\n", "icy-name"));
        Assert.Null(IcyMetadata.ParseHeader("icy-br: 128\n", "icy-name"));
        Assert.Equal("Café", IcyMetadata.DecodeText(Encoding.UTF8.GetBytes("Café")));
        Assert.Equal("Café", IcyMetadata.DecodeText(Encoding.Latin1.GetBytes("Café")));
    }

    [Fact]
    public void Live_stream_reports_station_and_title_and_plays()
    {
        using var server = new IcyServer(TestMedia.Require("sine1k.mp3.mp3"), ["Artist - Song"]);
        var factory = new FfmpegSourceFactory();
        Assert.True(factory.CanOpen(server.Url));

        using var source = factory.Open(server.Url);
        var live = Assert.IsType<LiveStreamSource>(source);
        Assert.False(source.CanSeek);
        Assert.Null(source.LengthFrames);
        Assert.Equal("mp3", source.Info.Codec);
        Assert.Equal("Test Radio", live.StationName);

        var audio = ReadAudio(source, seconds: 3, TimeSpan.FromSeconds(20));
        Assert.True(audio >= 3 * 44_100, $"Only {audio} frames of audio were read.");
        Assert.Equal("Artist - Song", live.StreamTitle);
        Assert.Equal(FfmpegAudioSource.UserAgent, server.UserAgents.First());
    }

    [Fact]
    public void Live_stream_reconnects_after_a_drop_and_ends_when_the_server_is_gone()
    {
        // The server sends the 5 s file once per connection, then closes it; after two connections it answers 503.
        using var server = new IcyServer(TestMedia.Require("sine1k.mp3.mp3"), ["First - One", "Second - Two"]);
        var titles = new List<string?>();
        using var source = new LiveStreamSource(server.Url, FfmpegAudioSource.Open(server.Url), backoffScale: 0.05);
        source.StreamTitleChanged += (_, t) =>
        {
            lock (titles)
            {
                titles.Add(t);
            }
        };

        var buffer = new float[4096];
        var frames = 0L;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var n = source.Read(buffer);
            if (n == 0)
            {
                break; // ended after the reconnects failed
            }
            frames += n;
        }

        Assert.Equal(1, source.Reconnects);
        Assert.True(frames >= 9 * 44_100, $"Only {frames} frames were played across the reconnect.");
        lock (titles)
        {
            Assert.Contains("Second - Two", titles);
        }
        Assert.Equal(0, source.Read(buffer));
    }

    /// <summary>Reads until <paramref name="seconds"/> of non-silent audio have arrived (start-up buffering is silence).</summary>
    private static long ReadAudio(IAudioSource source, int seconds, TimeSpan timeout)
    {
        var buffer = new float[4096];
        long audio = 0;
        var deadline = DateTime.UtcNow + timeout;
        while (audio < seconds * 44_100 && DateTime.UtcNow < deadline)
        {
            var n = source.Read(buffer);
            if (n == 0)
            {
                break;
            }
            if (buffer.AsSpan(0, n * 2).ContainsAnyExcept(0f))
            {
                audio += n;
            }
        }
        return audio;
    }

    /// <summary>A minimal SHOUTcast/Icecast server: one MP3 file per connection, ICY metadata every 8 KiB.</summary>
    private sealed class IcyServer : IDisposable
    {
        private const int MetaInt = 8192;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _audio;
        private readonly IReadOnlyList<string> _titles;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public IcyServer(string mp3, IReadOnlyList<string> titles)
        {
            _audio = File.ReadAllBytes(mp3);
            _titles = titles;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/live";
            _loop = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public List<string> UserAgents { get; } = [];

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            for (var connection = 0; connection < _titles.Count; connection++)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream);
                if (!request.Contains("Icy-MetaData: 1", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The client did not ask for ICY metadata:\n" + request);
                }
                var agent = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase));
                UserAgents.Add(agent?["User-Agent:".Length..].Trim() ?? "");
                var headers = "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\nicy-name: Test Radio\r\n" +
                              $"icy-metaint: {MetaInt}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                await SendAsync(stream, _titles[connection]);
            }
            // Later reconnects are turned away (faster than a refused connection, which takes ~2 s on Windows).
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                await ReadHeadersAsync(stream);
                await stream.WriteAsync("HTTP/1.0 503 Service Unavailable\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
            }
        }

        private async Task SendAsync(NetworkStream stream, string title)
        {
            var meta = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
            var blocks = (meta.Length + 15) / 16;
            var block = new byte[1 + (blocks * 16)];
            block[0] = (byte)blocks;
            meta.CopyTo(block, 1);
            for (var offset = 0; offset < _audio.Length; offset += MetaInt)
            {
                var n = Math.Min(MetaInt, _audio.Length - offset);
                await stream.WriteAsync(_audio.AsMemory(offset, n), _stop.Token);
                if (n == MetaInt)
                {
                    await stream.WriteAsync(block, _stop.Token);
                }
            }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0)
                {
                    break;
                }
                bytes.Add(one[0]);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
