using System.Net;

namespace AudioTastic.Casting.Discovery;

/// <summary>CN-05: the device families AudioTastic casts to.</summary>
public enum CastDeviceKind
{
    Chromecast,
    Dlna,
    AirPlay,
}

/// <summary>
/// CN-05: a cast device found on the local network.
/// </summary>
/// <param name="Id">Stable id (Chromecast "id", UPnP UDN, AirPlay name): the same device keeps it across scans.</param>
/// <param name="Name">The name the user gave the device (e.g. "Living room TV").</param>
/// <param name="Port">Chromecast: the Cast port (8009); DLNA: the description's HTTP port.</param>
/// <param name="HasScreen">True when the device can show video (CN-09); null when not known yet.</param>
/// <param name="Details">Protocol data the targets need (DLNA control URLs, Chromecast TXT values).</param>
public sealed record CastDevice(string Id, string Name, CastDeviceKind Kind, IPAddress Address, int Port, string? Model, bool? HasScreen,
    IReadOnlyDictionary<string, string> Details);
