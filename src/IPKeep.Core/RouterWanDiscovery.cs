using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IPKeep.Core;

public sealed record RouterWanObservation(string? GatewayIPv4, string WanIPv4, string Method);
public sealed record RouterWanEvidence(string Status, bool VpnDetected, bool MultipleGateways, RouterWanObservation[] Observations);
internal sealed record RouterGateway(IPAddress LocalAddress, IPAddress Address);
internal sealed record RouterNetwork(RouterGateway[] Gateways, bool VpnDetected, bool MultipleGateways);
internal interface IRouterWanProbe
{
    Task<string?> NatPmpAsync(RouterGateway gateway, CancellationToken ct);
    Task<string[]> UpnpAsync(RouterGateway gateway, CancellationToken ct);
}

public sealed class RouterWanDiscovery
{
    private readonly IRouterWanProbe probe;
    public RouterWanDiscovery() : this(new RouterWanProbe()) { }
    internal RouterWanDiscovery(IRouterWanProbe probe) => this.probe = probe;

    public Task<RouterWanEvidence> DiscoverAsync(string? manual, CancellationToken ct)
    {
        RouterNetwork network;
        try { network = CaptureNetwork(); }
        catch (NetworkInformationException) { network = new([], false, false); }
        return DiscoverAsync(network, manual, ct);
    }

    internal async Task<RouterWanEvidence> DiscoverAsync(RouterNetwork network, string? manual, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (manual is not null && UsableIPv4(manual) is null)
            throw new SettingsException("Enter a current router WAN IPv4, or leave it blank for automatic detection.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var rows = await Task.WhenAll(network.Gateways.Distinct().Take(4).Select(async gateway =>
        {
            async Task<RouterWanObservation[]> Query(bool upnp)
            {
                try
                {
                    string?[] addresses;
                    if (upnp) addresses = await probe.UpnpAsync(gateway, deadline.Token);
                    else addresses = new string?[] { await probe.NatPmpAsync(gateway, deadline.Token) };
                    return addresses.Select(UsableIPv4).OfType<string>().Distinct().Take(2)
                        .Select(ip => new RouterWanObservation(gateway.Address.ToString(), ip, upnp ? "upnp" : "nat-pmp")).ToArray();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return []; }
            }
            return (await Task.WhenAll(Query(false), Query(true))).SelectMany(x => x).ToArray();
        }));
        ct.ThrowIfCancellationRequested();
        var observations = rows.SelectMany(x => x).Take(12).ToArray();
        // Automatic measurements are never replaced with a saved/manual value.
        if (observations.Length == 0 && manual is not null)
            observations = [new(null, UsableIPv4(manual)!, "manual")];
        var distinct = observations.Select(o => o.WanIPv4).Distinct().Count();
        return new(distinct > 1 ? "conflict" : distinct == 0 ? "unavailable" : observations[0].Method == "manual" ? "manual" : "detected",
            network.VpnDetected, network.MultipleGateways, observations);
    }

    internal static RouterNetwork CaptureNetwork()
    {
        var gateways = new List<RouterGateway>(); bool vpn = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var info = nic.GetIPProperties();
            if (!info.UnicastAddresses.Any()) continue;
            vpn |= IsTunnel(nic.NetworkInterfaceType, nic.Name + " " + nic.Description);
            foreach (var gateway in info.GatewayAddresses.Select(g => g.Address).Where(g => UsableIPv4(g.ToString()) is not null))
            {
                // Only the OS-configured gateway on this interface's subnet is contacted.
                var local = info.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    SameSubnet(a.Address, gateway, a.IPv4Mask));
                if (local is not null) gateways.Add(new(local.Address, gateway));
            }
        }
        var distinct = gateways.Distinct().ToArray();
        return new(distinct, vpn, distinct.Length > 1);
    }
    internal static bool IsTunnel(NetworkInterfaceType type, string description) => type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
        new[] { "wireguard", "wintun", "openvpn", "tailscale", "zerotier", "tap-windows", "vpn", "tunnel" }.Any(s => description.Contains(s, StringComparison.OrdinalIgnoreCase));
    private static bool SameSubnet(IPAddress local, IPAddress gateway, IPAddress mask) =>
        local.GetAddressBytes().Zip(gateway.GetAddressBytes(), (a, b) => a ^ b).Zip(mask.GetAddressBytes(), (v, m) => v & m).All(v => v == 0);

    internal static string? UsableIPv4(string? value)
    {
        if (value is null || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value) return null;
        var b = ip.GetAddressBytes();
        return b[0] == 0 || b[0] == 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || value == "255.255.255.255" ? null : value;
    }
}
