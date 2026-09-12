using IPKeep.Core;
namespace IPKeep.Desktop;
internal class DiagnosticRuntime
{
    public virtual HttpClient CreateHttp() => NetworkClients.Create(262144);
    public virtual LocalDiagnosticOptions Load(string token, string hostname) => DiagnosticPreferences.Load(token, hostname);
    public virtual void Save(string token, string hostname, LocalDiagnosticOptions options) => DiagnosticPreferences.Save(token, hostname, options);
    public virtual async Task<LocalDiagnosticEvidence> CollectAsync(string hostname, string providerId, DiagnosticService[] services, LocalDiagnosticOptions options, bool? updater,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ipv4 = NetworkClients.CreateDiscovery(false, TimeSpan.FromSeconds(6));
        using var ipv6 = NetworkClients.CreateDiscovery(true, TimeSpan.FromSeconds(6));
        return await new LocalDiagnostics(new PublicIpResolver(ipv4, ipv6, new QuietLog())).CollectAsync(hostname, providerId, services, options, updater, progress, ct);
    }
    private sealed class QuietLog : IActivityLog { public void Write(string level, string message) { } }
}
