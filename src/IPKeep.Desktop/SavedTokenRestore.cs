using System.Diagnostics;
using System.Security.Principal;
using IPKeep.Core;

namespace IPKeep.Desktop;

internal static class SavedTokenRestore
{
    internal const string Argument = "--restore-user-token";

    // This elevated, short-lived helper never receives a token or output path on
    // its command line, and refuses to export a token to another Windows account.
    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != Argument || !DeploymentSecurity.IsAdministrator
            || arguments[1] != WindowsIdentity.GetCurrent().User?.Value) return 2;
        try { UserTokenStore.Current.Save(SettingsStore.Load().Token); return 0; }
        catch { return 1; }
    }

    internal static async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        using var process = Process.Start(start) ?? throw new SettingsException("Windows could not restore the saved token.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 2)
            throw new SettingsException("Restore using administrator access for the same Windows account, or paste your IPKeep token here.");
        if (process.ExitCode != 0)
            throw new SettingsException("The saved token could not be restored. Paste your IPKeep token here to reconnect.");
    }
}
