using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPKeep.Core;

public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly Regex Format = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
    private readonly string[] numbers;
    private readonly string[] prerelease;
    public string Text { get; }
    public bool IsPreview => prerelease.Length > 0;

    private ReleaseVersion(string text, Match match)
    {
        Text = text.Split('+')[0];
        numbers = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
    }

    public static ReleaseVersion Parse(string text)
    {
        if (text.Length > 200) throw new FormatException("Invalid release version.");
        var match = Format.Match(text);
        if (!match.Success || match.Groups[4].Value.Split('.').Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0'))
            throw new FormatException("Invalid release version.");
        return new(text, match);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < numbers.Length; i++)
        {
            int order = CompareNumber(numbers[i], other.numbers[i]);
            if (order != 0) return order;
        }
        if (!IsPreview || !other.IsPreview) return other.IsPreview.CompareTo(IsPreview);
        for (int i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            string left = prerelease[i], right = other.prerelease[i];
            bool leftNumeric = IsNumeric(left), rightNumeric = IsNumeric(right);
            int order = leftNumeric && rightNumeric ? CompareNumber(left, right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
            if (order != 0) return order;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static int CompareNumber(string left, string right) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}

public sealed record AppRelease(ReleaseVersion Version, DateTimeOffset ReleaseDate, string ReleaseNotes);
public sealed record AppUpdateResult(AppRelease? NewRelease, bool HasPublishedRelease, bool CheckIncomplete);

public sealed class AppUpdateClient(HttpClient http)
{
    public static readonly Uri StableEndpoint = new("https://api.ipkeep.net/version");
    public static readonly Uri PreviewEndpoint = new("https://api.ipkeep.net/version?channel=preview");
    public static readonly Uri ReleasesPage = new("https://github.com/calxibe/ipkeep-windows/releases");
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static ReleaseVersion CurrentVersion { get; } = ReleaseVersion.Parse(
        typeof(AppUpdateClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    // This client is separate from authenticated hostname/DNS requests. It has no token input.
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16384 };

    public async Task<AppUpdateResult> CheckAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        string[] channels = current.IsPreview ? ["stable", "preview"] : ["stable"];
        var results = await Task.WhenAll(channels.Select(channel => ReadChannelAsync(channel, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        var published = results.Where(result => result.Release is not null).Select(result => result.Release!).ToArray();
        var newer = published.Where(release => release.Version.CompareTo(current) > 0).OrderByDescending(release => release.Version).FirstOrDefault();
        return new(newer, published.Length > 0, results.Any(result => result.Failed));
    }

    private async Task<(AppRelease? Release, bool Failed)> ReadChannelAsync(string channel, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, channel == "preview" ? PreviewEndpoint : StableEndpoint);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("IPKeep-Windows/" + CurrentVersion.Text);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound)) return (null, true);
            if (response.Content.Headers.ContentType?.MediaType != "application/json") return (null, true);
            // Enforce the bound even for injected clients without the production buffer limit.
            await response.Content.LoadIntoBufferAsync(16384, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken), new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, true);
            var fields = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) return (null, true);
            string? Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Field("app") != "ipkeep-windows" || Field("channel") != channel) return (null, true);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, Field("code") != "NO_RELEASE");
            var version = ReleaseVersion.Parse(Field("version") ?? "");
            if (version.IsPreview != (channel == "preview")) return (null, true);
            if (!DateTimeOffset.TryParseExact(Field("releaseDate"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return (null, true);
            string? notes = Field("releaseNotes");
            if (string.IsNullOrWhiteSpace(notes) || notes.Length > 1000 || notes.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                return (null, true);
            return (new(version, date, notes), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, true); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException)
        { return (null, true); }
    }
}
