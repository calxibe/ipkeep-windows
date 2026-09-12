using IPKeep.Core;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class DiagnosticTests
{
    private const string Token = "ipk_0123456789012345678901234567890123456789012345678901234567890123";
    private static void Check(bool ok) { if (!ok) throw new Exception("Diagnostic assertion failed"); }
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Diagnostic authentication stays on the canonical HTTPS endpoint and permission errors are actionable", async () =>
        {
            using var http = new HttpClient(new Handler(request =>
            {
                Check(request.RequestUri!.AbsoluteUri == "https://api.ipkeep.net/diagnostics/hosts/home.ipkeep.cloud/settings");
                Check(request.Headers.Authorization?.Parameter == Token && request.Headers.Authorization.Scheme == "Bearer");
                Check(!request.RequestUri.ToString().Contains(Token) && !request.Headers.Contains("Cookie"));
                return new(HttpStatusCode.Forbidden) { Content = new StringContent(Token) };
            }));
            try { await new DiagnosticClient(http).SettingsAsync(Token, "home.ipkeep.cloud", default); throw new Exception("Expected permission refusal"); }
            catch (UpdateException ex) { Check(ex.Message.Contains("denied") && !ex.Message.Contains(Token)); }
        }));
        tests.Add(("Diagnostic settings cannot substitute another host or arbitrary service ports", async () =>
        {
            using var http = new HttpClient(new Handler(_ => Json(new { host = new { id = 1, hostname = "other.ipkeep.cloud" }, services = Array.Empty<object>() })));
            try { await new DiagnosticClient(http).SettingsAsync(Token, "home.ipkeep.cloud", default); throw new Exception("Expected host refusal"); } catch (UpdateException) { }
            foreach (var s in new[] { new DiagnosticService("SMTP", "tcp", 25), new("HTTP", "http", 80, "https://example.com"), new("SSH", "ssh", 22, "example.com") })
            { try { DiagnosticClient.ValidateServices([s]); throw new Exception("Expected service refusal"); } catch (SettingsException) { } }
        }));
        tests.Add(("Diagnostic settings omit absent website names and preserve HTTPS virtual-host settings", async () =>
        {
            string? body = null;
            using var http = new HttpClient(new AsyncHandler(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(new { success = true }); }));
            var client = new DiagnosticClient(http);
            await client.SaveSettingsAsync(Token, "home.ipkeep.cloud", [new("SSH", "ssh", 22), new("HTTPS", "https", 443, "usa1.example.com")], default);
            using var json = JsonDocument.Parse(body!); var services = json.RootElement.GetProperty("services");
            Check(!services[0].TryGetProperty("websiteHostname", out _) && services[1].GetProperty("websiteHostname").GetString() == "usa1.example.com");
        }));
        tests.Add(("Retry sends the same UUID and evidence and never sends an alternate external target", async () =>
        {
            var bodies = new List<string>(); using var http = new HttpClient(new AsyncHandler(async request => { bodies.Add(await request.Content!.ReadAsStringAsync()); return Json(new { id = Guid.NewGuid() }); }));
            var client = new DiagnosticClient(http);
            var evidence = new LocalDiagnosticEvidence("windows", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "8.8.8.8", null, null, "192.168.1.20", true, []);
            var request = new DiagnosticRequest(Guid.NewGuid().ToString("D"), true, true, evidence);
            await client.StartAsync(Token, "home.ipkeep.cloud", request, default); await client.StartAsync(Token, "home.ipkeep.cloud", request, default);
            Check(bodies.Count == 2 && bodies[0] == bodies[1]); using var json = JsonDocument.Parse(bodies[0]);
            Check(!json.RootElement.TryGetProperty("ipv4", out _) && !json.RootElement.TryGetProperty("token", out _));
        }));
        tests.Add(("Local targets permit private LAN addresses but reject URLs, metadata and public addresses", () =>
        {
            Check(LocalDiagnostics.ValidateLocalTarget("") is null);
            foreach (var address in new[] { "192.168.1.20", "10.1.2.3", "fd00::20" }) Check(LocalDiagnostics.ValidateLocalTarget(address) is not null);
            foreach (var address in new[] { "169.254.169.254", "8.8.8.8", "127.0.0.1", "http://192.168.1.2", "192.168.1.2:443", "::ffff:192.168.1.2" })
            { try { LocalDiagnostics.ValidateLocalTarget(address); throw new Exception("Expected address refusal"); } catch (SettingsException) { } }
            return Task.CompletedTask;
        }));
        tests.Add(("Loopback listeners and IPv6-only bindings never claim an externally accessible IPv4 listener", () =>
        {
            var loopback = LocalDiagnostics.InspectListeners([new(IPAddress.Loopback, 443), new(IPAddress.IPv6Loopback, 443)], 443);
            Check(loopback.Listening && loopback.LoopbackOnly && loopback.Families.Length == 0);
            var v6 = LocalDiagnostics.InspectListeners([new(IPAddress.IPv6Any, 443)], 443); Check(v6.Listening && !v6.LoopbackOnly && v6.Families.SequenceEqual(new[] { 6 }));
            var missing = LocalDiagnostics.InspectListeners([new(IPAddress.Any, 8443)], 443); Check(!missing.Listening);
            return Task.CompletedTask;
        }));
        tests.Add(("Real local listener capture uses mapped local port and retains uncertainty when IP discovery fails", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var collector = new LocalDiagnostics(new FailedResolver(), includeNetworkChecks: false);
            // localhost avoids public DNS/network probes; resolver is a failure fixture.
            var result = await collector.CollectAsync("localhost", "ipkeep", [new("TCP", "tcp", 443)], new(new() { [443] = new("", port) }), true, null, deadline.Token);
            Check(result.PublicIPv4 is null && result.PublicIPv6 is null && result.Services[0].Port == 443 && result.Services[0].LocalPort == port && result.Services[0].LoopbackOnly);
        }));
        tests.Add(("Cancelled local collection does not submit stale evidence", async () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await new LocalDiagnostics(new FailedResolver(), includeNetworkChecks: false).CollectAsync("localhost", "ipkeep", [new("HTTPS", "https", 443)], new(new()), null, null, cts.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }));
        tests.Add(("Diagnostics use only the selected lookup provider and skip unsupported IPv6 without sending credentials", async () =>
        {
            foreach (var provider in IpLookupProviders.All)
            {
                var requests = new System.Collections.Concurrent.ConcurrentBag<Uri>();
                using var http4 = new HttpClient(new Handler(request =>
                {
                    requests.Add(request.RequestUri!); Check(request.RequestUri == provider.IPv4Endpoint && request.Headers.Authorization is null);
                    return provider.ReturnsFamilyJson ? Json(new { ipv4 = "8.8.8.8" }) : new(HttpStatusCode.OK) { Content = new StringContent("8.8.8.8\n") };
                }));
                using var http6 = new HttpClient(new Handler(request =>
                {
                    requests.Add(request.RequestUri!); Check(provider.SupportsIPv6 && request.RequestUri == provider.IPv6Endpoint && request.Headers.Authorization is null);
                    return provider.ReturnsFamilyJson ? Json(new { ipv6 = "2606:4700:4700::1111" }) : new(HttpStatusCode.OK) { Content = new StringContent("2606:4700:4700::1111\n") };
                }));
                var result = await new LocalDiagnostics(new PublicIpResolver(http4, http6, new QuietLog()), includeNetworkChecks: false)
                    .CollectAsync("localhost", provider.Id, [], new(new()), null, null, default);
                Check(result.PublicIPv4 == "8.8.8.8" && result.PublicIPv6 == (provider.SupportsIPv6 ? "2606:4700:4700::1111" : null));
                Check(result.IpLookupProviderId == provider.Id && requests.Count == (provider.SupportsIPv6 ? 2 : 1));
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, DiagnosticClient.JsonOptions));
                Check(json.RootElement.GetProperty("ipLookupProviderId").GetString() == provider.Id);
            }
        }));
        tests.Add(("A failed selected lookup stays unverified and never falls back to IPKeep or another provider", async () =>
        {
            var requests = new List<Uri>();
            using var http = new HttpClient(new Handler(request => { requests.Add(request.RequestUri!); return new(HttpStatusCode.ServiceUnavailable); }));
            var result = await new LocalDiagnostics(new PublicIpResolver(http, http, new QuietLog()), includeNetworkChecks: false)
                .CollectAsync("localhost", "amazon", [], new(new()), null, null, default);
            Check(requests.Count == 1 && requests[0] == IpLookupProviders.Get("amazon").IPv4Endpoint);
            Check(result.PublicIPv4 is null && result.PublicIPv6 is null && result.IpLookupProviderId == "amazon");
        }));
        tests.Add(("Invalid diagnostic lookup providers are rejected before discovery", async () =>
        {
            try { await new LocalDiagnostics(new FailedResolver(), includeNetworkChecks: false).CollectAsync("localhost", "https://example.com", [], new(new()), null, null, default); throw new Exception("Expected invalid provider"); }
            catch (SettingsException) { }
        }));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request); }
    private sealed class FailedResolver : IPublicIpResolver
    { public Task<string> ResolveAsync(bool ipv6, CancellationToken ct, string providerId = IpLookupProviders.DefaultId) { ct.ThrowIfCancellationRequested(); throw new UpdateException("Unavailable"); } }
    private sealed class QuietLog : IActivityLog { public void Write(string level, string message) { } }
}
