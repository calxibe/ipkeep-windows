using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPKeep.Core;

// Read-only discovery: NAT-PMP opcode 0 and UPnP GetExternalIPAddress only.
// Never creates a mapping, subscribes to events, authenticates or changes the router.
internal sealed class RouterWanProbe(Func<RouterGateway,CancellationToken,Task<Uri[]>>? discover = null) : IRouterWanProbe
{
    internal static readonly string[] Services = ["urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1"];
    public async Task<string?> NatPmpAsync(RouterGateway gateway, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(gateway.LocalAddress, 0));
        udp.Connect(gateway.Address, 5351); // Connected UDP rejects replies from any other endpoint.
        foreach (var milliseconds in new[] { 250, 500, 1000 })
        {
            await udp.SendAsync(new byte[] { 0, 0 }, ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(milliseconds);
            try { return ParseNatPmp((await udp.ReceiveAsync(wait.Token)).Buffer); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }
    internal static string? ParseNatPmp(byte[] packet) => packet.Length == 12 && packet[0] == 0 && packet[1] == 128 && packet[2] == 0 && packet[3] == 0
        ? RouterWanDiscovery.UsableIPv4(new IPAddress(packet.AsSpan(8, 4)).ToString()) : null;

    public async Task<string[]> UpnpAsync(RouterGateway gateway, CancellationToken ct)
    {
        var locations = await (discover ?? DiscoverLocationsAsync)(gateway, ct);
        var values = new List<string>();
        using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false, MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
            {
                // Numeric gateway pinned again at connect time; SSDP/XML cannot redirect a request.
                if (context.DnsEndPoint.Host != gateway.Address.ToString()) throw new HttpRequestException("Invalid router endpoint.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(gateway.LocalAddress, 0)); await socket.ConnectAsync(gateway.Address, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            } };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        foreach (var location in locations.Take(2))
        {
            try
            {
                using var description = new HttpRequestMessage(HttpMethod.Get, location);
                var xml = await ReadXmlAsync(http, description, ct);
                foreach (var service in ParseServices(xml, location, gateway.Address).Take(2))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, service.Url);
                    request.Headers.Add("SOAPAction", $"\"{service.Type}#GetExternalIPAddress\"");
                    request.Content = new StringContent($"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:GetExternalIPAddress xmlns:u=\"{service.Type}\"/></s:Body></s:Envelope>", Encoding.UTF8, "text/xml");
                    var reply = await ReadXmlAsync(http, request, ct);
                    var ip = ParseExternalAddress(reply, service.Type);
                    if (ip is not null) values.Add(ip);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Unsupported, denied or malformed router replies remain unavailable. */ }
        }
        return values.Distinct().Take(2).ToArray();
    }
    private static async Task<Uri[]> DiscoverLocationsAsync(RouterGateway gateway, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(gateway.LocalAddress, 0));
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, gateway.LocalAddress.GetAddressBytes());
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        foreach (var version in new[] { 1, 2 })
        {
            var message = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:{version}\r\n\r\n");
            await udp.SendAsync(message, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var locations = new HashSet<Uri>();
        for (int i = 0; i < 16 && locations.Count < 2; i++)
        {
            try
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                if (!reply.RemoteEndPoint.Address.Equals(gateway.Address) || reply.Buffer.Length > 8192) continue;
                var uri = ParseLocation(Encoding.ASCII.GetString(reply.Buffer), gateway.Address);
                if (uri is not null) locations.Add(uri);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        return locations.ToArray();
    }
    internal static Uri? ParseLocation(string response, IPAddress gateway)
    {
        var lines = response.Split("\r\n");
        if (lines.Length < 2 || !lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.OrdinalIgnoreCase)) return null;
        var locations = lines.Skip(1).Where(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)).ToArray();
        return locations.Length == 1 ? SafeUrl(locations[0][9..].Trim(), null, gateway) : null;
    }
    internal static Uri? SafeUrl(string value, Uri? baseUri, IPAddress gateway)
    {
        if (value.Length is 0 or > 2048 || value.Any(char.IsControl) || value.Contains('\\')) return null;
        Uri? uri;
        bool valid = baseUri is null ? Uri.TryCreate(value, UriKind.Absolute, out uri) : Uri.TryCreate(baseUri, value, out uri);
        return valid && uri is not null && uri.Scheme == "http" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Port > 0 &&
            uri.Host == gateway.ToString() ? uri : null;
    }
    internal static XDocument ParseXml(string value)
    {
        using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        return XDocument.Load(reader);
    }
    internal static (string Type, Uri Url)[] ParseServices(XDocument xml, Uri location, IPAddress gateway)
    {
        var baseText = xml.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "URLBase")?.Value;
        var baseUri = baseText is null ? location : SafeUrl(baseText.Trim(), null, gateway);
        if (baseUri is null) return [];
        return xml.Descendants().Where(e => e.Name.LocalName == "service").Take(32).Select(e =>
        {
            string type = e.Elements().FirstOrDefault(x => x.Name.LocalName == "serviceType")?.Value.Trim() ?? "";
            string url = e.Elements().FirstOrDefault(x => x.Name.LocalName == "controlURL")?.Value.Trim() ?? "";
            return (Type: type, Url: SafeUrl(url, baseUri, gateway));
        }).Where(s => Services.Contains(s.Type) && s.Url is not null).Select(s => (s.Type, s.Url!)).Take(2).ToArray();
    }
    internal static string? ParseExternalAddress(XDocument xml, string service)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var replies = xml.Root?.Name == soap + "Envelope" ? xml.Root.Element(soap + "Body")?.Elements(XName.Get("GetExternalIPAddressResponse", service)).ToArray() : null;
        var values = replies?.Length == 1 ? replies[0].Elements().Where(x => x.Name.LocalName == "NewExternalIPAddress").ToArray() : null;
        return values?.Length == 1 ? RouterWanDiscovery.UsableIPv4(values[0].Value.Trim()) : null;
    }
    private static async Task<XDocument> ReadXmlAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new HttpRequestException("Router response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        { if (bytes.Length + count > 65536) throw new HttpRequestException("Router response too large."); bytes.Write(buffer, 0, count); }
        return ParseXml(Encoding.UTF8.GetString(bytes.ToArray()));
    }
}
