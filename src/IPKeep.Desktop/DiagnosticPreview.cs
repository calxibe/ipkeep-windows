#if DEBUG
using IPKeep.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Net;
using System.Text.Json;
using Windows.Graphics;

namespace IPKeep.Desktop;
// Compiled only in Debug; the fixture never accesses credentials, local preferences or the network.
internal sealed class DiagnosticPreview : DiagnosticRuntime
{
    private DiagnosticService[] services = [new("HTTPS", "https", 443), new("SSH", "ssh", 22)];
    private readonly string id = Guid.NewGuid().ToString("D");
    private int polls;
    private LocalDiagnosticEvidence? evidence;
    public static Window Open()
    {
        var window = new Window { Title = "IPKeep diagnostic preview — simulated data" };
        var button = new Button { Content = "Diagnostics for home.ipkeep.cloud", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        window.Content = button; window.AppWindow.Resize(new SizeInt32(1100, 900));
        button.Click += async (_, _) => await new DiagnosticDialog("ipk_simulated_0123456789012345", "home.ipkeep.cloud", "amazon", () => true, button.XamlRoot, new DiagnosticPreview()).ShowAsync();
        window.Activate(); return window;
    }
    public override HttpClient CreateHttp() => new(new Handler(this));
    public override LocalDiagnosticOptions Load(string token, string hostname) => new(new() { [22] = new("192.168.1.20", 22) });
    public override void Save(string token, string hostname, LocalDiagnosticOptions options) { }
    public override async Task<LocalDiagnosticEvidence> CollectAsync(string hostname, string providerId, DiagnosticService[] s, LocalDiagnosticOptions o, bool? u, IProgress<string> p, CancellationToken ct)
    {
        await Task.Delay(1500, ct);
        evidence = new("windows", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "203.0.113.20", null, "100.64.1.2", "192.168.1.20", true, [], IpLookupProviderId: providerId,
            RouterWan: new("detected", false, false, [new("192.168.1.1", "100.64.1.2", "upnp")]),
            LocalTrace: new("partial", "198.51.100.10", [new(1, "192.168.1.1", 1.2, "reply"), new(2, "100.64.0.1", 6.5, "reply"), new(3, null, null, "timeout")]));
        return evidence;
    }
    private object Job() => new { id, sampleData = true, hostname = "home.ipkeep.cloud", createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), state = polls < 2 ? "pending" : "complete", localEvidence = evidence,
        tests = new[] { new { type = "tcp", port = 443 }, new { type = "ssh", port = 22 } },
        runs = new[] { new { location = "Frankfurt", state = polls < 2 ? "queued" : "complete", results = new[] { new { testIndex = 0, family = 4, durationMs = 28.2, state = "ok", detail = new { connected = true } } } } },
        analysis = new { summary = polls < 2 ? "Running checks… (simulated)" : "SSH needs attention (simulated)",
            checks = new[] { new { state = "pass", label = "Windows app connected", detail = "Simulated observations; no live requests." }, new { state = "pass", label = "Public IPv4 detected", detail = "203.0.113.20 — documentation address" },
                new { state = polls < 2 ? "running" : "fail", label = "CGNAT likely on your router connection", detail = "UPnP via 192.168.1.1: 100.64.1.2 · Amazon Check IP: 203.0.113.20. Simulated example: a shared WAN address is evidence of likely CGNAT; confirm with your ISP." },
                new { state = polls < 2 ? "running" : "pass", label = "DNS and HTTPS", detail = "Europe and North America" }, new { state = polls < 2 ? "running" : "fail", label = "SSH external connection", detail = "Local device accepts TCP/22; external probes timed out." } },
            findings = polls < 2 ? Array.Empty<object>() : [new { title = "Check SSH port forwarding", explanation = "This fictional example demonstrates a local listener with an external timeout.", steps = new[] { "Check TCP/22 forwarding and firewall rules." }, forwarding = "TCP 22 → 192.168.1.20:22", note = "Simulated result, not a measurement of this network." }] } };
    private sealed class Handler(DiagnosticPreview preview) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result; string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/settings")) {
                if (request.Content is not null) { using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)); preview.services = body.RootElement.GetProperty("services").Deserialize<DiagnosticService[]>(DiagnosticClient.JsonOptions)!; }
                result = new { host = new { id = 1, hostname = "home.ipkeep.cloud", ipv4 = "203.0.113.20", ipv6 = (string?)null }, services = preview.services };
            } else if (path.EndsWith("/dns")) result = new { results = new[] { new { resolver = "Cloudflare", type = "A", state = "matches", records = new[] { new { address = "203.0.113.20", ttl = 60 } } }, new { resolver = "Google", type = "A", state = "matches", records = new[] { new { address = "203.0.113.20", ttl = 60 } } } } };
            else if (path.Contains("/jobs/") || request.Method == HttpMethod.Post) { preview.polls++; result = preview.Job(); }
            else result = new { jobs = Array.Empty<object>() };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, DiagnosticClient.JsonOptions)) };
        }
    }
}
#endif
