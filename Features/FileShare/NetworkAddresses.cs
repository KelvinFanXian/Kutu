using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Kutu.Features.FileShare;

internal sealed record LocalAddress(string Name, IPAddress Ip, int Priority)
{
    public override string ToString() => $"{Ip}  ·  {Name}";
}

internal static class NetworkAddresses
{
    public static IReadOnlyList<LocalAddress> GetCurrent()
    {
        var result = new List<LocalAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            foreach (var entry in properties.UnicastAddresses)
            {
                var ip = entry.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) ||
                    ip.GetAddressBytes()[0] == 169)
                    continue;

                var bytes = ip.GetAddressBytes();
                var privateIp = bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
                var physical = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
                var priority = (privateIp ? 0 : 4) + (hasGateway ? 0 : 2) + (physical ? 0 : 1);
                result.Add(new LocalAddress(adapter.Name, ip, priority));
            }
        }

        return result.OrderBy(x => x.Priority).ThenBy(x => x.Name).ThenBy(x => x.Ip.ToString())
            .DistinctBy(x => x.Ip).ToArray();
    }
}
