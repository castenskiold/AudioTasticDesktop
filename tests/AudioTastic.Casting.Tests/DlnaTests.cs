using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using AudioTastic.Casting.Discovery;
using AudioTastic.Casting.Dlna;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Casting.Tests;

/// <summary>CN-02: DLNA/UPnP renderers: SOAP, DIDL-Lite, format choice, and the remote player against a fake renderer.</summary>
public sealed class DlnaTests
{
    private static readonly HashSet<string> Samsung = new(StringComparer.OrdinalIgnoreCase) { "audio/mpeg", "audio/x-flac", "audio/x-wav", "audio/mp4", "video/mp4" };

    [Fact]
    public void Sink_types_come_from_the_http_get_entries()
    {
        var types = UpnpRenderer.ParseSinkTypes(
            "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3,http-get:*:audio/x-flac:*, rtsp-rtp-udp:*:audio/L16:*,http-get:*:Audio/WAV:*,http-get:*:video/mp4:*");
        Assert.Equal(["audio/mpeg", "audio/wav", "audio/x-flac", "video/mp4"], types.Order(StringComparer.Ordinal));
        Assert.Empty(UpnpRenderer.ParseSinkTypes(null));
    }

    [Fact]
    public void Format_is_the_original_when_listed_else_wav_then_flac_or_mp3_with_the_devices_spelling()
    {
        StreamFormat[] all = [StreamFormat.Original, StreamFormat.Wav, StreamFormat.Flac, StreamFormat.Mp3];
        Assert.Equal((StreamFormat.Original, "audio/x-flac"), DlnaRemote.FormatFor(@"C:\m\a.flac", "flac", Samsung, all));
        Assert.Equal((StreamFormat.Original, "audio/mpeg"), DlnaRemote.FormatFor(@"C:\m\a.mp3", "mp3", Samsung, all));
        Assert.Equal((StreamFormat.Original, "audio/x-wav"), DlnaRemote.FormatFor(@"C:\m\a.wav", "pcm_s16le", Samsung, all));
        Assert.Equal((StreamFormat.Wav, "audio/x-wav"), DlnaRemote.FormatFor(@"C:\m\a.ape", "ape", Samsung, all));
        Assert.Equal((StreamFormat.Wav, "audio/x-wav"), DlnaRemote.FormatFor(@"C:\m\a.wav", "pcm_f32le", Samsung, all)); // codec not direct
        Assert.Equal((StreamFormat.Wav, "audio/x-wav"), DlnaRemote.FormatFor(@"C:\m\a.ogg", "vorbis", Samsung, all)); // ogg not listed

        var noWav = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/mpeg", "audio/flac" };
        Assert.Equal((StreamFormat.Flac, "audio/flac"), DlnaRemote.FormatFor(@"C:\m\a.wv", "wavpack", noWav, all));
        Assert.Equal((StreamFormat.Mp3, "audio/mpeg"), DlnaRemote.FormatFor(@"C:\m\a.wv", "wavpack", noWav, [StreamFormat.Mp3]));

        // Not reported: MP3, FLAC and WAV assumed.
        var unknown = new HashSet<string>();
        Assert.Equal((StreamFormat.Original, "audio/flac"), DlnaRemote.FormatFor(@"C:\m\a.flac", "flac", unknown, all));
        Assert.Equal((StreamFormat.Wav, "audio/wav"), DlnaRemote.FormatFor(@"C:\m\a.m4a", "aac", unknown, all));
    }

    [Fact]
    public void Didl_describes_the_track_with_its_resource()
    {
        var url = new Uri("http://192.168.0.70:5000/media/abc/" + Uri.EscapeDataString("Kunsten i Århus.flac"));
        var text = DlnaRemote.Didl(url, "http-get:*:audio/x-flac:DLNA.ORG_OP=01", "Rock & Roll <live>", "AC/DC", "Album", new Uri("http://192.168.0.70:5000/media/def/cover.jpg"),
            TimeSpan.FromSeconds(3723.4));
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        var item = XDocument.Parse(text).Root!.Element(didl + "item")!;
        Assert.Equal("Rock & Roll <live>", item.Element(dc + "title")!.Value);
        Assert.Equal("AC/DC", item.Element(upnp + "artist")!.Value);
        Assert.Equal("object.item.audioItem.musicTrack", item.Element(upnp + "class")!.Value);
        Assert.Equal("http://192.168.0.70:5000/media/def/cover.jpg", item.Element(upnp + "albumArtURI")!.Value);
        var res = item.Element(didl + "res")!;
        Assert.Equal("http://192.168.0.70:5000/media/abc/Kunsten%20i%20%C3%85rhus.flac", res.Value); // escaped: TVs refuse raw spaces (UPnP 716)
        Assert.Equal("http-get:*:audio/x-flac:DLNA.ORG_OP=01", res.Attribute("protocolInfo")!.Value);
        Assert.Equal("1:02:03.000", res.Attribute("duration")!.Value);
    }

