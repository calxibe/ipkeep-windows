using System.ComponentModel;
using IPKeep.Core;
using Microsoft.UI.Xaml;

namespace IPKeep.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource? installerDownload;
    private DownloadedAppInstaller? downloadedInstaller;
    private bool downloadingInstaller;
    private bool launchingInstaller;
    private bool loadingDownloadPreference = true;

    private void InitializeAppDownloads()
    {
        using var http = AppInstallerDownload.CreateHttpClient();
        try { AutomaticDownloadSwitch.IsOn = new AppInstallerDownload(http, AppInstallerDownload.CurrentDirectory).AutomaticallyDownload; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutomaticDownloadSwitch.IsOn = false;
            AppUpdateStatus.Text = "Could not read your download preference. Automatic downloads are off.";
        }
        loadingDownloadPreference = false;
    }

    private async void AutomaticDownload_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingDownloadPreference || windowClosed) return;
        using var http = AppInstallerDownload.CreateHttpClient();
        try { new AppInstallerDownload(http, AppInstallerDownload.CurrentDirectory).AutomaticallyDownload = AutomaticDownloadSwitch.IsOn; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppUpdateStatus.Text = "Could not save your download preference. Please try again.";
            return;
        }
        if (!AutomaticDownloadSwitch.IsOn) installerDownload?.Cancel();
        else if (!checkingAppUpdates && availableAppRelease is not null && downloadedInstaller is null) await DownloadInstallerAsync();
    }

    private void CancelAppDownload_Click(object sender, RoutedEventArgs e) => installerDownload?.Cancel();

    private void UpdateInstallerControls()
    {
        InstallAppUpdateButton.Content = downloadedInstaller is null ? "Download update" : "Install update";
        InstallAppUpdateButton.IsEnabled = availableAppRelease is not null && !checkingAppUpdates && !downloadingInstaller && !launchingInstaller;
        CancelAppDownloadButton.Visibility = downloadingInstaller ? Visibility.Visible : Visibility.Collapsed;
        InstallerDownloadProgress.Visibility = downloadingInstaller ? Visibility.Visible : Visibility.Collapsed;
        if (downloadedInstaller is not null) InstallerDownloadStatus.Text = "Update downloaded and verified. Ready to install.";
    }

    private async Task DownloadInstallerAsync()
    {
        if (windowClosed || downloadingInstaller || launchingInstaller || availableAppRelease is not { } release) return;
        downloadingInstaller = true;
        downloadedInstaller = null;
        installerDownload = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        using var cancellation = installerDownload;
        CheckAppUpdatesButton.IsEnabled = false;
        InstallerDownloadStatus.Text = "Downloading update…";
        InstallerDownloadProgress.Value = 0;
        UpdateInstallerControls();
        try
        {
            using var http = AppInstallerDownload.CreateHttpClient();
            var downloader = new AppInstallerDownload(http, AppInstallerDownload.CurrentDirectory);
            var progress = new Progress<double>(value =>
            {
                if (windowClosed || !downloadingInstaller) return;
                InstallerDownloadProgress.Value = value;
                InstallerDownloadStatus.Text = value >= 100 ? "Verifying download…" : $"Downloading update… {value:0}%";
            });
            downloadedInstaller = await downloader.DownloadAsync(release.Version, progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!windowClosed) InstallerDownloadStatus.Text = cancellation.IsCancellationRequested
                ? "Download cancelled. You can download it again when you're ready."
                : "The download timed out. Try again when your connection is available.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!windowClosed) InstallerDownloadStatus.Text = "Could not download and verify the update. Try again, or download it from GitHub.";
        }
        finally
        {
            installerDownload = null;
            downloadingInstaller = false;
            if (!windowClosed) { CheckAppUpdatesButton.IsEnabled = !checkingAppUpdates; UpdateInstallerControls(); }
        }
    }

    private async void InstallAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (checkingAppUpdates || downloadingInstaller || launchingInstaller || windowClosed) return;
        if (downloadedInstaller is not { } installer) { await DownloadInstallerAsync(); return; }
        launchingInstaller = true;
        UpdateInstallerControls();
        CheckAppUpdatesButton.IsEnabled = false;
        InstallerDownloadStatus.Text = "Opening Windows setup…";
        try
        {
            using var http = AppInstallerDownload.CreateHttpClient();
            await new AppInstallerDownload(http, AppInstallerDownload.CurrentDirectory).LaunchAsync(installer, windowLifetime.Token);
            if (!windowClosed) Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!windowClosed) InstallerDownloadStatus.Text = "Installation cancelled. The update is still ready when you are.";
        }
        catch (OperationCanceledException) when (windowClosed) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            downloadedInstaller = null;
            if (!windowClosed) InstallerDownloadStatus.Text = "Windows could not open the verified installer. Try downloading it again, or use GitHub.";
        }
        finally
        {
            launchingInstaller = false;
            if (!windowClosed)
            {
                // Keep cancellation/error feedback rather than replacing it with the ready message.
                string feedback = InstallerDownloadStatus.Text;
                UpdateInstallerControls();
                InstallerDownloadStatus.Text = feedback;
                CheckAppUpdatesButton.IsEnabled = !checkingAppUpdates;
            }
        }
    }
}
