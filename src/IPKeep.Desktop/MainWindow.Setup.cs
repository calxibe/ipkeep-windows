using System.ServiceProcess;
using IPKeep.Core;
using Microsoft.UI.Xaml;

namespace IPKeep.Desktop;

public sealed partial class MainWindow
{
    private void UpdateSetupState()
    {
        bool installed = serviceStatus is not null;
        bool needsRepair = installed && ServiceRecovery.For(cachedSnapshot) is not null;
        bool verified = hostSelection.IsConnected;
        ConnectionTitle.Text = installed ? "Your IPKeep connection." : "Set up this computer.";
        ConnectionSubtitle.Text = !installed
            ? "Install the background service and verify your token to finish setup."
            : verified ? "Manage your hostnames and background updates."
            : "The service is installed. Verify your token to connect your hostnames.";

        ServiceSetupStatus.Text = needsRepair ? "Needs repair" : !installed ? "Not installed"
            : serviceStatus == ServiceControllerStatus.Running ? "Installed and running" : "Installed · updates not running";
        ServiceSetupDot.Style = (Style)Application.Current.Resources[installed && !needsRepair ? "SuccessStatusDot" : "WarningStatusDot"];
        ServiceSetupHint.Text = needsRepair ? "Follow the repair steps above to restore background updates." : !installed
            ? "Install the service so IPKeep can update your hostnames even when this window is closed. Then connect your account below."
            : serviceStatus == ServiceControllerStatus.Running
                ? "IPKeep keeps updating your hostnames when you close this window or sign out."
                : "Connect your account below and save to enable background updates.";
        InstallServiceButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        InstallServiceButton.IsEnabled = !busy && !installed;
        // Make the first required action explicit; elevation returns here without installing anything.
        InstallServiceButton.Content = DeploymentSecurity.IsAdministrator ? "Install service" : "Allow changes to install service";

        TokenSetupStatus.Text = verified ? "Token verified" : TokenBox.Password.Length == 0 ? "Token required" : "Token not verified";
        TokenSetupDot.Style = (Style)Application.Current.Resources[verified ? "SuccessStatusDot" : "WarningStatusDot"];
        SaveButton.Content = installed ? "Save and enable updates" : "Install service and enable updates";
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (!DeploymentSecurity.IsAdministrator)
        {
            Elevate_Click(sender, e);
            return;
        }
        await RunAction(async () =>
        {
            // Another instance may have installed it since the last status refresh.
            await Task.Run(() =>
            {
                if (ServiceManager.GetStatus() is null) ServiceManager.Install(AppContext.BaseDirectory);
            });
            ShowFeedback(hostSelection.IsConnected
                ? "Background service installed. Choose your hostnames and Save and enable updates to finish setup."
                : "Background service installed. Add your token and load your hostnames to finish setup.");
        });
    }
}
