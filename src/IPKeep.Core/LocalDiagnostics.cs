using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;

namespace IPKeep.Core;

public sealed class LocalDiagnostics(IPublicIpResolver resolver, bool includeNetworkChecks = true)
{
    public static IPAddress? ValidateLocalTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null; // This computer.
        if (!IPAddress.TryParse(value, out var ip) || ip.IsIPv4MappedToIPv6 || ip.ScopeIdOrZero() != 0)
            throw new SettingsException("Local device must be a private LAN IP address, without a URL or port.");
        var b = ip.GetAddressBytes();
        bool privateAddress = ip.AddressFamily == AddressFamily.InterNetwork ? b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 : (b[0] & 0xfe) == 0xfc;
        if (!privateAddress) throw new SettingsException("Use a private LAN address for another device. Leave it blank for this computer.");
        return ip;
    }
    public static (bool Listening, bool LoopbackOnly, int[] Families) InspectListeners(IEnumerable<IPEndPoint> endpoints, int port)
    {
        var matching = endpoints.Where(e => e.Port == port).ToArray();
        var network = matching.Where(e => !IPAddress.IsLoopback(e.Address)).ToArray();
        // An IPv6 socket is not assumed dual-stack; the public IPv4 test remains independent.
        return (matching.Length > 0, matching.Length > 0 && network.Length == 0,
            network.Select(e => e.AddressFamily == AddressFamily.InterNetwork ? 4 : 6).Distinct().ToArray());
    }
    public async Task<LocalDiagnosticEvidence> CollectAsync(string hostname, string providerId, DiagnosticService[] services, LocalDiagnosticOptions options,
        bool? updaterRunning, IProgress<string>? progress, CancellationToken ct)
    {
        var provider = IpLookupProviders.Get(providerId);
        ct.ThrowIfCancellationRequested();
        DiagnosticClient.ValidateServices(services);
        if (options.WanIPv4 is not null && RouterWanDiscovery.UsableIPv4(options.WanIPv4) is null)
            throw new SettingsException("Enter the router WAN IPv4 shown in its settings, or leave it blank.");
        foreach (var s in services)
        {
            var target = options.Targets.GetValueOrDefault(s.Port, new()); ValidateLocalTarget(target.Address);
            if (target.Port is < 0 or > 65535) throw new SettingsException("Local port must be 1–65535, or blank to use the public port.");
        }
        progress?.Report($"Checking public addresses with {provider.Name} and local listeners…");
        async Task<string?> Discover(bool v6)
        {
            if (v6 && !provider.SupportsIPv6) return null;
            try { return await resolver.ResolveAsync(v6, ct, provider.Id); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }
        var ip4 = Discover(false); var ip6 = Discover(true);
        var router = includeNetworkChecks ? new RouterWanDiscovery().DiscoverAsync(options.WanIPv4, ct) : null;
        var trace = includeNetworkChecks ? new LocalTraceroute().TraceAsync(provider.Id, ct) : null;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
        var addresses = interfaces.OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        var lan = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.GetAddressBytes()[0] != 169);
        var gateway = interfaces.SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        async Task<double?> GatewayPing() {
            if (gateway is null || !includeNetworkChecks) return null;
            try { using var ping = new Ping(); var response = await ping.SendPingAsync(gateway, TimeSpan.FromSeconds(2), cancellationToken: ct); return response.Status == IPStatus.Success ? response.RoundtripTime : null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return null; }
        }
        async Task<string[]> LocalDns() {
            if (!includeNetworkChecks) return [];
            try { return (await Dns.GetHostAddressesAsync(hostname, ct).WaitAsync(TimeSpan.FromSeconds(5), ct)).Where(a => a.ScopeIdOrZero() == 0).Take(20).Select(a => a.ToString()).ToArray(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return []; }
        }
        var gatewayPing = GatewayPing(); var dns = LocalDns();
        IPEndPoint[]? tcp = null, udp = null;
        try { var properties = IPGlobalProperties.GetIPGlobalProperties(); tcp = properties.GetActiveTcpListeners(); udp = properties.GetActiveUdpListeners(); } catch (NetworkInformationException) { }
        var observations = await Task.WhenAll(services.Select(async service =>
        {
            ct.ThrowIfCancellationRequested();
            var mapping = options.Targets.GetValueOrDefault(service.Port, new()); var remote = ValidateLocalTarget(mapping.Address);
            int port = mapping.Port == 0 ? service.Port : mapping.Port;
            bool isUdp = service.Type == "bedrock"; bool? listening = null; bool loopback = false; int[] families = [];
            IPAddress? destination = remote;
            if (remote is null)
            {
                var listeners = isUdp ? udp : tcp;
                if (listeners is not null)
                {
                    var state = InspectListeners(listeners, port); listening = state.Listening; loopback = state.LoopbackOnly; families = state.Families;
                    var bindings = listeners.Where(e => e.Port == port).Select(e => e.Address).ToArray();
                    destination = bindings.FirstOrDefault(a => a.Equals(IPAddress.Any)) is not null ? lan ?? IPAddress.Loopback :
                        bindings.FirstOrDefault(a => !a.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(a)) ??
                        (bindings.Contains(IPAddress.IPv6Any) ? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal) ?? IPAddress.IPv6Loopback : bindings.FirstOrDefault());
                }
            }
            double? elapsed = null; int? status = null; string web = "not-tested";
            if (!isUdp && destination is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var watch = Stopwatch.StartNew();
                try
                {
                    using var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(destination, port, timeout.Token); elapsed = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
                    if (remote is not null) { listening = true; families = [destination.AddressFamily == AddressFamily.InterNetwork ? 4 : 6]; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { if (remote is not null) listening = false; }
                if (elapsed is not null && service.Type is "http" or "https")
                {
                    (status, web) = await LocalWebAsync(destination, port, service.Type, service.WebsiteHostname ?? hostname, ct);
                }
            }
            // A remote UDP timeout cannot establish that the service is stopped.
            progress?.Report($"Local check completed: {service.Name}");
            return new LocalServiceObservation(service.Port, isUdp ? "udp" : "tcp", listening, families, remote is null ? "computer" : "lan",
                (destination?.IsIPv6LinkLocal == true ? null : destination?.ToString()), port, loopback, elapsed, status, web);
        }));
        var routerEvidence = router is null ? null : await router;
        string? wanAddress = routerEvidence is { Status: "detected" or "manual" } ? routerEvidence.Observations[0].WanIPv4 : routerEvidence is null ? options.WanIPv4 : null;
        return new("windows", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), await ip4, await ip6, wanAddress, lan?.ToString(), updaterRunning, observations, await dns, gateway?.ToString(), await gatewayPing, provider.Id, routerEvidence, trace is null ? null : await trace);
    }
    private static async Task<(int?, string)> LocalWebAsync(IPAddress address, int port, string scheme, string website, CancellationToken ct)
    {
        // The selected numeric local destination is pinned; SNI and Host use the website name.
        // No credentials, redirects, proxies, response bodies or certificate bypasses.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            } };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(scheme, website, port, "/").Uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return ((int)response.StatusCode, "response");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException) { return (null, "certificate-invalid"); }
        catch { return (null, "failed"); }
    }
}
