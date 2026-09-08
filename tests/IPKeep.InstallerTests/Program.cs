using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using IPKeep.Core;
using Microsoft.Win32;

// This integration test intentionally installs/uninstalls a real service. Never run on
// a user's workstation: require explicit opt-in and an empty GitHub-hosted Windows runner.
if (args.Length != 2 || args[0] != "--disposable-runner" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
    Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || !DeploymentSecurity.IsAdministrator ||
    Directory.Exists(AppPaths.InstallRoot) || Directory.Exists(AppPaths.DataRoot) || ServiceManager.GetStatus() is not null)
    throw new InvalidOperationException("Installer tests require an empty, disposable GitHub-hosted Windows runner.");

string installer = Path.GetFullPath(args[1]);
if (!File.Exists(installer)) throw new FileNotFoundException("Installer not found.");
string logs = Path.Combine(Path.GetDirectoryName(installer)!, "installer-test-logs");
Directory.CreateDirectory(logs);
string app = InstallerOperations.DesktopDirectory;
string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B43D7C64-8CCA-44BD-B5E8-01BFE865A142}_is1";
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Pass(string message) { passed++; Console.WriteLine("PASS " + message); }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
int Run(string filename, params string[] arguments)
{
    using var process = new Process { StartInfo = new(filename) { UseShellExecute = false, CreateNoWindow = true } };
    foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    if (!process.WaitForExit(180_000)) { process.Kill(entireProcessTree: true); throw new System.TimeoutException("Installer test timed out."); }
    return process.ExitCode;
}
int Setup(string name, params string[] extra) => Run(installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + Path.Combine(logs, name + ".log"), .. extra]);
void Uninstall(string name)
{
    string uninstaller = Directory.GetFiles(app, "unins*.exe").Single();
    Assert(Run(uninstaller, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(logs, name + ".log")) == 0, "Uninstall failed.");
    for (int i = 0; i < 100 && File.Exists(Path.Combine(app, "IPKeep.exe")); i++) Thread.Sleep(100);
}
void CheckMode(ServiceControllerStatus status, int mode)
{
    Assert(ServiceManager.GetStatus() == status, "Service running/paused state was changed.");
    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\IPKeep");
    Assert(Equals(key?.GetValue("Start"), mode), "Service startup mode was changed.");
}

Assert(Setup("first-install") == 0, "Fresh install failed.");
Assert(File.Exists(Path.Combine(app, "IPKeep.exe")) && File.Exists(Path.Combine(app, "third-party", "README.md")), "Installed app/notices missing.");
Assert(ServiceManager.GetStatus() is null, "A new install must wait for token setup before creating a service.");
Assert(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "IPKeep.lnk")), "Start menu shortcut missing.");
using (var key = Registry.LocalMachine.OpenSubKey(uninstallKey)) Assert(key is not null, "Installed Apps entry missing.");
DeploymentSecurity.ValidateFile(app);
Pass("Fresh install, protected location, Start menu shortcut, Installed Apps entry, no unconfigured service");

ServiceManager.Install(app);
// Invalid encrypted bytes ensure the real service cannot reach token-authenticated APIs.
byte[] connectionFixture = [17, 29, 31, 41, 53];
FileStore.AtomicWrite(AppPaths.SecretFile, connectionFixture);
FileStore.WriteJson(AppPaths.SettingsFile, new ClientSettings { Hostnames = ["beta-test.a.ipkeep.net"], IntervalMinutes = 360 });
string secretHash = Hash(AppPaths.SecretFile), settingsHash = Hash(AppPaths.SettingsFile);
void CheckData()
{
    Assert(Hash(AppPaths.SecretFile) == secretHash && Hash(AppPaths.SettingsFile) == settingsHash, "Setup changed saved connection/settings.");
    Assert(File.ReadAllText(Path.Combine(AppPaths.RuntimeDirectory, "beta-sentinel.txt")) == "retained", "Setup removed activity data.");
}
File.WriteAllText(Path.Combine(AppPaths.RuntimeDirectory, "beta-sentinel.txt"), "retained");
ServiceManager.Start();
Assert(Setup("running-upgrade") == 0, "Running service upgrade failed.");
CheckMode(ServiceControllerStatus.Running, 2); CheckData();
Assert(Hash(AppPaths.ServiceExecutable) == Hash(Path.Combine(app, "service", "IPKeep.Service.exe")), "Service was not updated from the bundle.");
Pass("Upgrade of running service, executable replacement, settings/token/activity preservation");

ServiceManager.Pause();
Assert(Setup("paused-upgrade") == 0, "Paused service upgrade failed.");
CheckMode(ServiceControllerStatus.Stopped, 3); CheckData();
Pass("Paused service remains stopped and manual after an upgrade");

using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\IPKeep", writable: true)) key!.SetValue("Start", 4);
Assert(Setup("disabled-upgrade") == 0, "Disabled service upgrade failed.");
CheckMode(ServiceControllerStatus.Stopped, 4); CheckData();
Pass("Disabled service remains disabled after an upgrade");
using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\IPKeep", writable: true)) key!.SetValue("Start", 3);

// Block the directory swap. The installer must report failure, retaining the prior service.
string serviceHash = Hash(AppPaths.ServiceExecutable);
using (var heldFile = new FileStream(AppPaths.ServiceExecutable, FileMode.Open, FileAccess.Read, FileShare.Read))
    Assert(Setup("locked-service-upgrade") == 20, "Service update failure was reported as success.");
CheckMode(ServiceControllerStatus.Stopped, 3); CheckData();
Assert(Hash(AppPaths.ServiceExecutable) == serviceHash, "A failed upgrade lost the previous service.");
Assert(Setup("repair-after-failure") == 0, "Repair after failed upgrade did not succeed.");
Pass("Failed service update reports a nonzero exit code, preserves previous service, and can be repaired");

using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\IPKeep\Installer", writable: true)) key!.SetValue("Version", "99.0.0.0");
Assert(Setup("downgrade-refused") != 0, "Installer overwrote a newer version.");
using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\IPKeep\Installer", writable: true)) key!.SetValue("Version", "1.0.0.2");
Assert(Setup("custom-path-refused", "/DIR=" + Path.Combine(Path.GetTempPath(), "IPKeep-wrong-location")) != 0, "Installer accepted a custom executable directory.");
Pass("Downgrades and unsupported install paths are refused");

ServiceManager.Start();
Uninstall("remove-running-service");
Assert(ServiceManager.GetStatus() is null, "Uninstall left a registered service.");
Assert(!Directory.Exists(AppPaths.ServiceDirectory) && !File.Exists(Path.Combine(app, "IPKeep.exe")), "Uninstall left application/service binaries.");
Assert(!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "IPKeep.lnk")), "Uninstall left its shortcut.");
using (var key = Registry.LocalMachine.OpenSubKey(uninstallKey)) Assert(key is null, "Uninstall left its Installed Apps entry.");
CheckData();
Pass("Uninstall stops/removes service, app, shortcut, and registration while retaining connection/activity");

Assert(Setup("reinstall") == 0, "Reinstallation failed.");
CheckData(); Assert(ServiceManager.GetStatus() is null, "Reinstallation enabled updates without user action.");
Uninstall("remove-without-service"); CheckData();
Pass("Reinstall retains data and removal also works when no service is installed");
Console.WriteLine($"{passed} installer integration checks passed. No usable API token was present.");
