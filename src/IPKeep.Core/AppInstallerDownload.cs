using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace IPKeep.Core;

public sealed record DownloadedAppInstaller(string Path, string Sha256, ReleaseVersion Version);

// Downloads only this project's published installer and checksum file. This client
// has no token input and never runs the downloaded program automatically.
public sealed class AppInstallerDownload(HttpClient http, string directory)
{
    public const long MaximumInstallerBytes = 256 * 1024 * 1024;
    public static string CurrentDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPKeep", "Updates");
    private const string ReleaseRoot = "https://github.com/calxibe/ipkeep-windows/releases/download/";

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string InstallerName(ReleaseVersion version) => $"IPKeep-{version.Text}-windows-x64-unsigned-setup.exe";

    public bool AutomaticallyDownload
    {
        get
        {
            string path = System.IO.Path.Combine(directory, "automatic-download.txt");
            DeploymentSecurity.RejectLinks(path);
            return !File.Exists(path) || File.ReadAllText(path).Trim() != "off";
        }
        set
        {
            PrepareDirectory();
            string path = System.IO.Path.Combine(directory, "automatic-download.txt");
            DeploymentSecurity.RejectLinks(path);
            FileStore.AtomicWrite(path, Encoding.ASCII.GetBytes(value ? "on" : "off"));
        }
    }

    public async Task<DownloadedAppInstaller> DownloadAsync(ReleaseVersion version, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // This bound also keeps derived filenames within Windows path limits.
        if (version.Text.Length > 64) throw new InvalidDataException("The release version is too long.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        string filename = InstallerName(version);
        var releaseUrl = new Uri(ReleaseRoot + "v" + version.Text + "/");
        string checksum;
        using (var sums = await GetAsync(new Uri(releaseUrl, "SHA256SUMS.txt"), ct))
        {
            await sums.Content.LoadIntoBufferAsync(16384, ct);
            string text = Encoding.UTF8.GetString(await sums.Content.ReadAsByteArrayAsync(ct));
            var matches = text.Split('\n').Select(line => Regex.Match(line.TrimEnd('\r'), @"\A([0-9a-fA-F]{64})  (.+)\z"))
                .Where(match => match.Success && match.Groups[2].Value == filename).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The release does not have a unique installer checksum.");
            checksum = matches[0].Groups[1].Value.ToLowerInvariant();
        }

        PrepareDirectory();
        string destination = System.IO.Path.Combine(directory, filename);
        DeploymentSecurity.RejectLinks(destination);
        if (File.Exists(destination))
        {
            await using var existing = OpenInstaller(destination);
            if (existing.Length > 0 && existing.Length <= MaximumInstallerBytes &&
                await HasHashAsync(existing, checksum, ct))
            {
                progress?.Report(100);
                return new(destination, checksum, version);
            }
        }

        string partial = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var response = await GetAsync(new Uri(releaseUrl, filename), ct);
            long? length = response.Content.Headers.ContentLength;
            if (length is <= 0 or > MaximumInstallerBytes) throw new InvalidDataException("The installer has an invalid size.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int previousPercent = -1;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    total += count;
                    if (total > MaximumInstallerBytes || (length is not null && total > length))
                        throw new InvalidDataException("The installer exceeds its expected size.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    int percent = length is > 0 ? (int)(100 * total / length.Value) : 0;
                    if (percent != previousPercent) { progress?.Report(percent); previousPercent = percent; }
                }
                if (total == 0 || (length is not null && total != length) ||
                    !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(checksum, StringComparison.Ordinal))
                    throw new InvalidDataException("The downloaded installer did not match its published checksum. Try again.");
                await target.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            // Preserve Windows' downloaded-file protections for this unsigned beta.
            File.WriteAllText(partial + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={new Uri(releaseUrl, filename)}\r\n", Encoding.ASCII);
            DeploymentSecurity.RejectLinks(destination);
            File.Move(partial, destination, overwrite: true);
            progress?.Report(100);
            return new(destination, checksum, version);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public void PrepareDirectory()
    {
        DeploymentSecurity.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Windows user is unavailable.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct)
    {
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            if (!AllowedDownloadUrl(url)) throw new InvalidDataException("The release download redirected to an unexpected location.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("IPKeep-Windows/" + AppUpdateClient.CurrentVersion.Text);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("The release download returned an invalid redirect.");
                url = new Uri(url, location);
                continue;
            }
            response.Dispose();
            throw new HttpRequestException("The release download did not return a complete file.");
        }
        throw new InvalidDataException("The release download redirected too many times.");
    }

    public static bool AllowedDownloadUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == "https" && url.Port == 443 &&
        url.UserInfo.Length == 0 && url.Fragment.Length == 0 &&
        ((url.Host == "github.com" && url.AbsolutePath.StartsWith("/calxibe/ipkeep-windows/releases/download/", StringComparison.Ordinal)) ||
            url.Host == "release-assets.githubusercontent.com");

    private static FileStream OpenInstaller(string path)
    {
        DeploymentSecurity.RejectLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }

    private static async Task<bool> HasHashAsync(Stream file, string checksum, CancellationToken ct) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)).Equals(checksum, StringComparison.Ordinal);

    // Hold the file against writes/deletion through the launch. The caller invokes
    // this only after an explicit Install update click, with the normal Windows UI.
    public async Task LaunchAsync(DownloadedAppInstaller installer, CancellationToken ct, Action<ProcessStartInfo>? start = null)
    {
        string expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, InstallerName(installer.Version)));
        if (!System.IO.Path.GetFullPath(installer.Path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer is outside the update folder.");
        if (installer.Version.CompareTo(AppUpdateClient.CurrentVersion) <= 0)
            throw new InvalidDataException("This installer is not newer than the application.");
        await using var file = OpenInstaller(expected);
        if (file.Length == 0 || file.Length > MaximumInstallerBytes || !await HasHashAsync(file, installer.Sha256, ct))
            throw new InvalidDataException("The downloaded installer has changed. Download it again.");
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(expected)
        {
            UseShellExecute = true, Verb = "open", Arguments = "/NORESTART /CLOSEAPPLICATIONS",
            WorkingDirectory = directory
        };
        if (start is not null) start(info);
        else using (Process.Start(info) ?? throw new InvalidOperationException("Windows could not open the installer.")) { }
    }
}
