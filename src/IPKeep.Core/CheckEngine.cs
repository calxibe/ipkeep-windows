using System.Diagnostics;
using System.Net;

namespace IPKeep.Core;

public sealed record HostResult(string Hostname, bool Success, bool DnsUpdated, string Message);
// AuthenticationRejected is last with a default so existing construction sites and saved
// status files keep working. It carries the one failure the schedule must not keep retrying.
public sealed record CheckResult(DateTimeOffset CheckedAt, bool Success, bool Skipped, string? IPv4, string? IPv6, string Message, HostResult[] Hosts, bool AuthenticationRejected = false);

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
            string summary = authFailed
                ? "IPKeep rejected the token. Updates are paused until you save a valid token."
                : !success ? "Some updates need attention. Retrying shortly."
                : results.Any(x => !x.DnsUpdated) ? "IP address recorded. DNS publishing is pending on IPKeep."
                : "Your hostnames are up to date.";
            log.Write(success ? "INFO" : "WARN", $"Check completed: success={success}; auth_rejected={authFailed}; elapsed_ms={watch.ElapsedMilliseconds}. {summary}");
            return new(DateTimeOffset.Now, success, skipped, ipv4, ipv6, summary, results.ToArray(), authFailed);
        }
        finally { gate.Release(); }
    }
}

public static class CheckSchedule
{
    public const int FirstRetryMinutes = 5;
    public const int MaximumRetryMinutes = 60;

    /// <summary>
    /// How long to wait before the next check, or null to stop scheduling entirely.
    /// </summary>
    /// <remarks>
    /// A rejected token is not transient: retrying cannot fix it, and a machine left running
    /// with a revoked token would otherwise send a rejected request every five minutes for as
    /// long as it stays on. That case parks until someone saves a token or asks to check now.
    /// Other failures back off 5, 10, 20, 40 minutes, capped at both an hour and the interval
    /// the user chose, so a short outage still recovers quickly and a long one stops hammering.
    /// </remarks>
    public static TimeSpan? Delay(bool success, int intervalMinutes, int consecutiveFailures = 1, bool authenticationRejected = false)
    {
        if (success) return TimeSpan.FromMinutes(intervalMinutes);
        if (authenticationRejected) return null;
        int steps = Math.Clamp(consecutiveFailures, 1, 10) - 1;
        int minutes = Math.Min(FirstRetryMinutes * (1 << steps), MaximumRetryMinutes);
        return TimeSpan.FromMinutes(Math.Min(minutes, Math.Max(1, intervalMinutes)));
    }
}
