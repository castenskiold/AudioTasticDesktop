using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioTastic.Casting.Chromecast;
using AudioTastic.Casting.Discovery;
using AudioTastic.Casting.Dlna;
using AudioTastic.TestSupport;

namespace AudioTastic.Casting.Tests;

/// <summary>
/// P14 stability: what devices on the local network send (mDNS answers, SSDP replies, UPnP descriptions, Cast
/// messages and media status) is parsed from untrusted bytes. Malformed input must be ignored or refused the way the
/// callers expect, never crash a scan or a cast session.
/// </summary>
public sealed class NetworkFuzzTests
{
    private static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("AUDIOTASTIC_FUZZ_COUNT"), out var n) && n > 0 ? n : 1000;

    [Fact]
    public void Malformed_mdns_answers_never_throw()
    {
        // MdnsBrowser's receive loop catches only socket errors: the parser itself must never throw.
        var packet = new DiscoveryTests.DnsPacket();
        var instance = packet.Record("_googlecast._tcp.local", DnsMessage.TypePtr, p => p.FullName("Chromecast-Ultra-57c3._googlecast._tcp.local"));
        var host = 0;
        packet.Record(instance, DnsMessage.TypeSrv, p =>
        {
            p.Raw(0, 0, 0, 0, 0x1F, 0x49);
            host = p.FullName("57c369bc.local");
        });
        packet.Record(instance, DnsMessage.TypeTxt, p => p.Text("id=57c369bc", "fn=Living Room", "md=Chromecast Ultra", "ca=201221"));
        packet.Record(host, DnsMessage.TypeA, p => p.Raw(192, 168, 0, 141));
        var problems = Fuzz.Run(Fuzz.Mutations(packet.Bytes(), Count, 3), data =>
        {
            var records = DnsMessage.Parse(data);
            _ = MdnsBrowser.ToChromecasts([(System.Net.IPAddress.Loopback, records)]);
        }, []);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
    }

    [Fact]
    public void Malformed_ssdp_replies_and_upnp_descriptions_are_ignored()
    {
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.168.0.199:9197/dmr\r\n" +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\nUSN: uuid:abc::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
        var problems = Fuzz.Run(Fuzz.Mutations(reply, Count, 5), data => _ = SsdpBrowser.ParseResponse(Encoding.UTF8.GetString(data)), []);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));

        var description = Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>TV</friendlyName><UDN>uuid:1</UDN>
              <serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/upnp/control/AVTransport1</controlURL></service></serviceList>
            </device></root>
            """);
        // The browser drops a description that fails with any exception, so only hangs count here.
        problems = Fuzz.Run(Fuzz.Mutations(description, Count / 2, 6),
            data => _ = SsdpBrowser.ParseDescription(Encoding.UTF8.GetString(data), new Uri("http://192.168.0.199:9197/dmr")), [typeof(Exception)]);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));

        problems = Fuzz.Run(Fuzz.Mutations(Encoding.ASCII.GetBytes("http-get:*:audio/flac:*,http-get:*:audio/mpeg:DLNA.ORG_PN=MP3"), Count / 2, 8),
            data => _ = UpnpRenderer.ParseSinkTypes(Encoding.UTF8.GetString(data)), []);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
        problems = Fuzz.Run(Fuzz.Mutations(Encoding.ASCII.GetBytes("01:02:03.500"), Count / 2, 9),
            data => _ = UpnpRenderer.ParseTime(Encoding.UTF8.GetString(data)), []);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
    }

    [Fact]
    public void Malformed_cast_messages_and_media_status_are_refused_cleanly()
    {
        var message = new CastMessage("sender-0", "receiver-0", "urn:x-cast:com.google.cast.media", """{"type":"MEDIA_STATUS"}""").Encode();
        // CastChannel's read loop treats InvalidDataException and EndOfStreamException as a broken connection.
        var problems = Fuzz.Run(Fuzz.Mutations(message, Count, 10), data => _ = CastMessage.Decode(data), [typeof(InvalidDataException), typeof(EndOfStreamException)]);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));

        var status = Encoding.UTF8.GetBytes("""
            {"type":"MEDIA_STATUS","requestId":4,"status":[{"mediaSessionId":1,"playbackRate":1,"playerState":"PLAYING",
             "currentTime":120.5,"idleReason":"FINISHED","media":{"duration":452.1},"volume":{"level":0.4,"muted":false}}]}
            """);
        problems = Fuzz.Run(Fuzz.Mutations(status, Count, 12), data =>
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(Encoding.UTF8.GetString(data)); // the session gets the payload as a string
            }
            catch (JsonException)
            {
                return; // not JSON: refused before the parser sees it
            }
            if (node is JsonObject payload)
            {
                _ = ChromecastSession.ParseMediaStatus(payload);
            }
        }, []);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
    }
}
