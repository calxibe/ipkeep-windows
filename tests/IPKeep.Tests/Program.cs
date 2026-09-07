using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPKeep.Core;
using IPKeep.Desktop;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

const string token = "ipkeep_test_token_0123456789";
int passed = 0;
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T ex) { return ex; } throw new Exception($"Expected {typeof(T).Name}"); }
ClientSettings Settings(params string[] hosts) => new() { Hostnames = hosts.Length == 0 ? ["home"] : hosts };
HttpResponseMessage Json(string content, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
string Reply(string name = "home.a.ipkeep.net", bool dns = false) => JsonSerializer.Serialize(new { success = true, changed = true, hostname = name, dnsUpdated = dns });

Test("Normalizes short and full hostnames; removes duplicates", () => Assert(Settings(" HOME ", "home.a.ipkeep.net", "office").Validate().Hostnames.SequenceEqual(new[] { "home.a.ipkeep.net", "office.a.ipkeep.net" })));
Test("Rejects custom domains, nested labels and invalid names", () =>
{
    foreach (string name in new[] { "", "evil.com", "x.home.a.ipkeep.net", "-home", "home-", "h_ome", "home.a.ipkeep.net.evil.com", "*", new string('a', 64) }) Throws<SettingsException>(() => Settings(name).Validate());
});
Test("Validates intervals and token input", () =>
{
    Throws<SettingsException>(() => (Settings() with { IntervalMinutes = 0 }).Validate());
    Throws<SettingsException>(() => (Settings() with { IntervalMinutes = 1441 }).Validate());
    Throws<SettingsException>(() => ClientSettings.ValidateToken("short"));
    Throws<SettingsException>(() => ClientSettings.ValidateToken(token + "\r\nHeader:value"));
    Assert(ClientSettings.ValidateToken("  " + token + "  ") == token);
});
Test("IPv4 and IPv6 CIDR matching preserves family and prefix", () =>
{
    Assert(IpNetwork.Parse("198.51.100.0/24").Contains(IPAddress.Parse("198.51.100.13")));
    Assert(!IpNetwork.Parse("198.51.100.0/24").Contains(IPAddress.Parse("198.51.101.13")));
    Assert(IpNetwork.Parse("2001:db8::/32").Contains(IPAddress.Parse("2001:db8:55::1")));
    Assert(!IpNetwork.Parse("0.0.0.0/0").Contains(IPAddress.IPv6Loopback));
    Throws<SettingsException>(() => IpNetwork.Parse("1.2.3.4/33")); Throws<SettingsException>(() => IpNetwork.Parse("1.2.3.*"));
});
Test("Rejects private, reserved and wrong-family resolver addresses", () =>
{
    foreach (var ip in new[] { "127.0.0.1", "192.168.0.1", "10.0.0.1", "100.64.0.1", "169.254.1.1", "224.0.0.1", "0.0.0.0", "203.0.113.1", "::1" }) Assert(!PublicIpResolver.IsPublic(IPAddress.Parse(ip), false), ip);
    foreach (var ip in new[] { "::", "::1", "fe80::1", "fd00::1", "2001:db8::1", "8.8.8.8" }) Assert(!PublicIpResolver.IsPublic(IPAddress.Parse(ip), true), ip);
    Assert(PublicIpResolver.IsPublic(IPAddress.Parse("8.8.8.8"), false)); Assert(PublicIpResolver.IsPublic(IPAddress.Parse("2606:4700:4700::1111"), true));
});
AsyncTest("Bearer token goes only to the canonical IPKeep endpoint", async () =>
{
    var log = new MemoryLog();
    using var http = new HttpClient(new FakeHttp(async request =>
    {
        Assert(request.RequestUri == IpKeepClient.UpdateEndpoint && request.Method == HttpMethod.Post);
        Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == token);
        Assert(!request.Headers.Contains("X-Auth-Key") && !request.Headers.Contains("X-Auth-Email"));
        using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(data.RootElement.GetProperty("hostname").GetString() == "home.a.ipkeep.net"); Assert(data.RootElement.GetProperty("ipv4").GetString() == "8.8.8.8");
        Assert(!data.RootElement.TryGetProperty("ipv6", out _), "Omitted IPv6 must preserve server state"); Assert(!data.RootElement.TryGetProperty("token", out _));
        return Json(Reply());
    }));
    var reply = await new IpKeepClient(http, log).UpdateAsync(token, "home", "8.8.8.8", null, default);
    Assert(reply.Changed && !reply.DnsUpdated); Assert(!string.Join('\n', log.Messages).Contains(token));
});
AsyncTest("IPv6-only updates omit IPv4 instead of clearing it", async () =>
{
    using var http = new HttpClient(new FakeHttp(async request =>
    {
        using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(!data.RootElement.TryGetProperty("ipv4", out _)); Assert(data.RootElement.GetProperty("ipv6").GetString() == "2606:4700:4700::1111"); return Json(Reply());
    }));
    await new IpKeepClient(http, new MemoryLog()).UpdateAsync(token, "home", null, "2606:4700:4700::1111", default);
});
AsyncTest("Rejects malformed and mismatched success responses", async () =>
{
    foreach (string body in new[] { "not json", "[]", "{}", "{\"success\":false}", Reply("other.a.ipkeep.net"), "{\"success\":true,\"hostname\":\"home.a.ipkeep.net\",\"changed\":true}" })
    {
        using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(body))));
        await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, new MemoryLog()).UpdateAsync(token, "home", "8.8.8.8", null, default));
    }
});
AsyncTest("Maps API failures without leaking response bodies", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
    {
        var log = new MemoryLog(); using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(token, status))));
        var error = await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, log).UpdateAsync(token, "home", "8.8.8.8", null, default));
        Assert(!error.Message.Contains(token) && !string.Join('\n', log.Messages).Contains(token));
        Assert(error.AuthenticationFailure == (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden));
    }
});
AsyncTest("Network errors and timeout details are not exposed", async () =>
{
    foreach (Exception failure in new Exception[] { new HttpRequestException(token), new TaskCanceledException(token) })
    {
        using var http = new HttpClient(new FakeHttp(_ => throw failure));
        var error = await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, new MemoryLog()).UpdateAsync(token, "home", "8.8.8.8", null, default)); Assert(!error.Message.Contains(token));
    }
});
AsyncTest("Cancellation stops requests", async () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var http = new HttpClient(new FakeHttp(_ => throw new TaskCanceledException()));
    await ThrowsAsync<OperationCanceledException>(() => new IpKeepClient(http, new MemoryLog()).UpdateAsync(token, "home", "8.8.8.8", null, cancellation.Token));
});
AsyncTest("Hostname list sends the bearer token only to GET /hosts", async () =>
{
    var log = new MemoryLog();
    using var http = new HttpClient(new FakeHttp(request =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri == IpKeepClient.HostsEndpoint && request.Content is null);
        Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == token);
        Assert(string.IsNullOrEmpty(request.RequestUri!.Query));
        return Task.FromResult(Json("{\"hosts\":[{\"hostname\":\"office.a.ipkeep.net\"},{\"hostname\":\"home.a.ipkeep.net\"},{\"hostname\":\"home.a.ipkeep.net\"}]}"));
    }));
    var hosts = await new IpKeepClient(http, log).ListHostsAsync(token, default);
    Assert(hosts.SequenceEqual(new[] { "home.a.ipkeep.net", "office.a.ipkeep.net" }));
    Assert(!string.Join('\n', log.Messages).Contains(token));
});
AsyncTest("Hostname list handles empty accounts and rejects invalid server names", async () =>
{
    using var empty = new HttpClient(new FakeHttp(_ => Task.FromResult(Json("{\"hosts\":[]}"))));
    Assert((await new IpKeepClient(empty, new MemoryLog()).ListHostsAsync(token, default)).Length == 0);
    foreach (var body in new[] { "[]", "{}", "not JSON", "{\"hosts\":null}", "{\"hosts\":[{}]}", "{\"hosts\":[{\"hostname\":42}]}", "{\"hosts\":[{\"hostname\":\"home\"}]}", "{\"hosts\":[{\"hostname\":\"home.example.com\"}]}" })
    {
        using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(body))));
        await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, new MemoryLog()).ListHostsAsync(token, default));
    }
});
AsyncTest("Hostname list rejects errors and preserves credential redaction", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
    {
        var log = new MemoryLog(); using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(token, status))));
        var error = await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, log).ListHostsAsync(token, default));
        Assert(error.AuthenticationFailure == (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden));
        Assert(!error.Message.Contains(token) && !string.Join('\n', log.Messages).Contains(token));
    }
    foreach (Exception failure in new Exception[] { new HttpRequestException(token), new TaskCanceledException(token) })
    {
        using var http = new HttpClient(new FakeHttp(_ => throw failure));
        var error = await ThrowsAsync<UpdateException>(() => new IpKeepClient(http, new MemoryLog()).ListHostsAsync(token, default));
        Assert(!error.Message.Contains(token));
    }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    using var neverSend = new HttpClient(new FakeHttp(_ => throw new Exception("Must not request")));
    await ThrowsAsync<OperationCanceledException>(() => new IpKeepClient(neverSend, new MemoryLog()).ListHostsAsync(token, cancelled.Token));
});
AsyncTest("Hostname selection requires a loaded list and cannot accept typed or another account's names", async () =>
{
    var session = new HostSelectionSession();
    Throws<SettingsException>(() => session.Select(Settings(), "home.a.ipkeep.net"));
    await session.ConnectAsync(token, new FakeHostList(_ => Task.FromResult(new[] { "home.a.ipkeep.net" })), default);
    Throws<SettingsException>(() => session.Select(Settings(), "other.a.ipkeep.net"));
    Throws<SettingsException>(() => session.Select(Settings(), "home"));
    var connection = session.Select(Settings(), "home.a.ipkeep.net");
    Assert(connection.Token == token && connection.Settings.Hostnames.Single() == "home.a.ipkeep.net");
    session.Reset(); Throws<SettingsException>(() => session.Select(Settings(), "home.a.ipkeep.net"));
});
AsyncTest("Failed token changes and stale loads cannot retain an earlier selection", async () =>
{
    var session = new HostSelectionSession();
    await session.ConnectAsync(token, new FakeHostList(_ => Task.FromResult(new[] { "home.a.ipkeep.net" })), default);
    await ThrowsAsync<UpdateException>(() => session.ConnectAsync("different_test_token", new FakeHostList(_ => throw new UpdateException("Rejected", true)), default));
    Assert(!session.IsConnected && session.Hostnames.Count == 0);
    var delayed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = session.ConnectAsync(token, new FakeHostList(_ => delayed.Task), default);
    session.Reset(); delayed.SetResult(["home.a.ipkeep.net"]);
    Assert(!await pending && !session.IsConnected && session.Hostnames.Count == 0);
});
AsyncTest("Empty hostname accounts cannot enable updates", async () =>
{
    var session = new HostSelectionSession();
    await session.ConnectAsync(token, new FakeHostList(_ => Task.FromResult(Array.Empty<string>())), default);
    Assert(session.IsConnected && session.Hostnames.Count == 0);
    Throws<SettingsException>(() => session.Select(Settings(), "home.a.ipkeep.net"));
});
AsyncTest("Discovery uses IPKeep JSON without authorization headers", async () =>
{
    int attempts = 0; using var http = new HttpClient(new FakeHttp(request =>
    {
        Assert(request.Headers.Authorization is null && request.Content is null);
        Assert(request.RequestUri == PublicIpResolver.DiscoveryEndpoint && request.Method == HttpMethod.Get);
        attempts++; return Task.FromResult(Json("{\"ipv4\":\"8.8.4.4\",\"ipv6\":null}"));
    }));
    Assert(await new PublicIpResolver(http, http, new MemoryLog()).ResolveAsync(false, default) == "8.8.4.4"); Assert(attempts == 1);
});
AsyncTest("Discovery selects separate IPv4 and IPv6 connections", async () =>
{
    int v4Calls = 0, v6Calls = 0;
    using var v4 = new HttpClient(new FakeHttp(_ => { v4Calls++; return Task.FromResult(Json("{\"ipv4\":\"8.8.8.8\",\"ipv6\":null}")); }));
    using var v6 = new HttpClient(new FakeHttp(request =>
    {
        Assert(request.Headers.Authorization is null && request.RequestUri == PublicIpResolver.DiscoveryEndpoint);
        v6Calls++; return Task.FromResult(Json("{\"ipv4\":null,\"ipv6\":\"2606:4700:4700::1111\"}"));
    }));
    var resolver = new PublicIpResolver(v4, v6, new MemoryLog());
    Assert(await resolver.ResolveAsync(false, default) == "8.8.8.8" && v4Calls == 1 && v6Calls == 0);
    Assert(await resolver.ResolveAsync(true, default) == "2606:4700:4700::1111" && v4Calls == 1 && v6Calls == 1);
});
AsyncTest("Discovery rejects null, private, malformed and wrong-family addresses", async () =>
{
    foreach (bool ipv6 in new[] { false, true })
    foreach (string body in new[] { "not json", "[]", "{}", "{\"ipv4\":null,\"ipv6\":null}", "{\"ipv4\":123,\"ipv6\":[]}", "{\"ipv4\":\"192.168.1.1\",\"ipv6\":\"fe80::1\"}", "{\"ipv4\":\"2606:4700:4700::1111\",\"ipv6\":\"8.8.8.8\"}" })
    {
        using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(body))));
        await ThrowsAsync<UpdateException>(() => new PublicIpResolver(http, http, new MemoryLog()).ResolveAsync(ipv6, default));
    }
});
AsyncTest("Discovery errors do not leak bodies or fall back to third parties", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
    {
        int calls = 0; var log = new MemoryLog();
        using var http = new HttpClient(new FakeHttp(_ => { calls++; return Task.FromResult(Json(token, status)); }));
        var error = await ThrowsAsync<UpdateException>(() => new PublicIpResolver(http, http, log).ResolveAsync(false, default));
        Assert(calls == 1 && !error.Message.Contains(token) && !string.Join('\n', log.Messages).Contains(token));
    }
});
AsyncTest("Discovery handles network failures, timeouts and caller cancellation", async () =>
{
    foreach (Exception failure in new Exception[] { new HttpRequestException(token), new TaskCanceledException(token) })
    {
        using var http = new HttpClient(new FakeHttp(_ => throw failure));
        var error = await ThrowsAsync<UpdateException>(() => new PublicIpResolver(http, http, new MemoryLog()).ResolveAsync(false, default));
        Assert(!error.Message.Contains(token));
    }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    using var cancelledHttp = new HttpClient(new FakeHttp(_ => throw new Exception("Must not send")));
    await ThrowsAsync<OperationCanceledException>(() => new PublicIpResolver(cancelledHttp, cancelledHttp, new MemoryLog()).ResolveAsync(false, cancellation.Token));
});
AsyncTest("Discovery sockets use the requested address family on real loopback connections", async () =>
{
    foreach (bool ipv6 in new[] { false, true })
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        if (ipv6) listener.Server.DualMode = false;
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            Assert(connection.Client.RemoteEndPoint!.AddressFamily == (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork));
            using var stream = connection.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), timeout.Token);
        }, timeout.Token);
        using var http = NetworkClients.CreateDiscovery(ipv6);
        Assert(await http.GetStringAsync($"http://localhost:{port}/ip", timeout.Token) == "OK"); await serve;
    }
});
Test("Lookup selection defaults for existing settings and survives serialization", () =>
{
    var old = JsonSerializer.Deserialize<ClientSettings>("{\"Hostnames\":[\"home.a.ipkeep.net\"]}")!.Validate();
    Assert(old.IpLookupProviderId == IpLookupProviders.DefaultId);
    foreach (var provider in IpLookupProviders.All)
    {
        var settings = (Settings() with { IpLookupProviderId = provider.Id }).Validate();
        Assert(JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(settings))!.Validate().IpLookupProviderId == provider.Id);
        Assert(provider.IPv4Endpoint.Scheme == "https" && (provider.IPv6Endpoint is null || provider.IPv6Endpoint.Scheme == "https"));
    }
    Throws<SettingsException>(() => (Settings() with { IpLookupProviderId = "https://unexpected.example/" }).Validate());
    Throws<SettingsException>(() => (Settings() with { IpLookupProviderId = "amazon", EnableIPv6 = true }).Validate());
});
AsyncTest("Each lookup provider uses its own anonymous IPv4 and IPv6 endpoint", async () =>
{
    foreach (var provider in IpLookupProviders.All)
    foreach (bool ipv6 in new[] { false, true })
    {
        int calls = 0;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            calls++;
            Assert(request.Method == HttpMethod.Get && request.Headers.Authorization is null && request.Content is null);
            Assert(request.RequestUri == (ipv6 ? provider.IPv6Endpoint : provider.IPv4Endpoint));
            Assert(request.Headers.CacheControl?.NoCache == true && request.Headers.CacheControl.NoStore);
            string address = ipv6 ? "2606:4700:4700::1111" : "8.8.4.4";
            return Task.FromResult(Json(provider.ReturnsFamilyJson ? JsonSerializer.Serialize(new Dictionary<string, string> { [ipv6 ? "ipv6" : "ipv4"] = address }) : "  " + address + "\n"));
        }));
        var resolver = new PublicIpResolver(http, http, new MemoryLog());
        if (ipv6 && !provider.SupportsIPv6)
        { await ThrowsAsync<UpdateException>(() => resolver.ResolveAsync(ipv6, default, provider.Id)); Assert(calls == 0); }
        else
        { Assert(await resolver.ResolveAsync(ipv6, default, provider.Id) == (ipv6 ? "2606:4700:4700::1111" : "8.8.4.4")); Assert(calls == 1); }
    }
});
AsyncTest("External lookup replies reject invalid and wrong-family addresses", async () =>
{
    foreach (var provider in IpLookupProviders.All.Where(p => !p.ReturnsFamilyJson))
    foreach (string body in new[] { "", "<html>error</html>", "8.8.8.8\n9.9.9.9", "192.168.1.1", "2606:4700:4700::1111", "{\"ip\":\"8.8.8.8\"}" })
    {
        using var http = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(body))));
        await ThrowsAsync<UpdateException>(() => new PublicIpResolver(http, http, new MemoryLog()).ResolveAsync(false, default, provider.Id));
    }
});
AsyncTest("Selected provider failures never fall back or expose response content", async () =>
{
    foreach (var provider in IpLookupProviders.All)
    foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
    {
        int calls = 0; var log = new MemoryLog();
        using var http = new HttpClient(new FakeHttp(request => { calls++; Assert(request.RequestUri == provider.IPv4Endpoint); return Task.FromResult(Json(token, status)); }));
        var error = await ThrowsAsync<UpdateException>(() => new PublicIpResolver(http, http, log).ResolveAsync(false, default, provider.Id));
        Assert(calls == 1 && !error.Message.Contains(token) && !string.Join('\n', log.Messages).Contains(token));
    }
});
AsyncTest("Saved provider supplies update addresses while the token stays on IPKeep", async () =>
{
    var settings = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(Settings() with { IpLookupProviderId = "amazon" }))!;
    int lookups = 0, updates = 0;
    using var discovery = new HttpClient(new FakeHttp(request =>
    {
        lookups++; Assert(request.RequestUri == IpLookupProviders.Get("amazon").IPv4Endpoint && request.Headers.Authorization is null);
        return Task.FromResult(Json("9.9.9.9\n"));
    }));
    using var update = new HttpClient(new FakeHttp(async request =>
    {
        updates++; Assert(request.RequestUri == IpKeepClient.UpdateEndpoint && request.Headers.Authorization?.Parameter == token);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert(body.RootElement.GetProperty("ipv4").GetString() == "9.9.9.9" && !body.RootElement.TryGetProperty("ipv6", out _));
        return Json("{\"success\":true,\"hostname\":\"home.a.ipkeep.net\",\"changed\":true,\"dnsUpdated\":true}");
    }));
    var log = new MemoryLog();
    var result = await new CheckEngine(new PublicIpResolver(discovery, discovery, log), new IpKeepClient(update, log), log).RunAsync(settings, token, default);
    Assert(result.Success && lookups == 1 && updates == 1 && result.IPv4 == "9.9.9.9");
});
AsyncTest("Provider comparison reports fast results while slow and failed providers are independent", async () =>
{
    var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var seen = new List<string>();
    var resolver = new ProviderResolver((ipv6, id, _) =>
    {
        Assert(!ipv6); seen.Add(id);
        return id switch { "ipkeep" => slow.Task, "ipify" => throw new UpdateException("Cannot reach ipify."), _ => Task.FromResult("8.8.8.8") };
    });
    var session = new IpLookupProbeSession(); var pending = session.RefreshAsync(resolver, default);
    Assert(seen.Count == IpLookupProviders.All.Count && !pending.IsCompleted);
    Assert(session.Choices.Single(p => p.Provider.Id == "ipify").Error is not null);
    Assert(session.Choices.Single(p => p.Provider.Id == "amazon").IPv4 == "8.8.8.8");
    slow.SetResult("9.9.9.9"); await pending;
    Assert(session.Choices[0].IPv4 == "9.9.9.9");
});
AsyncTest("Reopening provider comparison discards stale results and closing cancels requests", async () =>
{
    var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new IpLookupProbeSession();
    var first = session.RefreshAsync(new ProviderResolver((_, _, _) => delayed.Task), default);
    await session.RefreshAsync(new ProviderResolver((_, _, _) => Task.FromResult("9.9.9.9")), default);
    delayed.SetResult("8.8.8.8"); await first;
    Assert(session.Choices.All(p => p.IPv4 == "9.9.9.9"));
    int cancelled = 0;
    var last = session.RefreshAsync(new ProviderResolver(async (_, _, ct) =>
    { try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "8.8.8.8"; } catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; } }), default);
    session.Cancel(); await last;
    Assert(cancelled == IpLookupProviders.All.Count && session.Choices.All(p => p.IPv4 is null));
});
AsyncTest("DNS pending stays visible; never claims published DNS", async () =>
{
    var client = new FakeUpdater((_, _, _) => Task.FromResult(new UpdateReply(true, false)));
    var result = await new CheckEngine(new FakeResolver(), client, new MemoryLog()).RunAsync(Settings(), token, default);
    Assert(result.Success && result.Message.Contains("pending")); Assert(!result.Hosts[0].DnsUpdated && result.Hosts[0].Message.Contains("pending"));
});
AsyncTest("Partial host failures do not stop remaining host updates", async () =>
{
    var client = new FakeUpdater((name, _, _) => name.StartsWith("bad.") ? throw new UpdateException("Host missing") : Task.FromResult(new UpdateReply(true, true)));
    var result = await new CheckEngine(new FakeResolver(), client, new MemoryLog()).RunAsync(Settings("good", "bad", "last"), token, default);
    Assert(!result.Success && result.Hosts.Length == 3 && result.Hosts[2].Success); Assert(client.Calls == 3 && CheckSchedule.Delay(result.Success, 360) == TimeSpan.FromMinutes(5));
});
AsyncTest("Authentication rejection stops redundant requests", async () =>
{
    var client = new FakeUpdater((_, _, _) => throw new UpdateException("Invalid token", true));
    var result = await new CheckEngine(new FakeResolver(), client, new MemoryLog()).RunAsync(Settings("home", "office"), token, default);
    Assert(!result.Success && client.Calls == 1 && result.Hosts.Length == 2);
});
AsyncTest("Ignored addresses skip requests and use normal interval", async () =>
{
    var client = new FakeUpdater((_, _, _) => throw new Exception("Must not call API"));
    var result = await new CheckEngine(new FakeResolver(), client, new MemoryLog()).RunAsync(Settings() with { IgnoredNetworks = ["8.8.8.0/24"] }, token, default);
    Assert(result.Success && result.Skipped && client.Calls == 0); Assert(CheckSchedule.Delay(result.Success, 360) == TimeSpan.FromHours(6));
});
AsyncTest("Missing IPv6 retries while preserving IPv4 updates", async () =>
{
    var resolver = new FakeResolver { Handler = ipv6 => ipv6 ? throw new UpdateException("No IPv6") : Task.FromResult("8.8.8.8") };
    var client = new FakeUpdater((_, v4, v6) => { Assert(v4 == "8.8.8.8" && v6 is null); return Task.FromResult(new UpdateReply(true, false)); });
    var result = await new CheckEngine(resolver, client, new MemoryLog()).RunAsync(Settings() with { EnableIPv6 = true }, token, default); Assert(!result.Success && result.Hosts[0].Success);
});
AsyncTest("Ignored IPv4 does not suppress usable IPv6", async () =>
{
    var client = new FakeUpdater((_, v4, v6) => { Assert(v4 is null && v6 is not null); return Task.FromResult(new UpdateReply(true, true)); });
    var result = await new CheckEngine(new FakeResolver(), client, new MemoryLog()).RunAsync(Settings() with { EnableIPv6 = true, IgnoredNetworks = ["8.8.8.8"] }, token, default); Assert(result.Success && result.Skipped && result.IPv4 is null && result.IPv6 is not null);
});
AsyncTest("Checks never overlap; cancelled waiters release cleanly", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int current = 0, max = 0;
    var client = new FakeUpdater(async (_, _, _) => { current++; max = Math.Max(max, current); entered.TrySetResult(); await release.Task; current--; return new(true, true); });
    var engine = new CheckEngine(new FakeResolver(), client, new MemoryLog()); var first = engine.RunAsync(Settings(), token, default); await entered.Task;
    using var cancellation = new CancellationTokenSource(); var cancelled = engine.RunAsync(Settings(), token, cancellation.Token); cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => cancelled);
    var second = engine.RunAsync(Settings(), token, default); Assert(client.Calls == 1); release.SetResult(); await Task.WhenAll(first, second); Assert(max == 1 && client.Calls == 2);
});
Test("Activity converts event and message timestamps to local time across dates", () =>
{
    var zone = TimeZoneInfo.CreateCustomTimeZone("Test West", TimeSpan.FromHours(-7), "Test West", "Test West");
    string raw = "2026-09-07 01:30:00 +02:00  INFO   Next check: 2026-09-07 07:30:00 +02:00; consecutive_failures=0.\r";
    var row = ActivityRow.Parse(raw, zone, CultureInfo.InvariantCulture);
    Assert(row.TimestampText == "06 Sep, 16:30:00");
    Assert(row.TimestampDetails == "2026-09-06 16:30:00 -07:00 (Test West)");
    Assert(row.Level == "INFO" && row.Message == "Next check: 06 Sep, 22:30:00; consecutive_failures=0.");
    Assert(row.RawLine == raw.TrimEnd('\r'));
    Assert(row.Matches("06 Sep, 16:30") && row.Matches("06 Sep, 22:30") && row.Matches("+02:00") && row.Matches("info"));
    Assert(!row.Matches("No match"));
});
Test("Activity uses the offset at the event time across daylight saving changes", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    var summer = ActivityRow.Parse("2026-07-01 12:00:00 +00:00  WARN   Example", zone, CultureInfo.InvariantCulture);
    var winter = ActivityRow.Parse("2026-01-01 12:00:00 +00:00  ERROR  Example", zone, CultureInfo.InvariantCulture);
    Assert(summer.TimestampText == "01 Jul, 14:00:00" && winter.TimestampText == "01 Jan, 13:00:00");
    var first = ActivityRow.Parse("2026-10-25 00:30:00 +00:00  INFO   Before fallback", zone, CultureInfo.InvariantCulture);
    var second = ActivityRow.Parse("2026-10-25 01:30:00 +00:00  INFO   After fallback", zone, CultureInfo.InvariantCulture);
    Assert(first.TimestampText == second.TimestampText);
    Assert(first.TimestampDetails.Contains("+02:00") && second.TimestampDetails.Contains("+01:00"));
});
Test("Activity retains malformed and incomplete log lines without inventing timestamps", () =>
{
    foreach (string raw in new[] { "Partial log entry", "2026-99-07 07:30:00 +02:00  INFO   Invalid date", "2026-09-07 07:30:00 +02:00", "" })
    {
        var row = ActivityRow.Parse(raw);
        Assert(row.TimestampText == "—" && row.Level == "—" && row.Message == raw);
    }
    var valid = ActivityRow.Parse("2026-09-07 07:30:00 +02:00  INFO   IPv6: 2001:db8:42::24; invalid date: 2026-99-07 07:30:00 +02:00.", TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
    Assert(valid.Message == "IPv6: 2001:db8:42::24; invalid date: 2026-99-07 07:30:00 +02:00.");
});

Test("Logs redact secrets, strip injected lines, rotate and read newest first", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "IPKeep-log-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try
    {
        string path = Path.Combine(directory, "service.log"); var log = new ActivityLog(path, 160); log.SetSecret(token); log.Write("INFO", token + "\r\nInjected");
        Assert(!File.ReadAllText(path).Contains(token) && File.ReadAllLines(path).Length == 1);
        for (int i = 0; i < 35; i++) log.Write("INFO", $"Event {i}: " + new string('x', 100));
        Assert(Directory.GetFiles(directory).Length <= 6); Assert(ActivityLog.ReadTail(path)[0].Contains("Event 34"));
    }
    finally { Directory.Delete(directory, true); }
});
Test("Atomic public settings saves contain no token", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "IPKeep-file-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try
    {
        string path = Path.Combine(directory, "settings.json"); FileStore.WriteJson(path, Settings("home").Validate()); FileStore.WriteJson(path, Settings("office").Validate());
        Assert(FileStore.ReadJson<ClientSettings>(path)!.Hostnames[0] == "office.a.ipkeep.net"); Assert(!File.ReadAllText(path).Contains("token", StringComparison.OrdinalIgnoreCase)); Assert(Directory.GetFiles(directory).Length == 1);
    }
    finally { Directory.Delete(directory, true); }
});
Test("Windows encryption round-trips and rejects tampered ciphertext", () =>
{
    byte[] bytes = Encoding.UTF8.GetBytes(token); byte[] cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
    Assert(!Encoding.UTF8.GetString(cipher).Contains(token)); Assert(ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine).SequenceEqual(bytes));
    cipher[^1] ^= 0xff; Throws<CryptographicException>(() => ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine));
});
Test("Desktop token persists with current-user encryption and private file permissions", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "IPKeep-user-token-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "token.bin");
    try
    {
        var store = new UserTokenStore(path);
        Assert(store.Load() is null);
        store.Save(token);
        Assert(new UserTokenStore(path).Load() == token);
        byte[] cipher = File.ReadAllBytes(path);
        Assert(!Encoding.UTF8.GetString(cipher).Contains(token));
        byte[] entropy = Encoding.UTF8.GetBytes("IPKeep.Windows.UserToken.v1");
        Assert(Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser)) == token);
        Throws<CryptographicException>(() => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
        var owner = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (FileSystemAccessRule rule in new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Assert(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(owner) || rule.IdentityReference.Equals(system));
        Assert(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
        store.Save(token + "_replacement");
        Assert(new UserTokenStore(path).Load() == token + "_replacement" && Directory.GetFiles(directory).Length == 1);
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Test("Desktop token rejects corruption and invalid replacement without losing the saved token", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "IPKeep-user-token-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "token.bin");
    try
    {
        var store = new UserTokenStore(path); store.Save(token);
        Throws<SettingsException>(() => store.Save("short")); Assert(store.Load() == token);
        byte[] cipher = File.ReadAllBytes(path); cipher[^1] ^= 0xff; File.WriteAllBytes(path, cipher);
        Throws<CryptographicException>(() => store.Load());
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
AsyncTest("Reopened desktop uses its remembered token to refresh hostname choices", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "IPKeep-user-token-" + Guid.NewGuid().ToString("N"));
    try
    {
        string path = Path.Combine(directory, "token.bin"); new UserTokenStore(path).Save(token);
        string restored = new UserTokenStore(path).Load()!;
        string response = "{\"hosts\":[{\"hostname\":\"home.a.ipkeep.net\"},{\"hostname\":\"office.a.ipkeep.net\"}]}";
        int requests = 0;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == "https://api.ipkeep.net/hosts");
            Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == restored);
            Assert(request.Content is null); requests++;
            return Task.FromResult(Json(response));
        }));
        var session = new HostSelectionSession(); var client = new IpKeepClient(http, new MemoryLog());
        Assert(await session.ConnectAsync(restored, client, default));
        Assert(session.Select(Settings(), "home.a.ipkeep.net").Token == token && session.Hostnames.Count == 2);
        response = "{\"hosts\":[{\"hostname\":\"office.a.ipkeep.net\"}]}";
        Assert(await session.ConnectAsync(restored, client, default) && requests == 2);
        Throws<SettingsException>(() => session.Select(Settings(), "home.a.ipkeep.net"));
        Assert(session.Hostnames.Single() == "office.a.ipkeep.net");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Test("Saved-token migration refuses another Windows account and unexpected arguments", () =>
{
    Assert(SavedTokenRestore.Run([SavedTokenRestore.Argument, "S-1-0-0"]) == 2);
    Assert(SavedTokenRestore.Run([SavedTokenRestore.Argument, WindowsIdentity.GetCurrent().User!.Value, "extra"]) == 2);
});
Test("Runtime guard refuses to run a service from the workspace", () => Throws<UnauthorizedAccessException>(DeploymentSecurity.ValidateRuntime));

if (args.Contains("--live-ipv4")) AsyncTest("Live anonymous IPv4 discovery from IPKeep over HTTPS", async () =>
{
    using var v4 = NetworkClients.CreateDiscovery(false); using var v6 = NetworkClients.CreateDiscovery(true);
    string address = await new PublicIpResolver(v4, v6, new MemoryLog()).ResolveAsync(false, default);
    Assert(PublicIpResolver.IsPublic(IPAddress.Parse(address), false));
});

if (args.Contains("--live-providers")) AsyncTest("Live anonymous IPv4 comparison across all lookup services", async () =>
{
    using var http = NetworkClients.CreateDiscovery(false, TimeSpan.FromSeconds(10));
    var session = new IpLookupProbeSession();
    await session.RefreshAsync(new PublicIpResolver(http, http, new MemoryLog()), default);
    foreach (var choice in session.Choices)
    {
        Console.WriteLine($"  {choice.DisplayName}: {choice.ResultText}");
        Assert(choice.IPv4 is not null && PublicIpResolver.IsPublic(IPAddress.Parse(choice.IPv4), false));
    }
});

foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + " " + ex.Message); Environment.ExitCode = 1; }
}
Console.WriteLine($"{passed}/{tests.Count} tests passed. No installed services or authenticated update APIs were changed.");

sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return handler(request); } }
sealed class MemoryLog : IActivityLog
{ public List<string> Messages { get; } = []; public void Write(string level, string message) => Messages.Add(level + " " + message); }
sealed class FakeResolver : IPublicIpResolver
{
    public Func<bool, Task<string>> Handler { get; init; } = ipv6 => Task.FromResult(ipv6 ? "2606:4700:4700::1111" : "8.8.8.8");
    public string? LastProviderId { get; private set; }
    public Task<string> ResolveAsync(bool ipv6, CancellationToken cancellationToken, string providerId = IpLookupProviders.DefaultId) { LastProviderId = providerId; return Handler(ipv6); }
}
sealed class FakeUpdater(Func<string, string?, string?, Task<UpdateReply>> handler) : IUpdateClient
{
    public int Calls { get; private set; }
    public Task<UpdateReply> UpdateAsync(string token, string hostname, string? ipv4, string? ipv6, CancellationToken cancellationToken) { Calls++; return handler(hostname, ipv4, ipv6); }
}
sealed class ProviderResolver(Func<bool, string, CancellationToken, Task<string>> handler) : IPublicIpResolver
{
    public Task<string> ResolveAsync(bool ipv6, CancellationToken cancellationToken, string providerId = IpLookupProviders.DefaultId) => handler(ipv6, providerId, cancellationToken);
}
sealed class FakeHostList(Func<string, Task<string[]>> handler) : IHostListClient
{
    public Task<string[]> ListHostsAsync(string token, CancellationToken cancellationToken) => handler(token);
}
