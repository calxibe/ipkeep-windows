using IPKeep.Core;
using System.Text.Json;

internal static class DiagnosticSmoke
{
    public static async Task RunRouterAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = new RouterWanDiscovery().DiscoverAsync(null, timeout.Token);
        var trace = new LocalTraceroute().TraceAsync("amazon", timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { routerWan = await router, localTrace = await trace }, DiagnosticClient.JsonOptions));
    }
    // Explicit operator-only acceptance check. Never runs in the ordinary test suite.
    public static async Task RunAsync()
    {
        string token = Environment.GetEnvironmentVariable("IPKEEP_DIAGNOSTIC_TEST_TOKEN") ?? throw new Exception("A disposable token is required.");
        string hostname = Environment.GetEnvironmentVariable("IPKEEP_DIAGNOSTIC_TEST_HOST") ?? throw new Exception("An owned test hostname is required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95)); var ct = timeout.Token;
        using var http = NetworkClients.Create(262144); var client = new DiagnosticClient(http);
        var settings = await client.SettingsAsync(token, hostname, ct);
        var dns = await client.DnsAsync(token, hostname, ct);
        using var ipv4 = NetworkClients.CreateDiscovery(false, TimeSpan.FromSeconds(6));
        using var ipv6 = NetworkClients.CreateDiscovery(true, TimeSpan.FromSeconds(6));
        var evidence = await new LocalDiagnostics(new PublicIpResolver(ipv4, ipv6, new QuietLog())).CollectAsync(hostname, Environment.GetEnvironmentVariable("IPKEEP_DIAGNOSTIC_TEST_PROVIDER") ?? IpLookupProviders.DefaultId, settings.Services, new(new()), null, null, ct);
        var provider = IpLookupProviders.Get(evidence.IpLookupProviderId!);
        if (!provider.SupportsIPv6 && evidence.PublicIPv6 is not null) throw new Exception("IPv4-only provider returned IPv6.");
        if (evidence.PublicIPv4 is null) throw new Exception("Selected lookup provider did not return IPv4.");
        var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), true, true, evidence);
        var job = await client.StartAsync(token, hostname, request, ct); string id = job.GetProperty("id").GetString()!;
        Console.WriteLine(JsonSerializer.Serialize(new { created = id, localServices = evidence.Services.Length, publicDnsRows = dns.GetProperty("results").GetArrayLength() }));
        var retry = await client.StartAsync(token, hostname, request, ct);
        if (retry.GetProperty("id").GetString() != id) throw new Exception("Retry duplicated a job.");
        while (job.GetProperty("state").GetString() == "pending") { await Task.Delay(3000, ct); job = await client.ReadAsync(token, id, ct); }
        var runs = job.GetProperty("runs").EnumerateArray().ToArray();
        if (runs.Length != 2 || runs.Any(r => r.GetProperty("state").GetString() != "complete")) throw new Exception("Both regions must complete.");
        var history = await client.HistoryAsync(token, hostname, ct);
        if (!history.GetProperty("jobs").EnumerateArray().Any(r => r.GetProperty("ID").GetString() == id)) throw new Exception("History missing job.");
        var storedEvidence = job.GetProperty("localEvidence");
        if (storedEvidence.GetProperty("routerWan").Deserialize<RouterWanEvidence>(DiagnosticClient.JsonOptions) is not { } storedRouter ||
            JsonSerializer.Serialize(storedRouter, DiagnosticClient.JsonOptions) != JsonSerializer.Serialize(evidence.RouterWan, DiagnosticClient.JsonOptions) ||
            storedEvidence.GetProperty("localTrace").GetProperty("hops").GetArrayLength() != evidence.LocalTrace!.Hops.Length)
            throw new Exception("Router/trace evidence was not preserved.");
        var cgnat = job.GetProperty("analysis").GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "cgnat");
        Console.WriteLine(JsonSerializer.Serialize(new { routerStatus = evidence.RouterWan!.Status, routerSources = evidence.RouterWan.Observations.Select(o => o.Method), evidence.RouterWan.VpnDetected,
            evidence.RouterWan.MultipleGateways, localTraceStatus = evidence.LocalTrace.Status, localTraceHops = evidence.LocalTrace.Hops.Length, cgnatResult = cgnat.GetProperty("label").GetString() }));
        if (storedEvidence.GetProperty("ipLookupProviderId").GetString() != provider.Id || storedEvidence.GetProperty("publicIPv4").GetString() != evidence.PublicIPv4)
            throw new Exception("Selected provider/address was not preserved in the report.");
        Console.WriteLine(JsonSerializer.Serialize(new { lookupProvider = provider.Name, selectedProviderPreserved = true, ipv6Skipped = !provider.SupportsIPv6 }));
        Console.WriteLine(JsonSerializer.Serialize(new { completed = id, state = job.GetProperty("state").GetString(), regions = runs.Select(r => new { name = r.GetProperty("location").GetString(), results = r.GetProperty("results").GetArrayLength() }), localSnapshotReturned = job.GetProperty("localEvidence").ValueKind == JsonValueKind.Object, idempotent = true, history = true }));
    }
    private sealed class QuietLog : IActivityLog { public void Write(string level, string message) { } }
}
