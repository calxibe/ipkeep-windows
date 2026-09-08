using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace IPKeep.Core;

public static class AppPaths
{
    public const string ServiceName = "IPKeep";
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IPKeep");
    public static string ServiceDirectory => Path.Combine(InstallRoot, "service");
    public static string ServiceExecutable => Path.Combine(ServiceDirectory, "IPKeep.Service.exe");
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IPKeep");
    public static string PrivateDirectory => Path.Combine(DataRoot, "Private");
    public static string RuntimeDirectory => Path.Combine(DataRoot, "Activity");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string SecretFile => Path.Combine(PrivateDirectory, "connection.bin");
    public static string StatusFile => Path.Combine(RuntimeDirectory, "status.json");
    public static string LogFile => Path.Combine(RuntimeDirectory, "service.log");
}

public static class FileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void AtomicWrite(string path, byte[] content)
    {
        DeploymentSecurity.RejectLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(content); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WriteJson<T>(string path, T value) => AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
    public static T? ReadJson<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default;

    /// <summary>Last write time and length, or null when the file is absent or unreadable.</summary>
    /// <remarks>
    /// The desktop polls a few files on a timer. Comparing this against the previous value
    /// tells it whether reading and parsing them again can change anything on screen.
    /// </remarks>
    public static (DateTime Written, long Length)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class ConnectionSettings
{
    public ClientSettings Settings { get; init; } = new();
    public string Token { get; init; } = "";
    public override string ToString() => "IPKeep connection (token hidden)";
}

public static class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPKeep.Windows.Connection.v1");
    public static void Save(ClientSettings settings, string token)
    {
        DeploymentSecurity.RequireAdministrator();
        settings = settings.Validate(); token = ClientSettings.ValidateToken(token);
        DeploymentSecurity.EnsureDataDirectories();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ConnectionSettings { Settings = settings, Token = token });
        try { FileStore.AtomicWrite(AppPaths.SecretFile, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        // A non-secret projection lets a standard user inspect configured hostnames.
        FileStore.WriteJson(AppPaths.SettingsFile, settings);
    }

    public static ConnectionSettings Load()
    {
        DeploymentSecurity.ValidateFile(AppPaths.SecretFile, secret: true);
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.SecretFile), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            var saved = JsonSerializer.Deserialize<ConnectionSettings>(plain) ?? throw new SettingsException("Set up your IPKeep connection in Settings.");
            return new() { Settings = saved.Settings.Validate(), Token = ClientSettings.ValidateToken(saved.Token) };
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public static class DeploymentSecurity
{
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier ServiceSid = new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const FileSystemRights WriteRights = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void RequireAdministrator()
    { if (!IsAdministrator) throw new UnauthorizedAccessException("Choose Allow changes to approve Windows administrator access."); }

    public static void RejectLinks(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("IPKeep requires a local disk.");
        for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links and junctions are not supported in the IPKeep installation.");
    }

    public static void SecureDirectory(string path, bool secret = false, bool runtime = false)
    {
        RequireAdministrator(); RejectLinks(path);
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetOwner(Admin); acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { Admin, SystemSid })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new(ServiceSid, runtime ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (!secret) acl.AddAccessRule(new(Users, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static void EnsureDataDirectories()
    {
        SecureDirectory(AppPaths.DataRoot);
        SecureDirectory(AppPaths.PrivateDirectory, secret: true);
        SecureDirectory(AppPaths.RuntimeDirectory, runtime: true);
    }

    public static void SecureProgramFile(string path)
    {
        RequireAdministrator(); RejectLinks(path);
        var acl = new FileSecurity();
        acl.SetOwner(Admin); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Admin, SystemSid })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var sid in new[] { ServiceSid, Users })
            acl.AddAccessRule(new(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    public static void ValidateFile(string path, bool secret = false, bool runtime = false)
    {
        RejectLinks(path);
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, Admin) && !Equals(owner, SystemSid) && !(runtime && Equals(owner, ServiceSid)))
            throw new UnauthorizedAccessException("IPKeep files have an unexpected owner. Reinstall IPKeep to repair permissions.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || Equals(rule.IdentityReference, Admin) || Equals(rule.IdentityReference, SystemSid)) continue;
            if (Equals(rule.IdentityReference, ServiceSid) && runtime) continue;
            if ((rule.FileSystemRights & WriteRights) != 0 || ((int)rule.FileSystemRights & unchecked((int)0x50000000)) != 0 ||
                (secret && !Equals(rule.IdentityReference, ServiceSid) && (rule.FileSystemRights & FileSystemRights.ReadData) != 0))
                throw new UnauthorizedAccessException("IPKeep files have unsafe permissions. Reinstall IPKeep to repair permissions.");
        }
    }

    /// <summary>Verify that IPKeep is running from a correctly protected installation.</summary>
    /// <param name="includeAllFiles">
    /// Read the ACL of every published file under the service directory. The service
    /// directory is owned by Administrators with inheritance protected and no write access
    /// for anyone else, so once that directory passes, no unprivileged account can add or
    /// alter a file inside it. The exhaustive sweep therefore guards against permissions
    /// that were already wrong when the directory was created, which is a startup concern:
    /// pass true on startup and after installing, false on the recurring check, where
    /// reading 200 ACLs every cycle costs far more than it detects.
    /// </param>
    public static void ValidateRuntime(bool includeAllFiles = true)
    {
        if (!string.Equals(Environment.ProcessPath, AppPaths.ServiceExecutable, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Install IPKeep before starting its background service.");
        foreach (string directory in new[] { AppPaths.InstallRoot, AppPaths.ServiceDirectory, AppPaths.DataRoot }) ValidateFile(directory);
        ValidateFile(AppPaths.PrivateDirectory, secret: true);
        ValidateFile(AppPaths.RuntimeDirectory, runtime: true);
        if (!includeAllFiles) { ValidateFile(AppPaths.ServiceExecutable); return; }
        foreach (string file in Directory.EnumerateFiles(AppPaths.ServiceDirectory, "*", SearchOption.AllDirectories)) ValidateFile(file);
    }
}

public sealed record ServiceSnapshot
{
    public string State { get; init; } = "Waiting";
    public DateTimeOffset? NextCheck { get; init; }
    public CheckResult? LastCheck { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string Message { get; init; } = "Set up your IPKeep connection to get started.";
}

public sealed class ActivityLog(string path, long maximumBytes = 2 * 1024 * 1024) : IActivityLog
{
    private readonly object sync = new();
    private string secret = "";
    public void SetSecret(string token) => secret = token;
    public void Write(string level, string message)
    {
        lock (sync)
        {
            try
            {
                message = message.Replace('\r', ' ').Replace('\n', ' ');
                if (secret.Length > 0) message = message.Replace(secret, "[hidden]", StringComparison.Ordinal);
                if (message.Length > 1500) message = message[..1500];
                if (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
                {
                    for (int i = 4; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(file);
                writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {level,-5}  {message}");
            }
            catch (IOException) { /* A locked log must not stop IP updates. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string[] ReadTail(string path, int count = 200)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 100_000) stream.Seek(-100_000, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (stream.Position > 0) reader.ReadLine();
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count).Reverse().ToArray();
    }
}
