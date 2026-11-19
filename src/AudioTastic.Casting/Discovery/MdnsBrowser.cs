using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Discovery;

/// <summary>
/// CN-05: finds Chromecasts (and, later, AirPlay receivers) with mDNS service discovery. A PTR query for the service
/// goes to 224.0.0.251:5353 from an ordinary socket on each network interface; devices answer that socket directly
/// (RFC 6762 §6.7, "legacy unicast"), so port 5353 (often held by the OS) is never needed.
/// </summary>
public sealed class MdnsBrowser(ILogger logger)
{
    public const string GoogleCast = "_googlecast._tcp.local";
    public const string AirPlayAudio = "_raop._tcp.local";

    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>All records answered for <paramref name="service"/> within <paramref name="duration"/>, with the answering address.</summary>
    public async Task<IReadOnlyList<(IPAddress From, IReadOnlyList<DnsRecord> Records)>> QueryAsync(string service, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var query = DnsMessage.PtrQuery(service);
        var answers = new List<(IPAddress, IReadOnlyList<DnsRecord>)>();
        var tasks = LocalInterfaces.MulticastIPv4().Select(async local =>
        {
            try
            {
                using var socket = new UdpClient(new IPEndPoint(local, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(duration);
                await socket.SendAsync(query, Group, timeout.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
                await socket.SendAsync(query, Group, timeout.Token).ConfigureAwait(false); // UDP may drop one
                while (!timeout.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    var records = DnsMessage.Parse(result.Buffer);
                    if (records.Count > 0)
                    {
                        lock (answers)
                        {
                            answers.Add((result.RemoteEndPoint.Address, records));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException ex)
            {
                logger.LogDebug("mDNS on {Interface} failed: {Message}", local, ex.Message);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return answers;
    }

    /// <summary>Chromecasts (and Cast-enabled TVs and speakers) answering within <paramref name="duration"/>.</summary>
    public async Task<IReadOnlyList<CastDevice>> FindChromecastsAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        ToChromecasts(await QueryAsync(GoogleCast, duration, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Builds devices from mDNS answers: PTR → instance, SRV → host and port, TXT → name ("fn"), model ("md"), id
    /// ("id") and capabilities ("ca"; bit 0 = video out, so the device has a screen), A → address (else the sender).
    /// </summary>
    public static IReadOnlyList<CastDevice> ToChromecasts(IReadOnlyList<(IPAddress From, IReadOnlyList<DnsRecord> Records)> answers)
    {
        var all = answers.SelectMany(a => a.Records.Select(r => (a.From, Record: r))).ToList();
        var addresses = all.Where(x => x.Record.Address is not null && x.Record.Type == DnsMessage.TypeA)
            .GroupBy(x => x.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Record.Address!, StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, CastDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in all.Where(x => x.Record.Type == DnsMessage.TypePtr && x.Record.PointsTo is not null)
                     .Select(x => x.Record.PointsTo!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var srv = all.FirstOrDefault(x => x.Record.Type == DnsMessage.TypeSrv && x.Record.Name.Equals(instance, StringComparison.OrdinalIgnoreCase));
            var txt = all.FirstOrDefault(x => x.Record.Type == DnsMessage.TypeTxt && x.Record.Name.Equals(instance, StringComparison.OrdinalIgnoreCase))
                .Record?.Text ?? new Dictionary<string, string>();
            var from = srv.Record is not null ? srv.From : all.First(x => x.Record.PointsTo == instance).From;
            var address = srv.Record?.Target is { } target && addresses.TryGetValue(target, out var a) ? a : from;
            var id = txt.GetValueOrDefault("id") ?? instance;
            var name = txt.GetValueOrDefault("fn") ?? instance.Split('.')[0];
            bool? screen = int.TryParse(txt.GetValueOrDefault("ca"), out var capabilities) ? (capabilities & 1) != 0 : null;
            devices[id] = new CastDevice(id, name, CastDeviceKind.Chromecast, address, srv.Record?.Port ?? 8009,
                txt.GetValueOrDefault("md"), screen, txt);
        }
        return [.. devices.Values];
    }
}