    [Theory]
    [InlineData("0:03:12", 192.0)]
    [InlineData("00:03:12.500", 192.5)]
    [InlineData("1:00:00", 3600.0)]
    [InlineData("0:00:05.1/10", 5.0)]
    public void Times_parse(string text, double seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), UpnpRenderer.ParseTime(text));

    [Fact]
    public void Unknown_times_and_formatting()
    {
        Assert.Null(UpnpRenderer.ParseTime("NOT_IMPLEMENTED"));
        Assert.Null(UpnpRenderer.ParseTime(""));
        Assert.Equal("0:01:05", UpnpRenderer.FormatTime(TimeSpan.FromSeconds(65.9)));
        Assert.Equal("2:00:00", UpnpRenderer.FormatTime(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Device_volume_follows_the_player_in_proportion()
    {
        Assert.Equal(15, DlnaRemote.DeviceVolume(15, 80, 80));
        Assert.Equal(8, DlnaRemote.DeviceVolume(15, 80, 40));
        Assert.Equal(19, DlnaRemote.DeviceVolume(15, 80, 100));
        Assert.Equal(0, DlnaRemote.DeviceVolume(15, 80, 0));
        Assert.Equal(100, DlnaRemote.DeviceVolume(90, 10, 100));
        Assert.Equal(15, DlnaRemote.DeviceVolume(0, 80, 50)); // no reference
        Assert.Null(DlnaRemote.DeviceVolume(null, 80, 50));
    }

    [Fact]
    public void Envelope_carries_the_action_and_escaped_arguments()
    {
        var text = UpnpRenderer.Envelope("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURIMetaData", "<DIDL-Lite>&</DIDL-Lite>")]);
        XNamespace u = "urn:schemas-upnp-org:service:AVTransport:1";
        var action = XDocument.Parse(text).Descendants(u + "SetAVTransportURI").Single();
        Assert.Equal("<DIDL-Lite>&</DIDL-Lite>", action.Element("CurrentURIMetaData")!.Value);
        Assert.Contains("&lt;DIDL-Lite&gt;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_plays_seeks_pauses_and_detects_the_end_on_a_fake_renderer()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TestMedia.Require("clicks.flac.flac");
        await using var renderer = await FakeRenderer.StartAsync(ct);
        await using var server = new StreamServer([new FfmpegSourceFactory()], [new FfmpegEncoderFactory()], NullLogger<StreamServer>.Instance);
        await server.StartAsync(0, IPAddress.Loopback, ct);

        await using var remote = await DlnaRemote.ConnectAsync(renderer.Device, server, _ => null, 80, NullLogger.Instance, ct);
        var track = new TrackInfo(path, "Clicks", "Tester", new AudioStreamInfo("flac", 44100, 2, 16, 0, true), TimeSpan.FromSeconds(10));
        await remote.LoadAsync(new PlaylistEntry(path), track, TimeSpan.Zero, paused: false);

        Assert.Equal(["GetProtocolInfo", "GetTransportInfo", "GetVolume", "Stop", "SetAVTransportURI", "Play"], renderer.Actions.Take(6));
        var uri = renderer.CurrentUri!;
        Assert.Contains("http-get:*:audio/x-flac:DLNA.ORG_OP=01", renderer.Metadata, StringComparison.Ordinal);

        // The stream answers the DLNA header query with the content features.
        using (var http = new HttpClient())
        using (var request = new HttpRequestMessage(HttpMethod.Head, uri))
        {
            request.Headers.Add("getcontentFeatures.dlna.org", "1");
            using var response = await http.SendAsync(request, ct);
            Assert.Equal("audio/x-flac", response.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("DLNA.ORG_OP=01;DLNA.ORG_CI=0", response.Headers.GetValues("contentFeatures.dlna.org").Single(), StringComparison.Ordinal);
            Assert.Equal("Streaming", response.Headers.GetValues("transferMode.dlna.org").Single());
        }

        renderer.State = "PLAYING";
        renderer.RelTime = "0:00:04";
        await WaitUntil(() => remote.Position >= TimeSpan.FromSeconds(4), ct);
        Assert.False(remote.HasEnded);

        await remote.SeekAsync(TimeSpan.FromSeconds(7));
        Assert.Equal("0:00:07", renderer.LastSeek);
        await remote.PauseAsync();
        Assert.Contains("Pause", renderer.Actions);
        await remote.SetVolumeAsync(40, false);
        Assert.Equal(10, renderer.Volume); // 20 at 80 % → 10 at 40 %

        renderer.State = "STOPPED";
        await WaitUntil(() => remote.HasEnded, ct);
    }

    [Fact]
    public async Task Remote_is_lost_when_another_app_takes_the_renderer_over()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TestMedia.Require("clicks.mp3.mp3");
        await using var renderer = await FakeRenderer.StartAsync(ct);
        await using var server = new StreamServer([new FfmpegSourceFactory()], [new FfmpegEncoderFactory()], NullLogger<StreamServer>.Instance);
        await server.StartAsync(0, IPAddress.Loopback, ct);
        await using var remote = await DlnaRemote.ConnectAsync(renderer.Device, server, _ => null, 80, NullLogger.Instance, ct);
        var lost = new TaskCompletionSource();
        remote.Lost += (_, _) => lost.TrySetResult();
        var track = new TrackInfo(path, "Clicks", null, new AudioStreamInfo("mp3", 44100, 2, 16, 128000, false), TimeSpan.FromSeconds(10));
        await remote.LoadAsync(new PlaylistEntry(path), track, TimeSpan.Zero, paused: false);
        renderer.State = "PLAYING";
        await WaitUntil(() => remote.Position > TimeSpan.Zero, ct);

        renderer.CurrentUri = "http://192.168.0.5:8000/someone-else.mp3";
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(remote.HasEnded);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>A minimal UPnP media renderer: answers the SOAP actions DlnaRemote uses and records them.</summary>
    private sealed class FakeRenderer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeRenderer(WebApplication app, int port)
        {
            _app = app;
            var root = $"http://127.0.0.1:{port}/";
            Device = new CastDevice("uuid:fake", "Fake TV", CastDeviceKind.Dlna, IPAddress.Loopback, port, "Fake", null,
                new Dictionary<string, string>
                {
                    ["avTransport"] = root + "avt",
                    ["avTransportType"] = "urn:schemas-upnp-org:service:AVTransport:1",
                    ["renderingControl"] = root + "rc",
                    ["connectionManager"] = root + "cm",
                });
        }

        public CastDevice Device { get; }

        public ConcurrentQueue<string> Actions { get; } = new();

        public volatile string State = "NO_MEDIA_PRESENT";
        public volatile string RelTime = "0:00:00";
        public volatile string? CurrentUri;
        public volatile string Metadata = "";
        public volatile string? LastSeek;
        public int Volume = 20;

        public static async Task<FakeRenderer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            builder.Services.AddRoutingCore();
            var app = builder.Build();
            FakeRenderer? fake = null;
            app.UseRouting();
            app.MapPost("/{service}", async (HttpContext context) =>
            {
                var action = context.Request.Headers["SOAPAction"].ToString().Trim('"').Split('#')[1];
                var body = XDocument.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
                var args = body.Descendants().Where(e => !e.HasElements).ToDictionary(e => e.Name.LocalName, e => e.Value);
                return Results.Content(fake!.Answer(action, args), "text/xml");
            });
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            fake = new FakeRenderer(app, new Uri(address).Port);
            return fake;
        }

        private string Answer(string action, Dictionary<string, string> args)
        {
            Actions.Enqueue(action);
            var output = new List<(string, string)>();
            switch (action)
            {
                case "GetProtocolInfo":
                    output.Add(("Source", ""));
                    output.Add(("Sink", "http-get:*:audio/mpeg:*,http-get:*:audio/x-flac:*,http-get:*:audio/x-wav:*"));
                    break;
                case "GetTransportInfo":
                    output.Add(("CurrentTransportState", State));
                    output.Add(("CurrentTransportStatus", "OK"));
                    break;
                case "GetPositionInfo":
                    output.Add(("RelTime", RelTime));
                    output.Add(("TrackDuration", "0:00:10"));
                    output.Add(("TrackURI", CurrentUri ?? ""));
                    break;
                case "GetVolume":
                    output.Add(("CurrentVolume", Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                case "SetVolume":
                    Volume = int.Parse(args["DesiredVolume"], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "SetAVTransportURI":
                    CurrentUri = args["CurrentURI"];
                    Metadata = args["CurrentURIMetaData"];
                    State = "STOPPED";
                    break;
                case "Seek":
                    LastSeek = args["Target"];
                    break;
                case "Pause":
                    State = "PAUSED_PLAYBACK";
                    break;
                default:
                    break;
            }
            XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace u = "urn:schemas-upnp-org:service:AVTransport:1";
            return new XElement(s + "Envelope", new XElement(s + "Body",
                new XElement(u + (action + "Response"), output.Select(o => new XElement(o.Item1, o.Item2))))).ToString();
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
