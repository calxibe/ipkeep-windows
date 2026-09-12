using IPKeep.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace IPKeep.Desktop;

internal sealed class DiagnosticDialog : ContentDialog
{
    private readonly string token, hostname;
    private readonly IpLookupProvider lookupProvider;
    private readonly Func<bool?> updaterRunning;
    private readonly HttpClient http;
    private readonly DiagnosticRuntime runtime;
    private readonly DiagnosticClient client;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? run;
    private DiagnosticRequest? pendingRequest;
    private string? jobId;
    private bool active, loaded;
    private readonly TextBlock status = Text("Loading diagnostic settings…"), summary = Text("");
    private readonly StackPanel checks = new() { Spacing = 10 }, findings = new() { Spacing = 12 }, editors = new() { Spacing = 14 };
    private readonly List<ServiceEditor> serviceEditors = [];
    
    private readonly TextBox wan = new() { Header = "Router WAN IPv4 (manual fallback)", PlaceholderText = "Automatic detection first; enter only if needed" };
    private readonly Button save = new() { Content = "Save settings" }, cancel = new() { Content = "Cancel test", IsEnabled = false };
    private readonly ComboBox preset = new() { Header = "Add a service", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox history = new() { Header = "Previous tests from this token", PlaceholderText = "No previous tests", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button historyLoad = new() { Content = "View selected test", IsEnabled = false };
    private readonly Expander advanced = new() { Header = "Advanced", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock report = Text("");
    private readonly StackPanel dnsRows = new() { Spacing = 8 };
    private JsonElement? dnsReport;
    private readonly Expander details = new() { Header = "Full details", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private static readonly DiagnosticService[] Presets = [new("HTTPS", "https", 443), new("HTTP", "http", 80), new("SSH / SFTP", "ssh", 22),
        new("Home Assistant", "http", 8123), new("Plex Media Server", "http", 32400), new("Jellyfin", "http", 8096), new("Jellyfin HTTPS", "https", 8920),
        new("Minecraft Java", "minecraft", 25565), new("Minecraft Bedrock", "bedrock", 19132), new("Synology DSM", "https", 5001), new("QNAP QTS", "https", 443),
        new("Camera / NVR / RTSP", "rtsp", 554), new("RDP", "tcp", 3389), new("Custom service", "tcp", 0)];

    public DiagnosticDialog(string token, string hostname, string providerId, Func<bool?> updaterRunning, XamlRoot root, DiagnosticRuntime? runtime = null)
    {
        this.token = token; this.hostname = hostname; this.updaterRunning = updaterRunning; this.runtime = runtime ?? new DiagnosticRuntime(); http = this.runtime.CreateHttp(); client = new(http);
        lookupProvider = IpLookupProviders.Get(providerId);
        Title = "Network diagnostic · " + hostname; XamlRoot = root;
        Resources["ContentDialogMaxWidth"] = 980d;
        PrimaryButtonText = "Test host"; CloseButtonText = "Close"; IsPrimaryButtonEnabled = false;
        var body = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 20, 8) };
        body.Children.Add(Text("Compare this computer and your local services with independent tests from Frankfurt and Virginia. No DNS records or firewall rules are changed."));
        body.Children.Add(Text($"IP lookup for new tests: {lookupProvider.Name}" + (lookupProvider.SupportsIPv6 ? " (IPv4 and IPv6)." : " (IPv4 only; IPv6 address discovery is skipped).")));
        body.Children.Add(Text("By selecting Test host, you confirm permission to test these services and share their local observations with IPKeep for this report (retained seven days)."));
        body.Children.Add(status); summary.Visibility = Visibility.Collapsed; body.Children.Add(summary); checks.Visibility = findings.Visibility = Visibility.Collapsed; body.Children.Add(checks); body.Children.Add(findings);
        var detailPanel = new StackPanel { Spacing = 10 };
        var copy = new Button { Content = "Copy full report" }; copy.Click += (_, _) => { var data = new DataPackage(); data.SetText(report.Text); Clipboard.SetContent(data); };
        report.FontFamily = new FontFamily("Consolas"); report.FontSize = 12; report.Foreground = new SolidColorBrush(Colors.WhiteSmoke);
        detailPanel.Children.Add(copy);
        detailPanel.Children.Add(new Border { Background = new SolidColorBrush(Colors.Black), Padding = new Thickness(16), CornerRadius = new CornerRadius(5),
            Child = new ScrollViewer { Content = report, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        details.Content = detailPanel; details.Visibility = Visibility.Collapsed; body.Children.Add(details);
        var dnsPanel = new StackPanel { Spacing = 8 }; dnsPanel.Children.Add(Text("Cloudflare and Google · A / AAAA", true)); dnsPanel.Children.Add(dnsRows);
        var dnsRefresh = new Button { Content = "Check public DNS again" }; dnsRefresh.Click += async (_, _) => { dnsRefresh.IsEnabled = false; try { await LoadDnsAsync(); } finally { dnsRefresh.IsEnabled = true; } }; dnsPanel.Children.Add(dnsRefresh);
        body.Children.Add(new Expander { Header = "Public DNS lookup", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = dnsPanel });
        var advancedPanel = new StackPanel { Spacing = 12 };
        advancedPanel.Children.Add(Text("Public service choices are shared with the website. Local addresses and port mappings are saved securely for this Windows user. Leave Local device blank for this computer. For another device, enter its private LAN IP. A local listener does not establish application identity."));
        advancedPanel.Children.Add(editors);
        preset.ItemsSource = Presets.Select(p => p.Name + (p.Port > 0 ? $" ({p.Port})" : "")).ToArray(); preset.SelectedIndex = 0;
        var add = new Button { Content = "Add service" }; add.Click += (_, _) =>
        {
            if (serviceEditors.Count >= 4) { status.Text = "Choose up to four services."; return; }
            AddEditor(Presets[preset.SelectedIndex], new());
        };
        advancedPanel.Children.Add(preset); advancedPanel.Children.Add(add);
        advancedPanel.Children.Add(Text("Ping and DNS are included. Traceroute is included when the selected services leave room. WireGuard/OpenVPN cannot be verified without VPN credentials; generic UDP silence is inconclusive."));
        advancedPanel.Children.Add(wan); advancedPanel.Children.Add(Text("Each test asks your configured router for its WAN IPv4 using read-only UPnP/NAT-PMP requests. If unavailable, enter the current WAN IPv4 from your router's Internet status. This fallback is not saved; your computer's LAN address cannot establish CGNAT. A short local traceroute toward the selected IP lookup service is included in Full details."));
        advancedPanel.Children.Add(save); advanced.Content = advancedPanel; body.Children.Add(advanced);
        body.Children.Add(history); body.Children.Add(historyLoad);
        body.Children.Add(Text("Test host sends public addresses, the selected local endpoints, listener/web observations, router WAN replies, possible VPN/multiple-gateway indicators, local traceroute hops and updater state to IPKeep. Reports are retained for seven days. Local observations are client-reported. Closing this window stops waiting; accepted regional jobs may finish."));
        body.Children.Add(cancel);
        Content = new ScrollViewer { Content = body, Width = Math.Clamp(root.Size.Width - 120, 350, 880),
            MaxHeight = Math.Max(250, root.Size.Height - 240), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        history.SelectionChanged += (_, _) => historyLoad.IsEnabled = !active && history.SelectedItem is HistoryItem;
        PrimaryButtonClick += async (_, e) => { e.Cancel = true; await StartAsync(); };
        save.Click += async (_, _) => { try { await SaveAsync(lifetime.Token); status.Text = "Settings saved."; } catch (Exception ex) { Error(ex); } };
        cancel.Click += async (_, _) =>
        {
            run?.Cancel();
            if (jobId is not null) try { await client.CancelAsync(token, jobId, lifetime.Token); status.Text = "Test cancelled."; } catch (Exception ex) { Error(ex); }
        };
        historyLoad.Click += async (_, _) =>
        {
            if (history.SelectedItem is not HistoryItem item) return;
            try { Render(await client.ReadAsync(token, item.Id, lifetime.Token)); } catch (Exception ex) { Error(ex); }
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { run?.Cancel(); lifetime.Cancel(); http.Dispose(); };
    }
    private async Task LoadAsync()
    {
        try
        {
            var settings = await client.SettingsAsync(token, hostname, lifetime.Token);
            LocalDiagnosticOptions options;
            try { options = runtime.Load(token, hostname); }
            catch { options = new(new()); status.Text = "Local preferences could not be read. Check the local destinations before testing."; }
            wan.Text = options.WanIPv4 ?? "";
            foreach (var service in settings.Services) AddEditor(service, options.Targets.GetValueOrDefault(service.Port, new()));
            loaded = true; UpdateButtons();
            status.Text = "Ready. Select Test host to check both sides of your connection.";
            await Task.WhenAll(LoadHistoryAsync(), LoadDnsAsync());
        }
        catch (Exception ex) { Error(ex); }
    }
    private void AddEditor(DiagnosticService service, LocalServiceTarget target)
    {
        var editor = new ServiceEditor(service, target);
        editor.Remove.Click += (_, _) => { serviceEditors.Remove(editor); editors.Children.Remove(editor.View); };
        serviceEditors.Add(editor); editors.Children.Add(editor.View);
    }
    private (DiagnosticService[], LocalDiagnosticOptions) ReadSettings()
    {
        var services = serviceEditors.Select(e => e.Service()).ToArray(); DiagnosticClient.ValidateServices(services);
        var targets = serviceEditors.ToDictionary(e => e.Service().Port, e => e.Target());
        string? wanAddress = string.IsNullOrWhiteSpace(wan.Text) ? null : wan.Text.Trim();
        if (wanAddress is not null && (!System.Net.IPAddress.TryParse(wanAddress, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new SettingsException("Enter an IPv4 address from your router's WAN status, or leave it blank.");
        return (services, new(targets, wanAddress));
    }
    private async Task SaveAsync(CancellationToken ct)
    {
        var (services, options) = ReadSettings();
        await client.SaveSettingsAsync(token, hostname, services, ct);
        runtime.Save(token, hostname, options);
    }
    private void UpdateButtons()
    {
        IsPrimaryButtonEnabled = loaded && !active;
        advanced.IsEnabled = loaded && !active && pendingRequest is null;
        save.IsEnabled = loaded && !active; cancel.IsEnabled = active;
        history.IsEnabled = !active && history.Items.Count > 0;
        historyLoad.IsEnabled = !active && history.SelectedItem is HistoryItem;
        PrimaryButtonText = pendingRequest is null ? "Test host" : "Retry same test";
    }
    private async Task StartAsync()
    {
        if (active || !loaded) return;
        active = true; run?.Dispose(); run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var ct = run.Token;
        UpdateButtons(); jobId = null; checks.Visibility = Visibility.Visible; findings.Visibility = Visibility.Collapsed; findings.Children.Clear(); checks.Children.Clear(); report.Text = ""; summary.Text = ""; details.Visibility = summary.Visibility = Visibility.Collapsed;
        try
        {
            if (pendingRequest is null)
            {
                var (services, options) = ReadSettings();
                foreach (var label in new[] { "Windows updater state", "Public IPv4", "Public IPv6", "Router WAN / CGNAT", "Local traceroute", "Windows DNS lookup", "Gateway ping", "DNS and regional reachability" }.Concat(services.Select(s => s.Name + " local and external checks"))) AddCheck("running", label, "Waiting for this run's measurements.");
                await SaveAsync(ct);
                var evidence = await runtime.CollectAsync(hostname, lookupProvider.Id, services, options, updaterRunning(), new Progress<string>(s => { if (!lifetime.IsCancellationRequested) status.Text = s; }), ct);
                ct.ThrowIfCancellationRequested();
                pendingRequest = new(Guid.NewGuid().ToString("D"), true, true, evidence);
            }
            status.Text = "Queuing the regional tests…";
            // Keep the UUID and evidence on uncertain replies; Retry never spends a second job quota.
            var job = await client.StartAsync(token, hostname, pendingRequest, lifetime.Token);
            jobId = job.GetProperty("id").GetString()!; pendingRequest = null;
            if (ct.IsCancellationRequested) { await client.CancelAsync(token, jobId, lifetime.Token); ct.ThrowIfCancellationRequested(); }
            Render(job);
            while (job.GetProperty("state").GetString() == "pending")
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                job = await client.ReadAsync(token, jobId, ct); Render(job);
            }
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) { status.Text = "Test cancelled."; StopSpinners(); } }
        catch (Exception ex) { if (ex is UpdateException { StatusCode: >= 400 and < 500 and not 429 }) pendingRequest = null; Error(ex); StopSpinners(); }
        finally { active = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
    }
    private void StopSpinners()
    {
        checks.Children.Clear(); AddCheck("unknown", "Diagnostic stopped before all results were received", pendingRequest is null ? "Completed regional results remain available in Previous tests." : "Retry same test to resolve the uncertain response without creating a duplicate job.");
    }
    private async Task LoadHistoryAsync()
    {
        var result = await client.HistoryAsync(token, hostname, lifetime.Token);
        var items = result.GetProperty("jobs").EnumerateArray().Select(j => new HistoryItem(j.GetProperty("ID").GetString()!,
            DateTimeOffset.FromUnixTimeSeconds(j.GetProperty("CreatedAt").GetInt64()).ToLocalTime().ToString("g"))).ToArray();
        history.ItemsSource = items; history.IsEnabled = !active && items.Length > 0;
        history.PlaceholderText = items.Length > 0 ? "Choose a previous test" : "No previous tests";
    }
    private async Task LoadDnsAsync()
    {
        dnsRows.Children.Clear(); dnsRows.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        try {
            var data = await client.DnsAsync(token, hostname, lifetime.Token); dnsReport = data; dnsRows.Children.Clear();
            foreach (var row in data.GetProperty("results").EnumerateArray()) {
                var records = string.Join(", ", row.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("address").GetString() + " / TTL " + r.GetProperty("ttl")));
                dnsRows.Children.Add(Text($"{row.GetProperty("resolver").GetString()}  {row.GetProperty("type").GetString()}  ·  {row.GetProperty("state").GetString()}  ·  {(records.Length > 0 ? records : "No address returned")}"));
            }
        } catch (Exception ex) { dnsRows.Children.Clear(); dnsRows.Children.Add(Text(ex is UpdateException ? ex.Message : "Public DNS lookup could not complete.")); }
    }
    private sealed record HistoryItem(string Id, string Label) { public override string ToString() => Label; }
    private void Render(JsonElement job)
    {
        checks.Children.Clear(); findings.Children.Clear();
        details.Visibility = summary.Visibility = Visibility.Visible;
        var analysis = job.GetProperty("analysis");
        if (analysis.ValueKind != JsonValueKind.Object) throw new UpdateException("No guided diagnostic report was returned.");
        var state = job.GetProperty("state").GetString();
        status.Text = state == "pending" ? "Testing from Frankfurt and Virginia…" : "Test " + state + ".";
        summary.Text = analysis.TryGetProperty("summary", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : "Network diagnostic results";
        foreach (var check in analysis.GetProperty("checks").EnumerateArray()) AddCheck(check.GetProperty("state").GetString()!, check.GetProperty("label").GetString()!, check.GetProperty("detail").GetString() ?? "");
        foreach (var finding in analysis.GetProperty("findings").EnumerateArray())
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Text(finding.GetProperty("title").GetString()!, true)); panel.Children.Add(Text(finding.GetProperty("explanation").GetString()!));
            int i = 1; foreach (var step in finding.GetProperty("steps").EnumerateArray()) panel.Children.Add(Text($"{i++}. {step.GetString()}"));
            foreach (var key in new[] { "forwarding", "note" }) if (finding.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) panel.Children.Add(Text(value.GetString()!));
            findings.Children.Add(panel);
        }
        checks.Visibility = Visibility.Visible; findings.Visibility = findings.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        report.Text = FormatReport(job) + (dnsReport is { } dns ? "\nLATEST PUBLIC DNS LOOKUP (separate observation)\n" + JsonSerializer.Serialize(dns, new JsonSerializerOptions { WriteIndented = true }) : "");
    }
    private void AddCheck(string state, string label, string detail)
    {
        var row = new Grid { ColumnSpacing = 12 }; row.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        if (state == "running") row.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top });
        else { var icon = Text(state == "pass" ? "✓" : state == "fail" ? "✕" : "—", true); icon.Foreground = new SolidColorBrush(state == "pass" ? Colors.SeaGreen : state == "fail" ? Colors.IndianRed : Colors.Gray); row.Children.Add(icon); }
        var content = new StackPanel { Spacing = 3 }; content.Children.Add(Text(label, true)); if (detail.Length > 0) content.Children.Add(Text(detail)); Grid.SetColumn(content, 1); row.Children.Add(content); checks.Children.Add(row);
    }
    private void Error(Exception ex)
    {
        if (lifetime.IsCancellationRequested) return;
        status.Text = ex is UpdateException or SettingsException ? ex.Message : "Diagnostics could not complete. Check your connection and try again.";
    }
    private static TextBlock Text(string value, bool bold = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
    private sealed class QuietLog : IActivityLog { public void Write(string level, string message) { } }
    private static string FormatReport(JsonElement job)
    {
        var output = new StringBuilder("IPKEEP / NETWORK DIAGNOSTIC\n" + new string('=', 65) + "\n");
        if (job.TryGetProperty("sampleData", out var sample) && sample.ValueKind == JsonValueKind.True) output.AppendLine("SIMULATED PREVIEW — no real network tests");
        output.AppendLine(job.GetProperty("hostname").GetString());
        output.AppendLine("Job: " + job.GetProperty("id").GetString());
        output.AppendLine("UTC: " + DateTimeOffset.FromUnixTimeSeconds(job.GetProperty("createdAt").GetInt64()).ToString("u"));
        foreach (var region in job.GetProperty("runs").EnumerateArray())
        {
            output.AppendLine($"\n{region.GetProperty("location").GetString()} / {region.GetProperty("state").GetString()}");
            if (region.GetProperty("results").ValueKind != JsonValueKind.Array) continue;
            foreach (var result in region.GetProperty("results").EnumerateArray())
            {
                var test = job.GetProperty("tests")[result.GetProperty("testIndex").GetInt32()];
                output.AppendLine($"  IPv{result.GetProperty("family")} {test.GetProperty("type").GetString()} {(test.TryGetProperty("port", out var port) ? port.ToString() : "")}: {result.GetProperty("state").GetString()} / {result.GetProperty("durationMs")} ms");
            }
        }
        if (job.TryGetProperty("localEvidence", out var evidence) && evidence.ValueKind == JsonValueKind.Object && evidence.TryGetProperty("localTrace", out var trace))
        {
            output.AppendLine("\nLOCAL IPv4 TRACEROUTE / client to selected IP lookup service");
            output.AppendLine($"Target: {trace.GetProperty("targetIPv4")} / {trace.GetProperty("status")}");
            output.AppendLine("One ICMP sample per hop; maximum 12 hops / 7 seconds. HTTPS may take a different path.");
            output.AppendLine("Hop  Address                  RTT / result");
            foreach (var hop in trace.GetProperty("hops").EnumerateArray())
                output.AppendLine($"{hop.GetProperty("ttl").ToString(),3}  {(hop.GetProperty("address").GetString() ?? "*"),-24} {(hop.GetProperty("rttMs").ValueKind == JsonValueKind.Null ? "*" : hop.GetProperty("rttMs").ToString() + " ms")} / {hop.GetProperty("state")}");
            output.AppendLine("Missing/private/shared hops do not establish CGNAT.");
        }
        output.AppendLine("\nALL DETAILS (including traceroute hops, TLS, HTTP and client-reported local observations)\n" + new string('-', 65));
        output.AppendLine(JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));
        return output.ToString();
    }
    private sealed class ServiceEditor
    {
        public StackPanel View { get; } = new() { Spacing = 8 };
        public Button Remove { get; } = new() { Content = "Remove" };
        private readonly TextBox name = new() { Header = "Name", MaxLength = 40 }, address = new() { Header = "Local device (blank = this computer)", PlaceholderText = "e.g. 192.168.1.20" }, website = new() { Header = "Website hostname (HTTP Host / TLS name)", PlaceholderText = "Default: this IPKeep hostname" };
        private readonly ComboBox type = new() { Header = "Test type", HorizontalAlignment = HorizontalAlignment.Stretch }, port = new() { Header = "Public port", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a port" };
        private readonly NumberBox localPort = new() { Header = "Local port", Minimum = 1, Maximum = 65535, PlaceholderText = "Same as public" };
        public ServiceEditor(DiagnosticService service, LocalServiceTarget target)
        {
            name.Text = service.Name; type.ItemsSource = new[] { "TCP connection", "HTTP response", "HTTPS and certificate", "SSH identification", "Minecraft Java status", "Minecraft Bedrock response", "RTSP response" }; type.SelectedIndex = Array.IndexOf(DiagnosticClient.Types, service.Type);
            port.ItemsSource = DiagnosticClient.Ports; port.SelectedItem = service.Port == 0 ? null : service.Port;
            bool manualPort = service.Port != 0; port.SelectionChanged += (_, _) => manualPort = true;
            type.SelectionChanged += (_, _) => { website.Visibility = type.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed; if (!manualPort) port.SelectedItem = type.SelectedIndex switch { 1 => 80, 2 => 443, 3 => 22, 4 => 25565, 5 => 19132, 6 => 554, _ => (int?)null }; };
            website.Text = service.WebsiteHostname ?? ""; website.Visibility = service.Type is "http" or "https" ? Visibility.Visible : Visibility.Collapsed;
            address.Text = target.Address; localPort.Value = target.Port == 0 ? double.NaN : target.Port;
            var first = new Grid { ColumnSpacing = 10 }; foreach (var width in new[] { 2d, 2d, 1d }) first.ColumnDefinitions.Add(new() { Width = new GridLength(width, GridUnitType.Star) });
            first.Children.Add(name); Grid.SetColumn(type, 1); first.Children.Add(type); Grid.SetColumn(port, 2); first.Children.Add(port);
            var second = new Grid { ColumnSpacing = 10 }; second.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); second.ColumnDefinitions.Add(new() { Width = new GridLength(130) });
            second.Children.Add(address); Grid.SetColumn(localPort, 1); second.Children.Add(localPort);
            View.Children.Add(first); View.Children.Add(second); View.Children.Add(website); View.Children.Add(Remove);
        }
        public DiagnosticService Service() => new(name.Text.Trim(), DiagnosticClient.Types[Math.Max(0, type.SelectedIndex)], port.SelectedItem is int p ? p : 0, type.SelectedIndex is 1 or 2 && !string.IsNullOrWhiteSpace(website.Text) ? website.Text.Trim().ToLowerInvariant() : null);
        public LocalServiceTarget Target()
        {
            LocalDiagnostics.ValidateLocalTarget(address.Text);
            if (!double.IsNaN(localPort.Value) && (localPort.Value != Math.Truncate(localPort.Value) || localPort.Value is < 1 or > 65535)) throw new SettingsException("Local port must be a whole number from 1 to 65535.");
            return new(address.Text.Trim(), double.IsNaN(localPort.Value) ? 0 : (int)localPort.Value);
        }
    }
}
