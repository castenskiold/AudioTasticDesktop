using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Discovery;

/// <summary>
/// CN-05: finds DLNA/UPnP media renderers (smart TVs, Sonos, network receivers, Kodi) with SSDP: an M-SEARCH for
/// MediaRenderer:1 to 239.255.255.250:1900 on each network interface, then each answer's device description (XML)
/// for the name, model and the AVTransport / RenderingControl / ConnectionManager control URLs the target uses (CN-02).
/// </summary>
public sealed class SsdpBrowser(HttpClient http, ILogger logger)
{
    public const string MediaRenderer = "urn:schemas-upnp-org:device:MediaRenderer:1";

    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";

    /// <summary>The M-SEARCH request (MX 2: devices answer within 2 seconds).</summary>
    public static byte[] SearchRequest(string target) => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        $"ST: {target}\r\n" +
        "USER-AGENT: AudioTastic UPnP/1.1\r\n\r\n");

    /// <summary>The headers of an SSDP answer (names upper case), or null when it is not one.</summary>
    public static IReadOnlyDictionary<string, string>? ParseResponse(string text)
    {
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        return headers;
    }

    /// <summary>
    /// A device from its description XML at <paramref name="location"/>: friendlyName, modelName, UDN, and the control
    /// URLs (made absolute against URLBase or the description's address) as Details avTransport, renderingControl,
    /// connectionManager. Null when it is not a media renderer with AVTransport.
    /// </summary>
    public static CastDevice? ParseDescription(string xml, Uri location)
    {
        var root = XDocument.Parse(xml).Root;
        var device = root?.Element(Device + "device");
        if (device is null)
        {
            return null;
        }
        var baseUri = Uri.TryCreate(root!.Element(Device + "URLBase")?.Value, UriKind.Absolute, out var b) ? b : location;
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["location"] = location.ToString() };
        foreach (var service in device.Descendants(Device + "service"))
        {
            var type = service.Element(Device + "serviceType")?.Value ?? "";
            var control = service.Element(Device + "controlURL")?.Value;
            if (control is null)
            {
                continue;
            }
            var key = type.Contains(":AVTransport:", StringComparison.Ordinal) ? "avTransport"
                : type.Contains(":RenderingControl:", StringComparison.Ordinal) ? "renderingControl"
                : type.Contains(":ConnectionManager:", StringComparison.Ordinal) ? "connectionManager"
                : null;
            if (key is not null && !details.ContainsKey(key))
            {
                details[key] = new Uri(baseUri, control).ToString();
                details[key + "Type"] = type.Trim();
            }
        }
        if (!details.ContainsKey("avTransport"))
        {
            return null;
        }
        var udn = device.Element(Device + "UDN")?.Value ?? location.ToString();
        var name = device.Element(Device + "friendlyName")?.Value ?? location.Host;
        var model = string.Join(' ', new[] { device.Element(Device + "manufacturer")?.Value, device.Element(Device + "modelName")?.Value }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var address = IPAddress.TryParse(location.Host, out var ip) ? ip : IPAddress.None;
        // Whether a renderer shows video is known only from GetProtocolInfo (CN-02): null until then.
        return new CastDevice(udn, name, CastDeviceKind.Dlna, address, location.Port, model.Length > 0 ? model : null, null, details);
    }

    /// <summary>Media renderers answering within <paramref name="duration"/>, with their descriptions read.</summary>
    public async Task<IReadOnlyList<CastDevice>> FindRenderersAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var request = SearchRequest(MediaRenderer);
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = LocalInterfaces.MulticastIPv4().Select(async local =>
        {
            try
            {
                using var socket = new UdpClient(new IPEndPoint(local, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(duration);
                await socket.SendAsync(request, Group, timeout.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token).ConfigureAwait(false);
                await socket.SendAsync(request, Group, timeout.Token).ConfigureAwait(false);
                while (!timeout.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (ParseResponse(Encoding.UTF8.GetString(result.Buffer)) is { } headers && headers.TryGetValue("LOCATION", out var location))
                    {
                        lock (locations)
                        {
                            locations.Add(location);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException ex)
            {
                logger.LogDebug("SSDP on {Interface} failed: {Message}", local, ex.Message);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var devices = await Task.WhenAll(locations.Select(async location =>
        {
            try
            {
                if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var host) || !StreamServer.IsLocalNetwork(host))
                {
                    return null; // only devices on the local network
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return ParseDescription(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), uri);
            }
#pragma warning disable CA1031 // One device's broken description must not stop the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogDebug("Could not read the UPnP description at {Location}: {Message}", location, ex.Message);
                return null;
            }
        })).ConfigureAwait(false);
        return [.. devices.OfType<CastDevice>().GroupBy(d => d.Id).Select(g => g.First())];
    }
}
