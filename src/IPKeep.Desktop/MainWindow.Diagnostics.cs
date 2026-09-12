using IPKeep.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ServiceProcess;

namespace IPKeep.Desktop;

public sealed partial class MainWindow
{
    private bool diagnosticOpen;
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (diagnosticOpen || sender is not Button { Tag: string hostname }) return;
        try
        {
            string? token = UserTokenStore.Current.Load();
            if (token is null) { ShowFeedback("Open Settings and verify your API token before running diagnostics.", true); return; }
            diagnosticOpen = true;
            var dialog = new DiagnosticDialog(token, hostname, SelectedLookupProvider.Id, () => serviceStatus.HasValue ? serviceStatus == ServiceControllerStatus.Running : null, Root.XamlRoot);
            await dialog.ShowAsync();
        }
        catch (Exception ex) { ShowFeedback(ex is SettingsException or UpdateException ? ex.Message : "Diagnostics could not open. Verify your token in Settings and try again.", true); }
        finally { diagnosticOpen = false; }
    }
}
