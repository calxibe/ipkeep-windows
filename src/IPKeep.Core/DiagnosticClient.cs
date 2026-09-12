using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IPKeep.Core;

public sealed record DiagnosticService(string Name, string Type, int Port, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? WebsiteHostname = null);
public sealed record DiagnosticHost(long ID, string Hostname, string? IPv4, string? IPv6);
public sealed record DiagnosticSettings(DiagnosticHost Host, DiagnosticService[] Services);
public sealed record LocalServiceObservation(int Port, string Transport, bool? Listening, int[] Families,
    string Target, string? LocalAddress, int LocalPort, bool LoopbackOnly, double? ConnectMs, int? HttpStatus, string WebState);
public sealed record LocalDiagnosticEvidence(string Source, long ObservedAt, string? PublicIPv4, string? PublicIPv6,
    string? WanIPv4, string? LanIPv4, bool? UpdaterRunning, LocalServiceObservation[] Services, string[]? LocalDns = null, string? GatewayIPv4 = null, double? GatewayLatencyMs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? IpLookupProviderId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RouterWanEvidence? RouterWan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LocalTraceEvidence? LocalTrace = null);
public sealed record DiagnosticRequest(string RequestId, bool Guided, bool AcknowledgePermission, LocalDiagnosticEvidence LocalEvidence);
public sealed record LocalServiceTarget(string Address = "", int Port = 0);
public sealed record LocalDiagnosticOptions(Dictionary<int, LocalServiceTarget> Targets, string? WanIPv4 = null);

public sealed class DiagnosticClient(HttpClient http)
{
    public static readonly Uri Endpoint = new("https://api.ipkeep.net/diagnostics/");
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string HostPath(string hostname) => "hosts/" + ClientSettings.NormalizeHostname(hostname);
    public async Task<DiagnosticSettings> SettingsAsync(string token, string hostname, CancellationToken ct)
    {
        var json = await SendAsync(token, HttpMethod.Get, HostPath(hostname) + "/settings", null, ct);
        var result = json.Deserialize<DiagnosticSettings>(JsonOptions) ?? throw new UpdateException("Incomplete diagnostic settings.");
        if (result.Host?.Hostname != ClientSettings.NormalizeHostname(hostname) || result.Services is null)
            throw new UpdateException("IPKeep returned settings for a different hostname.");
        ValidateServices(result.Services);
        return result;
    }
    public Task<JsonElement> SaveSettingsAsync(string token, string hostname, DiagnosticService[] services, CancellationToken ct)
    { ValidateServices(services); return SendAsync(token, HttpMethod.Put, HostPath(hostname) + "/settings", new { services }, ct); }
    public Task<JsonElement> StartAsync(string token, string hostname, DiagnosticRequest request, CancellationToken ct) =>
        SendAsync(token, HttpMethod.Post, HostPath(hostname) + "/jobs", request, ct);
    public Task<JsonElement> DnsAsync(string token, string hostname, CancellationToken ct) =>
        SendAsync(token, HttpMethod.Get, HostPath(hostname) + "/dns", null, ct);
    public Task<JsonElement> HistoryAsync(string token, string hostname, CancellationToken ct) =>
        SendAsync(token, HttpMethod.Get, HostPath(hostname) + "/jobs", null, ct);
    public Task<JsonElement> ReadAsync(string token, string jobId, CancellationToken ct) => SendAsync(token, HttpMethod.Get, JobPath(jobId), null, ct);
    public Task<JsonElement> CancelAsync(string token, string jobId, CancellationToken ct) => SendAsync(token, HttpMethod.Delete, JobPath(jobId), null, ct);
    private static string JobPath(string jobId) => Guid.TryParseExact(jobId, "D", out var id) ? "jobs/" + id.ToString("D") : throw new SettingsException("Invalid diagnostic job.");
    public static readonly int[] Ports = [22, 53, 80, 443, 554, 3389, 5001, 8080, 8096, 8123, 8443, 8920, 19132, 19133, 25565, 32400, 51820];
    public static readonly string[] Types = ["tcp", "http", "https", "ssh", "minecraft", "bedrock", "rtsp"];
    public static void ValidateServices(DiagnosticService[] services)
    {
        if (services.Length > 4 || services.Select(s => s.Port).Distinct().Count() != services.Length)
            throw new SettingsException("Choose up to four services with different public ports.");
        foreach (var s in services)
        {
            if (!Types.Contains(s.Type) || !Ports.Contains(s.Port) || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 40 ||
                !System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"\A[\p{L}\p{N} ._()/-]{1,40}\z"))
                throw new SettingsException("Choose a supported test, public port and a name of 1–40 characters.");
            if (!string.IsNullOrEmpty(s.WebsiteHostname) && (s.Type is not ("http" or "https") ||
                Uri.CheckHostName(s.WebsiteHostname) != UriHostNameType.Dns || !s.WebsiteHostname.Contains('.') || s.WebsiteHostname.Length > 253 ||
                s.WebsiteHostname.Split('.').Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z"))))
                throw new SettingsException("Website hostname must be a domain name, without a URL, port or path, for HTTP/HTTPS only.");
        }
        if (2 + services.Sum(s => s.Type == "https" ? 3 : s.Type == "http" ? 2 : 1) > 8)
            throw new SettingsException("Choose fewer web services; HTTPS uses three checks per service.");
    }
    private async Task<JsonElement> SendAsync(string token, HttpMethod method, string path, object? data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ClientSettings.ValidateToken(token));
        request.Headers.UserAgent.ParseAdd("IPKeep-Windows/" + AppUpdateClient.CurrentVersion.Text);
        if (data is not null) request.Content = JsonContent.Create(data, options: JsonOptions);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The API token was rejected. Use an active token in Settings.",
                HttpStatusCode.Forbidden => "Access to these diagnostics was denied. Check the token's host access.",
                HttpStatusCode.NotFound => "This hostname or test is no longer available to this token.",
                HttpStatusCode.TooManyRequests => "Diagnostic limit reached. Wait before testing again; website and Windows tests share the same limits.",
                HttpStatusCode.Conflict => "This hostname or its settings changed. Close diagnostics and open it again.",
                HttpStatusCode.BadRequest => "IPKeep rejected these settings or observations. Check the selections and the computer clock.",
                _ => "IPKeep diagnostics could not be reached. Try again shortly."
            }, response.StatusCode == HttpStatusCode.Unauthorized, (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("Diagnostics timed out. Retry to retrieve the same test."); }
        catch (HttpRequestException) { throw new UpdateException("Cannot reach IPKeep diagnostics. Check your connection and retry."); }
        catch (JsonException) { throw new UpdateException("IPKeep returned an unreadable diagnostic response."); }
    }
}
