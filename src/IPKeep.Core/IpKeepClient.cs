using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace IPKeep.Core;

public interface IActivityLog { void Write(string level, string message); }
public interface IPublicIpResolver { Task<string> ResolveAsync(bool ipv6, CancellationToken cancellationToken, string providerId = IpLookupProviders.DefaultId); }
public interface IUpdateClient { Task<UpdateReply> UpdateAsync(string token, string hostname, string? ipv4, string? ipv6, CancellationToken cancellationToken); }
public interface IHostListClient { Task<string[]> ListHostsAsync(string token, CancellationToken cancellationToken); }
public sealed record UpdateReply(bool Changed, bool DnsUpdated);
public sealed class UpdateException(string message, bool authenticationFailure = false) : Exception(message)
{
    public bool AuthenticationFailure { get; } = authenticationFailure;
}

public static class NetworkClients
{
    // Do not let a redirect move a credential-bearing request to another endpoint.
    public static HttpClient Create(int maximumResponseBytes = 65536) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = maximumResponseBytes };

    public static HttpClient CreateDiscovery(bool ipv6, TimeSpan? timeout = null) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        // A proxy would report its own address and can change the requested IP family.
        UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var family = ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, cancellationToken);
            var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (ipv6) socket.DualMode = false;
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = timeout ?? TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 65536 };
}

public sealed class IpKeepClient(HttpClient http, IActivityLog log) : IUpdateClient, IHostListClient
{
    public static readonly Uri UpdateEndpoint = new("https://api.ipkeep.net/update");
    public static readonly Uri HostsEndpoint = new("https://api.ipkeep.net/hosts");

    public async Task<string[]> ListHostsAsync(string token, CancellationToken cancellationToken)
    {
        token = ClientSettings.ValidateToken(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, HostsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("IPKeep-Windows/1.0");
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            log.Write("INFO", $"IPKeep host list: http_status={(int)response.StatusCode}; elapsed_ms={watch.ElapsedMilliseconds}.");
            if (!response.IsSuccessStatusCode) throw new UpdateException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The token was rejected. Paste an active API token from the IPKeep admin panel.",
                HttpStatusCode.TooManyRequests => "Too many requests. Wait a moment, then load your hostnames again.",
                _ => "IPKeep could not load your hostnames. Try again shortly."
            }, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Array)
                throw new UpdateException("IPKeep returned an incomplete hostname list. Try loading it again.");
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in hosts.EnumerateArray())
            {
                if (host.ValueKind != JsonValueKind.Object || !host.TryGetProperty("hostname", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new UpdateException("IPKeep returned an invalid hostname list. Try loading it again.");
                string value = name.GetString()!;
                if (ClientSettings.NormalizeHostname(value) != value) throw new SettingsException("Invalid hostname response.");
                result.Add(value);
            }
            return result.Order(StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateException("Loading hostnames timed out. Check your internet connection and try again."); }
        catch (HttpRequestException) { throw new UpdateException("Cannot reach IPKeep to load hostnames. Check your internet connection."); }
        catch (JsonException) { throw new UpdateException("IPKeep returned unreadable hostname data. Try again shortly."); }
        catch (SettingsException) { throw new UpdateException("IPKeep returned an invalid hostname list. Try loading it again."); }
    }

    public async Task<UpdateReply> UpdateAsync(string token, string hostname, string? ipv4, string? ipv6, CancellationToken cancellationToken)
    {
        token = ClientSettings.ValidateToken(token);
        hostname = ClientSettings.NormalizeHostname(hostname);
        if (ipv4 is null && ipv6 is null) throw new UpdateException("No eligible public IP address is available.");
        var body = new Dictionary<string, string> { ["hostname"] = hostname };
        if (ipv4 is not null) body["ipv4"] = ipv4;
        if (ipv6 is not null) body["ipv6"] = ipv6;
        using var request = new HttpRequestMessage(HttpMethod.Post, UpdateEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("IPKeep-Windows/1.0");
        request.Content = JsonContent.Create(body);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            log.Write("INFO", $"IPKeep update: host={hostname}; http_status={(int)response.StatusCode}; elapsed_ms={watch.ElapsedMilliseconds}.");
            if (!response.IsSuccessStatusCode) throw new UpdateException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The IPKeep token was rejected. Create or enable a token on the website, then save it in Settings.",
                HttpStatusCode.NotFound => "The hostname is missing, paused, or belongs to another account. Check it on the IPKeep website.",
                HttpStatusCode.TooManyRequests => "IPKeep is receiving too many requests. The service will retry later.",
                HttpStatusCode.BadRequest => "IPKeep could not accept this hostname or IP address. Check your settings.",
                _ => "IPKeep is unavailable or returned an unexpected response. The service will retry later."
            }, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("hostname", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != hostname ||
                !root.TryGetProperty("changed", out var changed) || changed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("dnsUpdated", out var dns) || dns.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new UpdateException("IPKeep returned an incomplete response. The service will retry later.");
            return new(changed.GetBoolean(), dns.GetBoolean());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateException("The IPKeep request timed out. The service will retry later."); }
        catch (HttpRequestException) { throw new UpdateException("Cannot reach IPKeep. Check the internet connection."); }
        catch (JsonException) { throw new UpdateException("IPKeep returned unreadable data. The service will retry later."); }
    }
}

