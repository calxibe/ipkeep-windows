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

    public ConnectionSettings Select(ClientSettings settings, string? hostname)
    {
        if (connectedToken is null) throw new SettingsException("Load the hostnames for your token first.");
        if (hostname is null || !Hostnames.Contains(hostname, StringComparer.Ordinal))
            throw new SettingsException("Choose an existing hostname from the dropdown. Create new hostnames in the IPKeep admin panel.");
        return new ConnectionSettings { Token = connectedToken, Settings = (settings with { Hostnames = [hostname] }).Validate() };
    }
}
