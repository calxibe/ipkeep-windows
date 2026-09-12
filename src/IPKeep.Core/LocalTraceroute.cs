using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IPKeep.Core;

public sealed record LocalTraceHop(int Ttl, string? Address, double? RttMs, string State);
public sealed record LocalTraceEvidence(string Status, string? TargetIPv4, LocalTraceHop[] Hops);
internal interface ILocalTraceTransport
{
    Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct);
    Task<LocalTraceHop> PingAsync(IPAddress target, int ttl, CancellationToken ct);
}
public sealed class LocalTraceroute
{
    private readonly ILocalTraceTransport transport;
    public LocalTraceroute() : this(new Transport()) { }
    internal LocalTraceroute(ILocalTraceTransport transport) => this.transport = transport;
    public async Task<LocalTraceEvidence> TraceAsync(string providerId, CancellationToken ct)
    {
        var provider = IpLookupProviders.Get(providerId);
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(7));
        var hops = new List<LocalTraceHop>(); IPAddress? target = null;
        try
        {
            using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); dnsDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            target = (await transport.ResolveAsync(provider.IPv4Endpoint.Host, dnsDeadline.Token)).FirstOrDefault(ip => PublicIpResolver.IsPublic(ip, false));
            if (target is null) return new("unavailable", null, []);
            for (int ttl = 1; ttl <= 12; ttl++)
            {
                var hop = await transport.PingAsync(target, ttl, deadline.Token);
                hops.Add(hop);
                if (hop.State == "reached" && hop.Address == target.ToString()) return new("complete", target.ToString(), hops.ToArray());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* Filtered, unsupported and incomplete routes never establish CGNAT. */ }
        ct.ThrowIfCancellationRequested();
        return new(hops.Count > 0 ? "partial" : "unavailable", target?.ToString(), hops.ToArray());
    }
    private sealed class Transport : ILocalTraceTransport
    {
        public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct) => Dns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, ct);
        public async Task<LocalTraceHop> PingAsync(IPAddress target, int ttl, CancellationToken ct)
        {
            using var ping = new Ping(); var timer = Stopwatch.StartNew();
            var response = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(400), new byte[16], new PingOptions(ttl, false), ct);
            string state = response.Status == IPStatus.Success ? "reached" : response.Status is IPStatus.TtlExpired or IPStatus.TimeExceeded ? "reply" :
                response.Status == IPStatus.TimedOut ? "timeout" : "unreachable";
            bool replied = state is "reached" or "reply";
            return new(ttl, response.Address?.Equals(IPAddress.Any) == false ? response.Address.ToString() : null,
                replied ? Math.Round(timer.Elapsed.TotalMilliseconds, 2) : null, state);
        }
    }
}
