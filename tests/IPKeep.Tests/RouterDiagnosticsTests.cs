using IPKeep.Core;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

internal static class RouterDiagnosticsTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Router diagnostics assertion failed."); }
    private static readonly RouterGateway Gateway = new(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.1"));
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("NAT-PMP parser requires a complete successful address-only response", () =>
        {
            byte[] good = [0,128,0,0,0,0,0,1,100,64,1,2];
            Check(RouterWanProbe.ParseNatPmp(good) == "100.64.1.2");
            foreach (var index in new[] {0,1,2,3}) { var bad = (byte[])good.Clone(); bad[index]++; Check(RouterWanProbe.ParseNatPmp(bad) is null); }
            Check(RouterWanProbe.ParseNatPmp(good[..11]) is null && RouterWanProbe.ParseNatPmp([..good, 0]) is null);
            foreach (var ip in new[] { "0.0.0.0", "127.0.0.1", "169.254.169.254", "224.0.0.1", "255.255.255.255" })
            { IPAddress.Parse(ip).GetAddressBytes().CopyTo(good, 8); Check(RouterWanProbe.ParseNatPmp(good) is null); }
            return Task.CompletedTask;
        }));
        tests.Add(("SSDP and router XML cannot select a different host or use XML entities", () =>
        {
            var gateway = Gateway.Address; var location = new Uri("http://192.168.1.1:5000/root.xml");
            Check(RouterWanProbe.ParseLocation("HTTP/1.1 200 OK\r\nLOCATION: " + location + "\r\n", gateway) == location);
            foreach (var url in new[] { "http://evil.example/", "http://8.8.8.8/", "http://169.254.169.254/", "http://192.168.1.2/", "http://u:p@192.168.1.1/", "file:///etc/passwd", "http://192.168.1.1/#fragment", "http://192.168.1.1/\\evil", "//evil.example/a" })
                Check(RouterWanProbe.SafeUrl(url, location, gateway) is null);
            Check(RouterWanProbe.ParseLocation("HTTP/1.1 302 Found\r\nLOCATION: " + location, gateway) is null);
            Check(RouterWanProbe.ParseLocation("HTTP/1.1 200 OK\r\nLOCATION: " + location + "\r\nLocation: " + location, gateway) is null);
            try { RouterWanProbe.ParseXml("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///private'>]><root>&x;</root>"); throw new Exception("Expected DTD refusal"); } catch (System.Xml.XmlException) { }
            try { RouterWanProbe.ParseXml("<root>" + new string('a', 65537) + "</root>"); throw new Exception("Expected length refusal"); } catch (System.Xml.XmlException) { }
            var xml = RouterWanProbe.ParseXml($"<root><URLBase>http://evil.example/</URLBase><service><serviceType>{RouterWanProbe.Services[0]}</serviceType><controlURL>/control</controlURL></service></root>");
            Check(RouterWanProbe.ParseServices(xml, location, gateway).Length == 0);
            return Task.CompletedTask;
        }));
        tests.Add(("Automatic WAN keeps both sources and never substitutes a conflicting manual value", async () =>
        {
            var result = await new RouterWanDiscovery(new Probe("100.64.1.2", ["100.64.1.2"]))
                .DiscoverAsync(new([Gateway], false, false), "8.8.8.8", default);
            Check(result.Status == "detected" && result.Observations.Length == 2 && result.Observations.All(o => o.WanIPv4 == "100.64.1.2"));
        }));
        tests.Add(("Conflicting WAN replies and multiple connections retain uncertainty", async () =>
        {
            var result = await new RouterWanDiscovery(new Probe("8.8.8.8", ["1.1.1.1"]))
                .DiscoverAsync(new([Gateway], true, true), null, default);
            Check(result.Status == "conflict" && result.VpnDetected && result.MultipleGateways && result.Observations.Length == 2);
            Check(RouterWanDiscovery.IsTunnel(NetworkInterfaceType.Ethernet, "WireGuard tunnel") && RouterWanDiscovery.IsTunnel(NetworkInterfaceType.Ppp, "WAN") &&
                !RouterWanDiscovery.IsTunnel(NetworkInterfaceType.Ethernet, "Intel Ethernet"));
        }));
        tests.Add(("Unsupported routers preserve explicit manual fallback or unavailable state", async () =>
        {
            var discovery = new RouterWanDiscovery(new Probe(null, []));
            var missing = await discovery.DiscoverAsync(new([Gateway], false, false), null, default);
            Check(missing.Status == "unavailable" && missing.Observations.Length == 0);
            var manual = await discovery.DiscoverAsync(new([Gateway], false, false), "192.168.0.2", default);
            Check(manual.Status == "manual" && manual.Observations[0].Method == "manual" && manual.Observations[0].GatewayIPv4 is null);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await discovery.DiscoverAsync(new([Gateway], false, false), null, cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }));
        tests.Add(("Router timeouts do not discard a successful independent NAT-PMP observation", async () =>
        {
            var probe = new Probe("8.8.8.8", [], upnpFails:true);
            var result = await new RouterWanDiscovery(probe).DiscoverAsync(new([Gateway], false, false), null, default);
            Check(result.Status == "detected" && result.Observations.Single().Method == "nat-pmp");
        }));
        tests.Add(("UPnP loopback fixture receives only unauthenticated description and GetExternalIPAddress requests", async () =>
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var location = new Uri($"http://127.0.0.1:{port}/root.xml"); var service = RouterWanProbe.Services[0];
            var received = new List<string>();
            var server = Task.Run(async () =>
            {
                foreach (int step in new[] { 0, 1 })
                {
                    using var connection = await tcp.AcceptTcpClientAsync(deadline.Token); await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen:true); var lines = new List<string>(); string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) lines.Add(line);
                    int length = int.Parse(lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?.Split(':')[1].Trim() ?? "0");
                    var body = new char[length]; if (length > 0) Check(await reader.ReadBlockAsync(body.AsMemory(), deadline.Token) == length);
                    received.Add(string.Join("\n", lines) + "\n" + new string(body));
                    string xml = step == 0 ? $"<root><service><serviceType>{service}</serviceType><controlURL>/control</controlURL></service></root>" :
                        $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"{service}\"><NewExternalIPAddress>100.64.1.2</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
                    byte[] bytes = Encoding.UTF8.GetBytes(xml); await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), deadline.Token); await stream.WriteAsync(bytes, deadline.Token);
                }
            });
            var probe = new RouterWanProbe((_, _) => Task.FromResult(new[] { location }));
            var result = await probe.UpnpAsync(new(IPAddress.Loopback, IPAddress.Loopback), deadline.Token); await server;
            Check(result.SequenceEqual(new[] { "100.64.1.2" }) && received[0].StartsWith("GET /root.xml") && received[1].StartsWith("POST /control"));
            Check(received[1].Contains("#GetExternalIPAddress") && received.All(s => !s.Contains("Authorization:") && !s.Contains("Cookie:") && !s.Contains("AddPortMapping") && !s.Contains("DeletePortMapping")));
        }));
        tests.Add(("Local traceroute uses selected provider, bounds hops and preserves timeouts", async () =>
        {
            var transport = new TraceTransport(); var result = await new LocalTraceroute(transport).TraceAsync("amazon", default);
            Check(transport.Host == "checkip.amazonaws.com" && result.Status == "partial" && result.Hops.Length == 12 && result.Hops[0].RttMs is null);
            Check(transport.Ttls.SequenceEqual(Enumerable.Range(1, 12)));
            transport = new TraceTransport(reach:true); result = await new LocalTraceroute(transport).TraceAsync("ipify", default);
            Check(result.Status == "complete" && result.Hops.Length == 2 && transport.Host == "api.ipify.org");
        }));
        tests.Add(("Traceroute refuses nonpublic lookup targets and respects cancellation", async () =>
        {
            var transport = new TraceTransport(privateTarget:true); var result = await new LocalTraceroute(transport).TraceAsync("amazon", default);
            Check(result.Status == "unavailable" && result.TargetIPv4 is null && transport.Ttls.Count == 0);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await new LocalTraceroute(transport).TraceAsync("amazon", cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }));
    }
    private sealed class Probe(string? nat, string[] upnp, bool upnpFails = false) : IRouterWanProbe
    {
        public Task<string?> NatPmpAsync(RouterGateway gateway, CancellationToken ct) => Task.FromResult(nat);
        public Task<string[]> UpnpAsync(RouterGateway gateway, CancellationToken ct) => upnpFails ? Task.FromException<string[]>(new TimeoutException()) : Task.FromResult(upnp);
    }
    private sealed class TraceTransport(bool reach = false, bool privateTarget = false) : ILocalTraceTransport
    {
        public string? Host; public List<int> Ttls = [];
        public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct) { Host = hostname; return Task.FromResult(new[] { IPAddress.Parse(privateTarget ? "192.168.1.1" : "8.8.8.8") }); }
        public Task<LocalTraceHop> PingAsync(IPAddress target, int ttl, CancellationToken ct)
        { Ttls.Add(ttl); return Task.FromResult(reach && ttl == 2 ? new LocalTraceHop(ttl, target.ToString(), 15, "reached") : new(ttl, null, null, "timeout")); }
    }
}
