using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPKeep.Core;

public static class DiagnosticPreferences
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPKeep.Windows.Diagnostics.v1");
    private static string PathFor(string token, string hostname)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + "\n" + ClientSettings.NormalizeHostname(hostname))));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPKeep", "Private", "diagnostic-" + id + ".bin");
    }
    public static LocalDiagnosticOptions Load(string token, string hostname)
    {
        string path = PathFor(token, hostname); DeploymentSecurity.RejectLinks(path);
        if (!File.Exists(path)) return new(new());
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return (JsonSerializer.Deserialize<LocalDiagnosticOptions>(plain, DiagnosticClient.JsonOptions) ?? new(new())) with { WanIPv4 = null }; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Save(string token, string hostname, LocalDiagnosticOptions options)
    {
        // Reuse the existing per-user ACL setup before writing the encrypted preferences.
        UserTokenStore.Current.Save(token);
        string path = PathFor(token, hostname); DeploymentSecurity.RejectLinks(path);
        // A manually entered WAN must be checked again when the dialog is reopened.
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(options with { WanIPv4 = null }, DiagnosticClient.JsonOptions);
        try { FileStore.AtomicWrite(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
