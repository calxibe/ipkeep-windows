using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace IPKeep.Core;

public static class ServiceManager
{
    public static void SaveAndEnable(string bundleDirectory, ClientSettings settings, string token)
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        settings = settings.Validate(); token = ClientSettings.ValidateToken(token);
        var oldStatus = GetStatus();
        byte[]? oldSecret = File.Exists(AppPaths.SecretFile) ? File.ReadAllBytes(AppPaths.SecretFile) : null;
        byte[]? oldPublic = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AppPaths.ServiceName);
        var oldStartMode = key?.GetValue("Start") as int?;
        bool installed = false;
        try
        {
            if (oldStatus is null) { Install(bundleDirectory); installed = true; }
            else Stop();
            SettingsStore.Save(settings, token); Start();
        }
        catch
        {
            // Restore the previous encrypted connection if saving or starting fails.
            if (GetStatus() is not null) Stop();
            if (oldSecret is not null) FileStore.AtomicWrite(AppPaths.SecretFile, oldSecret);
            else if (File.Exists(AppPaths.SecretFile)) File.Delete(AppPaths.SecretFile);
            if (oldPublic is not null) FileStore.AtomicWrite(AppPaths.SettingsFile, oldPublic);
            else if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
            if (installed) Uninstall();
            else if (oldStatus == ServiceControllerStatus.Running) Start();
            if (oldStartMode is not null) RunSc("config", AppPaths.ServiceName, "start=", oldStartMode == 2 ? "auto" : oldStartMode == 4 ? "disabled" : "demand");
            throw;
        }
    }

    public static ServiceControllerStatus? GetStatus()
    {
        using var service = new ServiceController(AppPaths.ServiceName);
        try { return service.Status; }
        catch (InvalidOperationException) { return null; }
    }

    public static void VerifyRegistration()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AppPaths.ServiceName);
        if (key is null) return;
        if (!string.Equals(key.GetValue("ImagePath") as string, '"' + AppPaths.ServiceExecutable + '"', StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(key.GetValue("ObjectName") as string, @"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different service uses the IPKeep name. Resolve its registration in Windows Services before installing.");
    }

    public static void Install(string bundleDirectory)
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        string source = Path.Combine(bundleDirectory, "service");
        if (!File.Exists(Path.Combine(source, "IPKeep.Service.exe")))
            throw new FileNotFoundException("The service files are missing. Extract the complete IPKeep download before setting up.");
        DeploymentSecurity.SecureDirectory(AppPaths.InstallRoot);
        DeploymentSecurity.EnsureDataDirectories();
        string stage = Path.Combine(AppPaths.InstallRoot, "service-stage-" + Guid.NewGuid().ToString("N"));
        string previous = Path.Combine(AppPaths.InstallRoot, "service-previous-" + Guid.NewGuid().ToString("N"));
        DeploymentSecurity.SecureDirectory(stage);
        CopyDirectory(source, stage);
        var oldStatus = GetStatus();
        bool moved = false, created = false;
        try
        {
            if (oldStatus is not null) Stop();
            if (Directory.Exists(AppPaths.ServiceDirectory)) { Directory.Move(AppPaths.ServiceDirectory, previous); moved = true; }
            Directory.Move(stage, AppPaths.ServiceDirectory);
            if (oldStatus is null)
            {
                RunSc("create", AppPaths.ServiceName, "binPath=", '"' + AppPaths.ServiceExecutable + '"', "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "IPKeep background updates");
                created = true;
            }
            RunSc("description", AppPaths.ServiceName, "Keeps your IPKeep hostnames connected using your IPKeep API token.");
            RunSc("failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/300000/restart/300000");
            if (oldStatus == ServiceControllerStatus.Running) Resume();
        }
        catch
        {
            if (created) RunSc("delete", AppPaths.ServiceName);
            if (moved)
            {
                if (Directory.Exists(AppPaths.ServiceDirectory)) Directory.Move(AppPaths.ServiceDirectory, stage + "-failed");
                Directory.Move(previous, AppPaths.ServiceDirectory);
            }
            if (oldStatus == ServiceControllerStatus.Running) Resume();
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        DeploymentSecurity.RejectLinks(source);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            DeploymentSecurity.RejectLinks(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            DeploymentSecurity.RejectLinks(folder);
            string target = Path.Combine(destination, Path.GetFileName(folder));
            Directory.CreateDirectory(target); CopyDirectory(folder, target);
        }
    }

    public static void Start()
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        RunSc("config", AppPaths.ServiceName, "start=", "auto");
        Resume();
    }

    private static void Resume()
    {
        // Installation must not change an existing service's automatic/manual startup mode.
        using var service = new ServiceController(AppPaths.ServiceName);
        if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Stop()
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        using var service = new ServiceController(AppPaths.ServiceName);
        if (service.Status == ServiceControllerStatus.StartPending) service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }

    public static void Pause()
    { Stop(); RunSc("config", AppPaths.ServiceName, "start=", "demand"); }

    public static void CheckNow()
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        using var service = new ServiceController(AppPaths.ServiceName);
        service.ExecuteCommand(128);
    }

    public static void Uninstall()
    {
        DeploymentSecurity.RequireAdministrator(); VerifyRegistration();
        if (GetStatus() is null) return;
        Stop(); RunSc("delete", AppPaths.ServiceName);
    }

    private static void RunSc(params string[] arguments)
    {
        using var process = new Process { StartInfo = new(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) throw new InvalidOperationException("Windows service management timed out. Check Windows Services before trying again.");
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows could not complete the service action (code {process.ExitCode}). Check administrator access and Windows Services.");
    }
}
