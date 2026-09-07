using System.ComponentModel;

namespace IPKeep.Core;

public sealed record IpLookupProvider(string Id, string Name, Uri IPv4Endpoint, Uri? IPv6Endpoint, bool ReturnsFamilyJson = false)
{
    public bool SupportsIPv6 => IPv6Endpoint is not null;
    public string DisplayName => Id == IpLookupProviders.DefaultId ? Name + " (default)" : Name;
}

public static class IpLookupProviders
{
    public const string DefaultId = "ipkeep";
    // Add future providers here with stable IDs. Never accept arbitrary URLs from saved settings.
    public static IReadOnlyList<IpLookupProvider> All { get; } = Array.AsReadOnly(new[]
    {
        new IpLookupProvider(DefaultId, "IPKeep", new("https://api.ipkeep.net/ip"), new("https://api.ipkeep.net/ip"), true),
        new IpLookupProvider("amazon", "Amazon Check IP", new("https://checkip.amazonaws.com/"), null),
        new IpLookupProvider("ipify", "ipify", new("https://api.ipify.org/"), new("https://api6.ipify.org/")),
        new IpLookupProvider("ident-me", "ident.me", new("https://4.ident.me/"), new("https://6.ident.me/"))
    });

    public static IpLookupProvider Get(string id) => All.FirstOrDefault(provider => provider.Id == id)
        ?? throw new SettingsException("Choose an IP lookup service from the list in Settings.");
}

public sealed class IpLookupChoice(IpLookupProvider provider) : INotifyPropertyChanged
{
    public IpLookupProvider Provider { get; } = provider;
    public string DisplayName => Provider.DisplayName;
    public override string ToString() => DisplayName;
    public string? IPv4 { get; private set; }
    public string? Error { get; private set; }
    public string ResultText { get; private set; } = "Open the list to check IPv4";
    public string Details => ResultText + (Provider.SupportsIPv6 ? "" : " · IPv4 only");
    public string Help => Error ?? Provider.IPv4Endpoint.ToString();
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetResult(string text, string? address = null, string? error = null)
    {
        ResultText = text; IPv4 = address; Error = error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

// Refresh all rows concurrently, but never let an earlier opening overwrite newer results.
public sealed class IpLookupProbeSession
{
    private CancellationTokenSource? active;
    private int generation;
    public IReadOnlyList<IpLookupChoice> Choices { get; } = Array.AsReadOnly(IpLookupProviders.All.Select(p => new IpLookupChoice(p)).ToArray());

    public void Cancel() { generation++; active?.Cancel(); }

    public async Task RefreshAsync(IPublicIpResolver resolver, CancellationToken cancellationToken)
    {
        Cancel();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        active = run;
        int current = generation;
        foreach (var choice in Choices) choice.SetResult("Checking IPv4…");
        try
        {
            await Task.WhenAll(Choices.Select(async choice =>
            {
                try
                {
                    string address = await resolver.ResolveAsync(false, run.Token, choice.Provider.Id);
                    if (current == generation && !run.IsCancellationRequested) choice.SetResult("IPv4: " + address, address);
                }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (current == generation && !run.IsCancellationRequested)
                        choice.SetResult("Unavailable", error: ex is UpdateException ? ex.Message : "The lookup failed. Open the list to try again.");
                }
            }));
        }
        finally { if (ReferenceEquals(active, run)) active = null; }
    }
}
