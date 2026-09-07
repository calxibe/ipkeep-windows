using System.Diagnostics;
using System.Net;

namespace IPKeep.Core;

public sealed record HostResult(string Hostname, bool Success, bool DnsUpdated, string Message);
public sealed record CheckResult(DateTimeOffset CheckedAt, bool Success, bool Skipped, string? IPv4, string? IPv6, string Message, HostResult[] Hosts);

public sealed class CheckEngine(IPublicIpResolver resolver, IUpdateClient client, IActivityLog log)
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<CheckResult> RunAsync(ClientSettings settings, string token, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        var watch = Stopwatch.StartNew();
        try
        {
            settings = settings.Validate();
            token = ClientSettings.ValidateToken(token);
            log.Write("INFO", $"Checking public addresses with {IpLookupProviders.Get(settings.IpLookupProviderId).Name} and updating IPKeep hostnames.");
            string? ipv4 = null, ipv6 = null;
            bool detectionFailed = false;
            try { ipv4 = await resolver.ResolveAsync(false, cancellationToken, settings.IpLookupProviderId); }
            catch (UpdateException ex) { detectionFailed = true; log.Write("WARN", ex.Message); }
            if (settings.EnableIPv6)
            {
                try { ipv6 = await resolver.ResolveAsync(true, cancellationToken, settings.IpLookupProviderId); }
                catch (UpdateException ex) { detectionFailed = true; log.Write("WARN", ex.Message); }
            }
            var ignored = settings.IgnoredNetworks.Select(IpNetwork.Parse).ToArray();
            bool skipped = false;
            if (ipv4 is not null && ignored.Any(x => x.Contains(IPAddress.Parse(ipv4)))) { ipv4 = null; skipped = true; }
            if (ipv6 is not null && ignored.Any(x => x.Contains(IPAddress.Parse(ipv6)))) { ipv6 = null; skipped = true; }
            if (ipv4 is null && ipv6 is null)
            {
                string message = skipped && !detectionFailed ? "Public address is on your ignore list. No update sent." : "No eligible public address is available. Retrying in 5 minutes.";
                log.Write(skipped && !detectionFailed ? "INFO" : "ERROR", message);
                return new(DateTimeOffset.Now, skipped && !detectionFailed, skipped, null, null, message, []);
            }
            var results = new List<HostResult>();
            bool authFailed = false;
            foreach (var hostname in settings.Hostnames)
            {
                if (authFailed) { results.Add(new(hostname, false, false, "Update skipped because the token was rejected.")); continue; }
                try
                {
                    var reply = await client.UpdateAsync(token, hostname, ipv4, ipv6, cancellationToken);
                    string message = reply.DnsUpdated ? (reply.Changed ? "IP address and DNS updated." : "IP address confirmed; DNS published.") : "IP address recorded. DNS publishing is pending on IPKeep.";
                    results.Add(new(hostname, true, reply.DnsUpdated, message));
                    log.Write(reply.DnsUpdated ? "INFO" : "WARN", $"{hostname}: {message}");
                }
                catch (UpdateException ex)
                {
                    authFailed = ex.AuthenticationFailure;
                    results.Add(new(hostname, false, false, ex.Message));
                    log.Write("ERROR", $"{hostname}: {ex.Message}");
                }
            }
            bool success = !detectionFailed && results.All(x => x.Success);
            string summary = !success ? "Some updates need attention. Retrying in 5 minutes." : results.Any(x => !x.DnsUpdated) ? "IP address recorded. DNS publishing is pending on IPKeep." : "Your hostnames are up to date.";
            log.Write(success ? "INFO" : "WARN", $"Check completed: success={success}; elapsed_ms={watch.ElapsedMilliseconds}. {summary}");
            return new(DateTimeOffset.Now, success, skipped, ipv4, ipv6, summary, results.ToArray());
        }
        finally { gate.Release(); }
    }
}

public static class CheckSchedule
{
    public static TimeSpan Delay(bool success, int intervalMinutes) => TimeSpan.FromMinutes(success ? intervalMinutes : 5);
}
