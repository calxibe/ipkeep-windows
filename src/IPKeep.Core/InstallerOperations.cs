namespace IPKeep.Core;

// Invoked by the bundled service executable before it enters the Windows service loop.
// No installer operation reads tokens, changes settings, or accepts caller-supplied paths.
public static class InstallerOperations
{
    public static string DesktopDirectory => Path.Combine(AppPaths.InstallRoot, "app");

    public static bool IsCommand(string[] arguments) => arguments.Length == 1 &&
        arguments[0] is "--installer-check" or "--installer-provision" or "--installer-upgrade" or "--installer-remove";

    public static int Run(string[] arguments, string executableDirectory)
    {
        if (!IsCommand(arguments)) return 2;
        try
        {
            DeploymentSecurity.RequireAdministrator();
            ServiceManager.VerifyRegistration();
            CheckTree(AppPaths.InstallRoot);
            if (Directory.Exists(AppPaths.InstallRoot)) DeploymentSecurity.ValidateFile(AppPaths.InstallRoot);
            if (Directory.Exists(DesktopDirectory)) DeploymentSecurity.ValidateFile(DesktopDirectory);
            if (arguments[0] == "--installer-check")
            {
                // Explicit ownership avoids relying on the installer account's default owner.
                DeploymentSecurity.SecureDirectory(AppPaths.InstallRoot);
                DeploymentSecurity.SecureDirectory(DesktopDirectory);
                return 0;
            }
            if (arguments[0] == "--installer-provision")
            {
                ProtectBundle(DesktopDirectory);
                return 0;
            }

            // Only the helper installed in the fixed, protected app bundle can mutate a service.
            string expected = Path.Combine(DesktopDirectory, "service");
            if (!Path.GetFullPath(executableDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Run the installer from its installed bundle.");
            DeploymentSecurity.ValidateFile(DesktopDirectory);
            DeploymentSecurity.ValidateFile(Path.Combine(expected, "IPKeep.Service.exe"));

            if (arguments[0] == "--installer-upgrade")
            {
                // First-time setup is completed in the desktop after the user supplies a token.
                if (ServiceManager.GetStatus() is not null) ServiceManager.Install(DesktopDirectory);
                return 0;
            }

            ServiceManager.Uninstall();
            // Delete only the fixed service folder and GUID-named staging/backup folders.
            // A complete link check precedes deletion; settings and user profiles are outside it.
            foreach (string directory in Directory.EnumerateDirectories(AppPaths.InstallRoot))
                if (IsServiceDirectoryName(Path.GetFileName(directory)))
                {
                    CheckTree(directory);
                    Directory.Delete(directory, recursive: true);
                }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.TimeoutException or System.ServiceProcess.TimeoutException)
        {
            // These operations never read a token or connection, so diagnostics contain only deployment errors.
            Console.Error.WriteLine($"IPKeep installer operation failed ({ex.GetType().Name}): {ex.Message}");
            return arguments[0] switch { "--installer-check" => 10, "--installer-upgrade" => 20, _ => 30 };
        }
    }

    private static void ProtectBundle(string directory)
    {
        DeploymentSecurity.SecureDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            DeploymentSecurity.RejectLinks(file);
            DeploymentSecurity.SecureProgramFile(file);
            DeploymentSecurity.ValidateFile(file);
        }
        foreach (string child in Directory.EnumerateDirectories(directory)) ProtectBundle(child);
    }

    public static bool IsServiceDirectoryName(string name)
    {
        if (name.Equals("service", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string prefix in new[] { "service-stage-", "service-previous-" })
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = name[prefix.Length..];
            if (suffix.EndsWith("-failed", StringComparison.Ordinal)) suffix = suffix[..^7];
            return Guid.TryParseExact(suffix, "N", out _);
        }
        return false;
    }

    public static void CheckTree(string path)
    {
        DeploymentSecurity.RejectLinks(path);
        if (!Directory.Exists(path)) return;
        // Walk one level at a time so a junction is rejected before traversing its target.
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            DeploymentSecurity.RejectLinks(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
}
