using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace IPKeep.Core;

// The desktop remembers a verified token for this Windows user. The service's
// separate machine-protected connection and administrator-only ACLs stay intact.
public sealed class UserTokenStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPKeep.Windows.UserToken.v1");
    public static UserTokenStore Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPKeep", "Private", "token.bin"));

    public string? Load()
    {
        DeploymentSecurity.RejectLinks(path);
        if (!File.Exists(path)) return null;
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return ClientSettings.ValidateToken(Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(string token)
    {
        token = ClientSettings.ValidateToken(token);
        DeploymentSecurity.RejectLinks(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Windows user is unavailable.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);

        byte[] bytes = Encoding.UTF8.GetBytes(token);
        try { FileStore.AtomicWrite(path, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
