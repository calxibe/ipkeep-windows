namespace IPKeep.Core;

// A selection is bound to the exact token used to retrieve it, never to free-form user input.
public sealed class HostSelectionSession
{
    private string? connectedToken;
    private int generation;
    public IReadOnlyList<string> Hostnames { get; private set; } = Array.Empty<string>();
    public bool IsConnected => connectedToken is not null;

    public void Reset()
    { generation++; connectedToken = null; Hostnames = Array.Empty<string>(); }

    public async Task<bool> ConnectAsync(string token, IHostListClient client, CancellationToken cancellationToken)
    {
        Reset(); int requestGeneration = generation;
        token = ClientSettings.ValidateToken(token);
        string[] hosts = await client.ListHostsAsync(token, cancellationToken);
        if (requestGeneration != generation) return false;
        connectedToken = token;
        Hostnames = Array.AsReadOnly(hosts.ToArray());
        return true;
    }

    public string[] RestoreSelection(IEnumerable<string> previous)
    {
        if (!IsConnected) return [];
        if (Hostnames.Count == 1) return [Hostnames[0]];
        // Preserve all still-active choices, including an older oversized setup:
        // the user must explicitly reduce it instead of silently losing hosts.
        return previous.Distinct(StringComparer.Ordinal).Where(name => Hostnames.Contains(name, StringComparer.Ordinal)).ToArray();
    }

    public ConnectionSettings Select(ClientSettings settings, IEnumerable<string> hostnames)
    {
        if (connectedToken is null) throw new SettingsException("Load the hostnames for your token first.");
        var selected = hostnames.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Any(name => !Hostnames.Contains(name, StringComparer.Ordinal)))
            throw new SettingsException("Choose existing hostnames from the dropdown. Create new hostnames in the IPKeep admin panel.");
        return new ConnectionSettings { Token = connectedToken, Settings = (settings with { Hostnames = selected }).Validate() };
    }

    public async Task<ConnectionSettings> SelectAsync(ClientSettings settings, IEnumerable<string> hostnames,
        IHostListClient client, CancellationToken cancellationToken)
    {
        var connection = Select(settings, hostnames);
        int requestGeneration = generation;
        var available = await client.ListHostsAsync(connection.Token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (requestGeneration != generation)
            throw new SettingsException("The token changed. Load its hostnames again before saving.");
        if (connection.Settings.Hostnames.Any(name => !available.Contains(name, StringComparer.Ordinal)))
        {
            Reset();
            throw new SettingsException("One or more selected hostnames are no longer active. Load your hostnames again and review your selection.");
        }
        return connection;
    }
}
