using System.Buffers.Binary;
using System.Net;
using System.Text;
using AudioTastic.Casting.Discovery;

namespace AudioTastic.Casting.Tests;

/// <summary>CN-05: parsing what Chromecasts (mDNS) and DLNA renderers (SSDP + UPnP description) send.</summary>
public sealed class DiscoveryTests
{
    [Fact]
    public void Ptr_query_asks_for_a_unicast_answer()
    {
        var query = DnsMessage.PtrQuery("_googlecast._tcp.local");
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(11, query[12]); // "_googlecast"
        Assert.Equal(DnsMessage.TypePtr, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)));
        Assert.Equal(0x8001, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2)));
        Assert.Empty(DnsMessage.Parse(query)); // a query is not an answer
    }

    [Fact]
    public void Chromecast_answer_with_name_compression_becomes_a_device()
    {
        var packet = new DnsPacket();
        // PTR: service -> instance (instance written in full inside the data)
        var instance = packet.Record("_googlecast._tcp.local", DnsMessage.TypePtr, p => p.FullName("Chromecast-Ultra-57c3._googlecast._tcp.local"));
        // SRV for the instance (owner is a pointer), target host written in full
        var host = 0;
        packet.Record(instance, DnsMessage.TypeSrv, p =>
        {
            p.Raw(0, 0, 0, 0, 0x1F, 0x49); // priority, weight, port 8009
            host = p.FullName("57c369bc.local");
        });
        packet.Record(instance, DnsMessage.TypeTxt, p => p.Text("id=57c369bc", "fn=Living Room", "md=Chromecast Ultra", "ca=201221"));
        packet.Record(host, DnsMessage.TypeA, p => p.Raw(192, 168, 0, 141));

        var records = DnsMessage.Parse(packet.Bytes());
        Assert.Equal(4, records.Count);
        var device = Assert.Single(MdnsBrowser.ToChromecasts([(IPAddress.Parse("192.168.0.9"), records)]));

        Assert.Equal("57c369bc", device.Id);
        Assert.Equal("Living Room", device.Name);
        Assert.Equal(CastDeviceKind.Chromecast, device.Kind);
        Assert.Equal(IPAddress.Parse("192.168.0.141"), device.Address); // from the A record, not the sender
        Assert.Equal(8009, device.Port);
        Assert.Equal("Chromecast Ultra", device.Model);
        Assert.True(device.HasScreen); // ca bit 0 = video out
    }

    [Fact]
    public void Speaker_without_video_has_no_screen_and_broken_packets_are_ignored()
    {
        var packet = new DnsPacket();
        var instance = packet.Record("_googlecast._tcp.local", DnsMessage.TypePtr, p => p.FullName("Speaker._googlecast._tcp.local"));
        packet.Record(instance, DnsMessage.TypeTxt, p => p.Text("ca=2052")); // audio out only
        var bytes = packet.Bytes();
        var device = Assert.Single(MdnsBrowser.ToChromecasts([(IPAddress.Parse("192.168.0.50"), DnsMessage.Parse(bytes))]));
        Assert.False(device.HasScreen);
        Assert.Equal(IPAddress.Parse("192.168.0.50"), device.Address); // no SRV/A: the sender
        Assert.Equal("Speaker", device.Name);

        _ = DnsMessage.Parse(bytes.AsSpan(0, 30)); // truncated: must not throw
        Assert.Empty(DnsMessage.Parse([0x00]));
    }

    [Fact]
    public void Ssdp_answer_headers_are_read()
    {
        var headers = SsdpBrowser.ParseResponse(
            "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.168.0.199:9197/dmr\r\n" +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\nUSN: uuid:abc::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
        Assert.NotNull(headers);
        Assert.Equal("http://192.168.0.199:9197/dmr", headers["LOCATION"]);
        Assert.Null(SsdpBrowser.ParseResponse("NOTIFY * HTTP/1.1\r\n\r\n"));
        Assert.Contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1", Encoding.ASCII.GetString(SsdpBrowser.SearchRequest(SsdpBrowser.MediaRenderer)), StringComparison.Ordinal);
    }

    [Fact]
    public void Upnp_description_gives_name_model_and_absolute_control_urls()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>Living room TV</friendlyName>
                <manufacturer>Samsung Electronics</manufacturer>
                <modelName>UE55AU7175UXXC</modelName>
                <UDN>uuid:0123-4567</UDN>
                <serviceList>
                  <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/upnp/control/RenderingControl1</controlURL></service>
                  <service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><controlURL>/upnp/control/ConnectionManager1</controlURL></service>
                  <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/upnp/control/AVTransport1</controlURL></service>
                </serviceList>
              </device>
            </root>
            """;
        var device = SsdpBrowser.ParseDescription(xml, new Uri("http://192.168.0.199:9197/dmr"));
        Assert.NotNull(device);
        Assert.Equal("uuid:0123-4567", device.Id);
        Assert.Equal("Living room TV", device.Name);
        Assert.Equal("Samsung Electronics UE55AU7175UXXC", device.Model);
        Assert.Equal(CastDeviceKind.Dlna, device.Kind);
        Assert.Equal(IPAddress.Parse("192.168.0.199"), device.Address);
        Assert.Equal("http://192.168.0.199:9197/upnp/control/AVTransport1", device.Details["avTransport"]);
        Assert.Equal("http://192.168.0.199:9197/upnp/control/RenderingControl1", device.Details["renderingControl"]);
        Assert.Null(device.HasScreen); // known only after asking the renderer (CN-02)

        Assert.Null(SsdpBrowser.ParseDescription(xml.Replace("AVTransport", "Something", StringComparison.Ordinal), new Uri("http://192.168.0.199:9197/dmr")));
    }

    /// <summary>Builds a DNS answer with compressed names, like real mDNS responders send.</summary>
    internal sealed class DnsPacket
    {
        private readonly List<byte> _bytes = [0, 0, 0x84, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        private int _count;

        /// <summary>A record whose owner name is written in full; returns the offset of the name its data wrote first.</summary>
        public int Record(string owner, ushort type, Action<Data> data)
        {
            WriteName(_bytes, owner);
            return Finish(type, data);
        }

        /// <summary>A record whose owner name is a pointer to an earlier name.</summary>
        public int Record(int ownerOffset, ushort type, Action<Data> data)
        {
            _bytes.Add((byte)(0xC0 | (ownerOffset >> 8)));
            _bytes.Add((byte)ownerOffset);
            return Finish(type, data);
        }

        public byte[] Bytes()
        {
            _bytes[6] = (byte)(_count >> 8);
            _bytes[7] = (byte)_count;
            return [.. _bytes];
        }

        private int Finish(ushort type, Action<Data> fill)
        {
            _bytes.AddRange([(byte)(type >> 8), (byte)type, 0x80, 0x01, 0, 0, 0x11, 0x94, 0, 0]);
            var lengthAt = _bytes.Count - 2;
            var data = new Data(_bytes);
            fill(data);
            var length = _bytes.Count - lengthAt - 2;
            _bytes[lengthAt] = (byte)(length >> 8);
            _bytes[lengthAt + 1] = (byte)length;
            _count++;
            return data.FirstName;
        }

        private static void WriteName(List<byte> bytes, string name)
        {
            foreach (var label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.UTF8.GetBytes(label));
            }
            bytes.Add(0);
        }

        public sealed class Data(List<byte> bytes)
        {
            public int FirstName { get; private set; }

            public int FullName(string name)
            {
                var offset = bytes.Count;
                if (FirstName == 0)
                {
                    FirstName = offset;
                }
                WriteName(bytes, name);
                return offset;
            }

            public void Raw(params byte[] data) => bytes.AddRange(data);

            public void Text(params string[] entries)
            {
                foreach (var entry in entries)
                {
                    bytes.Add((byte)Encoding.UTF8.GetByteCount(entry));
                    bytes.AddRange(Encoding.UTF8.GetBytes(entry));
                }
            }
        }
    }
}