public sealed class PublicIpResolver(HttpClient ipv4Http, HttpClient ipv6Http, IActivityLog log) : IPublicIpResolver
{
    public static readonly Uri DiscoveryEndpoint = IpLookupProviders.Get(IpLookupProviders.DefaultId).IPv4Endpoint;

    public async Task<string> ResolveAsync(bool ipv6, CancellationToken cancellationToken, string providerId = IpLookupProviders.DefaultId)
    {
        var provider = IpLookupProviders.Get(providerId);
        var endpoint = ipv6 ? provider.IPv6Endpoint : provider.IPv4Endpoint;
        if (endpoint is null) throw new UpdateException($"{provider.Name} supports IPv4 only. Choose another lookup service to use IPv6.");
        var watch = Stopwatch.StartNew();
        string family = ipv6 ? "ipv6" : "ipv4";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("IPKeep-Windows/1.0");
            request.Headers.Accept.ParseAdd(provider.ReturnsFamilyJson ? "application/json" : "text/plain");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await (ipv6 ? ipv6Http : ipv4Http).SendAsync(request, cancellationToken);
            log.Write("INFO", $"IP lookup: provider={provider.Name}; endpoint={endpoint}; family={family}; http_status={(int)response.StatusCode}; elapsed_ms={watch.ElapsedMilliseconds}.");
            if (!response.IsSuccessStatusCode) throw new UpdateException($"{provider.Name} could not look up your public IPv{(ipv6 ? 6 : 4)} address. Try again later.");
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            string? address = body.Trim();
            if (provider.ReturnsFamilyJson)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                address = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(family, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            if (IPAddress.TryParse(address, out var ip) && IsPublic(ip, ipv6))
            {
                log.Write("INFO", $"Public IPv{(ipv6 ? 6 : 4)}: source={endpoint.Host}; address={ip}.");
                return ip.ToString();
            }
            throw new UpdateException($"{provider.Name} did not return a valid public IPv{(ipv6 ? 6 : 4)} address.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateException($"The {provider.Name} IPv{(ipv6 ? 6 : 4)} lookup timed out. Check your connection."); }
        catch (HttpRequestException) { throw new UpdateException($"Cannot reach {provider.Name} over IPv{(ipv6 ? 6 : 4)}. Check your connection."); }
        catch (JsonException) { throw new UpdateException($"{provider.Name} returned unreadable IP address data. Try again later."); }
    }

    public static bool IsPublic(IPAddress ip, bool ipv6)
    {
        if (ip.AddressFamily != (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) || IPAddress.IsLoopback(ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (ipv6) return (bytes[0] & 0xe0) == 0x20 && !IpNetwork.Parse("2001:db8::/32").Contains(ip);
        return !new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3" }.Any(x => IpNetwork.Parse(x).Contains(ip));
    }
}
