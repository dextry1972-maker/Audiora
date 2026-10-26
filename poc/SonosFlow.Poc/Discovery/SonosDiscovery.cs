using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SonosFlow.Poc.Discovery;

public sealed record SonosDevice(IPAddress Address, string RoomName, string ModelName, string ModelNumber, string Udn)
{
    public override string ToString() => $"{RoomName} ({ModelName}) — {Address}";
}

/// <summary>Finds Sonos players on the LAN with an SSDP M-SEARCH for ZonePlayer devices.</summary>
public static class SonosDiscovery
{
    private const string SearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    public static async Task<IReadOnlyList<SonosDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 1\r\n" +
            $"ST: {SearchTarget}\r\n\r\n");

        // Send from every active IPv4 interface so VPN/virtual adapters don't swallow the search.
        var listeners = LocalIPv4Addresses().Select(ip => ListenAsync(ip, request, timeout, locations, ct)).ToList();
        await Task.WhenAll(listeners);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var devices = await Task.WhenAll(locations.Select(loc => DescribeAsync(http, loc, ct)));
        return devices.OfType<SonosDevice>()
            .GroupBy(d => d.Udn)
            .Select(g => g.First())
            .OrderBy(d => d.RoomName)
            .ToList();
    }

    private static async Task ListenAsync(IPAddress localIp, byte[] request, TimeSpan timeout, HashSet<string> locations, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(localIp, 0));
        try
        {
            await udp.SendAsync(request, MulticastEndpoint, ct);
            await Task.Delay(100, ct);
            await udp.SendAsync(request, MulticastEndpoint, ct);
        }
        catch (SocketException)
        {
            return; // Interface can't reach multicast; skip it.
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.ASCII.GetString(result.Buffer);
                var location = ReadHeader(text, "LOCATION");
                if (location is not null && text.Contains(SearchTarget, StringComparison.OrdinalIgnoreCase))
                {
                    lock (locations) locations.Add(location);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private static async Task<SonosDevice?> DescribeAsync(HttpClient http, string location, CancellationToken ct)
    {
        try
        {
            var xml = XDocument.Parse(await http.GetStringAsync(location, ct));
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var device = xml.Root?.Element(ns + "device");
            if (device is null) return null;
            string Get(string name) => device.Element(ns + name)?.Value ?? "";
            return new SonosDevice(
                IPAddress.Parse(new Uri(location).Host),
                Get("roomName"),
                Get("modelName"),
                Get("modelNumber"),
                Get("UDN"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }

    private static string? ReadHeader(string response, string header)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && line[..idx].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return null;
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    /// <summary>The local address the OS would use to reach <paramref name="remote"/> — the one Sonos can call back.</summary>
    public static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(remote, 1400);
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }
}
