namespace IPKeep.Core;

// Only application-authored descriptions cross into the public status file/log.
// Native exception messages can contain private paths and must not be displayed verbatim.
public sealed class InstallationPermissionException(bool connection, string message, Exception? inner = null)
    : UnauthorizedAccessException(message, inner)
{
    public string ProblemCode { get; } = connection ? ServiceRecovery.ConnectionPermissions : ServiceRecovery.InstallationPermissions;
}

public sealed record ServiceRecovery(string Title, string Instructions, bool RepairInstallation = false)
{
    public const string ConnectionPermissions = "connection-permissions";
    public const string InstallationPermissions = "installation-permissions";
    public const string Permissions = "permissions";
    public const string LegacyFailureMessage = "Cannot read the connection or verify the installation. Open IPKeep Settings to repair it.";

    public static string? ProblemCodeFor(Exception error) => error switch
    {
        InstallationPermissionException permission => permission.ProblemCode,
        UnauthorizedAccessException => Permissions,
        _ => null
    };

    public static string FailureMessage(Exception error) => error switch
    {
        InstallationPermissionException or SettingsException => error.Message,
        UnauthorizedAccessException => "Windows file permissions are preventing IPKeep from updating. Open Settings for repair steps.",
        _ => LegacyFailureMessage
    };

    public static ServiceRecovery? For(ServiceSnapshot snapshot) => snapshot.ProblemCode switch
    {
        ConnectionPermissions => new("Saved connection permissions need repair",
            "Load your hostnames if needed, then Save and enable updates. Saving restores the private permissions on your saved connection and restarts updates."),
        InstallationPermissions => new("Installation permissions need repair",
            "Choose Repair / update service under Service maintenance. If requested, verify your token and Save and enable updates.", true),
        Permissions => new("Windows permissions need attention",
            "Verify your token and Save and enable updates. If the warning returns, use Repair / update service under Service maintenance and check Activity for the new error."),
        // Older services did not distinguish permission errors from other setup failures.
        null when snapshot.State == "Needs attention" && snapshot.Message == LegacyFailureMessage => new("Connection setup needs attention",
            "Verify your token and Save and enable updates. If it still fails, check Activity and use Repair / update service under Service maintenance."),
        _ => null
    };

    public string InstructionsFor(bool administrator) => (administrator ? "" : "Choose Allow changes above. ") + Instructions;

    public static string HostMessage(ServiceSnapshot snapshot, string hostname)
    {
        var result = snapshot.LastCheck?.Hosts.FirstOrDefault(host => host.Hostname == hostname);
        if (result is null) return snapshot.LastAttemptFailed ? "The latest check failed before this hostname could be checked." : "Waiting for its first update.";
        bool earlierResult = snapshot.LastAttemptFailed || (snapshot.State == "Needs attention" && snapshot.LastCheck!.Success);
        return earlierResult
            ? $"Earlier result ({snapshot.LastCheck!.CheckedAt.ToLocalTime():dd MMM, HH:mm:ss}): {result.Message} The latest check could not complete."
            : result.Message;
    }
}
