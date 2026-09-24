using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Susurro.Core.Net;

public sealed record LocalInterface(IPAddress Address, IPAddress Mask, bool HasGateway, string Name)
{
    public IPAddress Broadcast
    {
        get
        {
            var a = Address.GetAddressBytes();
            var m = Mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            return new IPAddress(b);
        }
    }
}

public static class NetworkInfo
{
    /// <summary>Interfaces IPv4 activas (sin loopback ni túneles), primero las que tienen puerta de enlace.</summary>
    public static IReadOnlyList<LocalInterface> GetInterfaces()
    {
        var list = new List<LocalInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA: sin DHCP
                    list.Add(new LocalInterface(ua.Address, ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), hasGateway, nic.Name));
                }
            }
        }
        catch
        {
            // Sin información de red: se devuelve lo que haya.
        }
        return list.OrderByDescending(i => i.HasGateway).ToList();
    }

    public static string DescribeLocalAddresses()
    {
        var ifs = GetInterfaces();
        return ifs.Count == 0 ? "sin red" : string.Join(", ", ifs.Select(i => i.Address.ToString()).Distinct());
    }

    /// <summary>
    /// Interpreta "192.168.1.20", "192.168.1.20:47810" o "OFICINA" (nombre de equipo).
    /// </summary>
    public static bool TryParseHostPort(string? input, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon)
        {
            if (!int.TryParse(s[(colon + 1)..], out var p) || p is < 1 or > 65535) return false;
            port = p;
            s = s[..colon];
        }
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return false;
        host = s;
        return true;
    }

    public static async Task<IPAddress[]> ResolveIPv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetwork ? new[] { ip } : Array.Empty<IPAddress>();
        try
        {
            var all = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            return all;
        }
        catch (SocketException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
