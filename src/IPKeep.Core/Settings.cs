using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IPKeep.Core;

public sealed record ClientSettings
{
    public string[] Hostnames { get; init; } = [];
    public int IntervalMinutes { get; init; } = 360;
    public bool EnableIPv6 { get; init; }
    public string IpLookupProviderId { get; init; } = IpLookupProviders.DefaultId;
    public string[] IgnoredNetworks { get; init; } = [];

    public ClientSettings Validate()
    {
        if (Hostnames is null || Hostnames.Length is < 1 or > 20)
            throw new SettingsException("Enter between 1 and 20 hostnames created on the IPKeep website.");
        if (IntervalMinutes is < 1 or > 1440)
            throw new SettingsException("Choose a check interval between 1 and 1,440 minutes.");
        if (IgnoredNetworks is null || IgnoredNetworks.Length > 100)
            throw new SettingsException("Enter at most 100 ignored IP addresses or CIDR ranges.");
        var provider = IpLookupProviders.Get(IpLookupProviderId);
        if (EnableIPv6 && !provider.SupportsIPv6)
            throw new SettingsException("This IP lookup service supports IPv4 only. Turn off IPv6 updates or choose another service.");
        var names = Hostnames.Select(NormalizeHostname).Distinct().ToArray();
        foreach (var network in IgnoredNetworks) IpNetwork.Parse(network);
        return this with { Hostnames = names, IgnoredNetworks = IgnoredNetworks.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() };
    }

    public static string NormalizeHostname(string input)
    {
        var name = (input ?? "").Trim().ToLowerInvariant();
        const string suffix = ".a.ipkeep.net";
        var label = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
        if (!Regex.IsMatch(label, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant))
            throw new SettingsException("Use a hostname such as home or home.a.ipkeep.net. Custom domains are not supported.");
        return label + suffix;
    }

    public static string ValidateToken(string token)
    {
        token = token.Trim();
        if (token.Length is < 16 or > 2048 || token.Any(c => c < 33 || c > 126))
            throw new SettingsException("Paste the complete API token created on the IPKeep website.");
        return token;
    }
}

public sealed class SettingsException(string message) : Exception(message);

public sealed record IpNetwork(IPAddress Address, int PrefixLength)
{
    public static IpNetwork Parse(string text)
    {
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.ScopeIdOrZero() != 0)
            throw new SettingsException("An ignored network is invalid. Use an IP address or CIDR, such as 198.51.100.0/24.");
        int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = bits;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits))
            throw new SettingsException("An ignored network has an invalid CIDR prefix.");
        return new(address, prefix);
    }

    public bool Contains(IPAddress ip)
    {
        if (ip.AddressFamily != Address.AddressFamily) return false;
        var a = Address.GetAddressBytes(); var b = ip.GetAddressBytes();
        for (int bit = 0; bit < PrefixLength; bit++)
            if ((a[bit / 8] & (1 << (7 - bit % 8))) != (b[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }
}

internal static class AddressExtensions
{
    public static long ScopeIdOrZero(this IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
}
