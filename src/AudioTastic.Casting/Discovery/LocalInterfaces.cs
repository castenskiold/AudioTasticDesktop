using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AudioTastic.Casting.Discovery;

/// <summary>CN-05: the IPv4 addresses of this computer's active network interfaces that can send multicast.</summary>
public static class LocalInterfaces
{
    public static IReadOnlyList<IPAddress> MulticastIPv4()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast)
            {
                continue;
            }
            addresses.AddRange(nic.GetIPProperties().UnicastAddresses.Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && StreamServer.IsLocalNetwork(a)));
        }
        return addresses;
    }
}
