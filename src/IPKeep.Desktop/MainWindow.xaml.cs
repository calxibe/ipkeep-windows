using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Security.Cryptography;
using System.Text.Json;
using IPKeep.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace IPKeep.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool busy;
    private string[] logLines = [];
    private string lastLog = "";
    private ServiceControllerStatus? serviceStatus;
    private readonly CancellationTokenSource windowLifetime = new();
    private CancellationTokenSource? addressLookup;
    private int addressGeneration;
    private readonly IpLookupProbeSession lookupProbes = new();
    private bool loadingSettings = true;
    private bool windowClosed;
    private readonly HostSelectionSession hostSelection = new();
    private ClientSettings? savedSettings;
    private bool settingTokenField;
    private bool attemptedTokenRestore;
    private readonly bool openSettingsOnLaunch;

    public MainWindow(bool openSettings = false)
    {
        openSettingsOnLaunch = openSettings;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1100, 850));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "IPKeep.ico"));
        AccessBanner.IsOpen = !DeploymentSecurity.IsAdministrator;
        SetEditingState(DeploymentSecurity.IsAdministrator);
        RepairButton.IsEnabled = RemoveButton.IsEnabled = DeploymentSecurity.IsAdministrator;
        IpLookupBox.ItemsSource = lookupProbes.Choices;
        IpLookupBox.SelectedIndex = 0;
        LoadSettings(); RestoreRememberedToken(); ShowPage(openSettings ? "settings" : "overview"); Refresh();
        loadingSettings = false; UpdateLookupHint();
        refresh.Tick += (_, _) => Refresh(); refresh.Start();
        Root.Loaded += InitialAddressLookup;
        Closed += (_, _) => { windowClosed = true; refresh.Stop(); lookupProbes.Cancel(); windowLifetime.Cancel(); windowLifetime.Dispose(); };
    }

    private async void InitialAddressLookup(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= InitialAddressLookup;
        if (openSettingsOnLaunch) await Task.WhenAll(DiscoverAddressesAsync(), OpenSettingsAsync());
        else await DiscoverAddressesAsync();
    }

    private async void RefreshAddresses_Click(object sender, RoutedEventArgs e) => await DiscoverAddressesAsync();

    private async Task DiscoverAddressesAsync()
    {
        if (windowClosed) return;
        addressLookup?.Cancel();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        addressLookup = run;
        int current = ++addressGeneration;
        var provider = SelectedLookupProvider;
        RefreshAddressesButton.IsEnabled = false;
        IPv4Text.Text = IPv6Text.Text = "Checking…";
        IPv4Hint.Text = IPv6Hint.Text = "Looking up this connection";
        UpdateLookupHint();
        var cancellationToken = run.Token;
        try
        {
            using var v4 = NetworkClients.CreateDiscovery(ipv6: false);
            using var v6 = NetworkClients.CreateDiscovery(ipv6: true);
            var resolver = new PublicIpResolver(v4, v6, new PreviewLog());
            // Each result is displayed immediately; a missing IPv6 connection does not delay IPv4.
            await Task.WhenAll(LookupAsync(false, IPv4Text, IPv4Hint), LookupAsync(true, IPv6Text, IPv6Hint));

            async Task LookupAsync(bool ipv6, TextBlock addressText, TextBlock hintText)
            {
                if (ipv6 && !provider.SupportsIPv6)
                {
                    addressText.Text = "Not supported";
                    hintText.Text = $"{provider.Name} provides IPv4 only.";
                    return;
                }
                try
                {
                    string address = await resolver.ResolveAsync(ipv6, cancellationToken, provider.Id);
                    if (windowClosed || current != addressGeneration) return;
                    addressText.Text = address;
                    hintText.Text = $"Detected at {DateTime.Now:HH:mm:ss}";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (windowClosed || current != addressGeneration) return;
                    addressText.Text = "Not detected";
                    hintText.Text = ex is UpdateException ? ex.Message : "The address lookup failed. Refresh to try again.";
                }
            }
        }
        finally
        {
            if (ReferenceEquals(addressLookup, run)) addressLookup = null;
            if (!windowClosed && current == addressGeneration) RefreshAddressesButton.IsEnabled = true;
        }
    }

    private IpLookupProvider SelectedLookupProvider => (IpLookupBox.SelectedItem as IpLookupChoice)?.Provider ?? IpLookupProviders.Get(IpLookupProviders.DefaultId);

    private async void IpLookup_Opened(object sender, object e)
    {
        if (windowClosed || !hostSelection.IsConnected || hostSelection.Hostnames.Count == 0) return;
        using var http = NetworkClients.CreateDiscovery(false, TimeSpan.FromSeconds(10));
        await lookupProbes.RefreshAsync(new PublicIpResolver(http, http, new PreviewLog()), windowLifetime.Token);
    }

    private async void IpLookup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || windowClosed) return;
        if (!SelectedLookupProvider.SupportsIPv6) IPv6Switch.IsOn = false;
        SetEditingState(!busy && DeploymentSecurity.IsAdministrator);
        UpdateLookupHint();
        await DiscoverAddressesAsync();
    }

    private void UpdateLookupHint()
    {
        var provider = SelectedLookupProvider;
        bool unsaved = provider.Id != (savedSettings?.IpLookupProviderId ?? IpLookupProviders.DefaultId);
        LookupSourceText.Text = $"IP lookup: {provider.Name}. " + (unsaved ? "Preview; save in Settings to use for updates." : "No token needed.");
        IpLookupHint.Text = "Open the list to compare IPv4 addresses. VPN routing can give each service a different address. "
            + (unsaved ? "Save settings to use this selection for background updates." : "Your selected service is used for background updates after setup.");
        IPv6SettingHint.Text = provider.SupportsIPv6
            ? "Enable IPv6 only if this computer has a public IPv6 connection. Missing or ignored addresses never clear saved addresses."
            : $"{provider.Name} supports IPv4 only. IPv6 updates are off; existing IPv6 records are kept.";
    }

    // Read-only address previews do not require or write the administrator-protected service log.
    private sealed class PreviewLog : IActivityLog { public void Write(string level, string message) { } }

    private void LoadSettings()
    {
        bool wasLoading = loadingSettings;
        loadingSettings = true;
        try
        {
            var settings = FileStore.ReadJson<ClientSettings>(AppPaths.SettingsFile);
            savedSettings = settings;
            if (settings is not null)
            {
                if (!hostSelection.IsConnected)
                {
                    HostnameBox.ItemsSource = settings.Hostnames;
                    HostnameBox.SelectedItem = settings.Hostnames.FirstOrDefault();
                }
                IntervalBox.Value = settings.IntervalMinutes; IPv6Switch.IsOn = settings.EnableIPv6;
                var provider = IpLookupProviders.Get(settings.IpLookupProviderId);
                IpLookupBox.SelectedItem = lookupProbes.Choices.Single(choice => choice.Provider.Id == provider.Id);
                IgnoreBox.Text = string.Join(Environment.NewLine, settings.IgnoredNetworks);
                if (TokenBox.Password.Length == 0)
                {
                    TokenBox.PlaceholderText = "Saved token — restore to load your hostnames";
                    TokenHint.Text = "Your token is saved. Windows may ask once to restore it for this Windows user.";
                }
                if (settings.Hostnames.Length > 1) HostListHint.Text = "This setup selects one hostname. Saving will replace your previous hostname selections.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SettingsException)
        { ShowFeedback("Saved settings could not be read. Enter your connection again or repair the service.", true); }
        finally { loadingSettings = wasLoading; SetEditingState(!busy && DeploymentSecurity.IsAdministrator); UpdateLookupHint(); }
    }

    private void ShowPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, name) in new[] { (OverviewNav, "overview"), (SettingsNav, "settings"), (ActivityNav, "activity") })
        { button.Background = new SolidColorBrush(name == page ? ColorHelper.FromArgb(255, 235, 241, 253) : Colors.Transparent); button.Foreground = new SolidColorBrush(name == page ? ColorHelper.FromArgb(255, 40, 95, 213) : ColorHelper.FromArgb(255, 100, 116, 139)); }
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage("overview");
    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();
    private void Activity_Click(object sender, RoutedEventArgs e) => ShowPage("activity");

    private async Task OpenSettingsAsync()
    {
        ShowPage("settings");
        if (!busy && (TokenBox.Password.Length > 0 || (savedSettings is not null && !attemptedTokenRestore)))
            await LoadHostChoicesAsync();
    }

    private void RestoreRememberedToken()
    {
        try
        {
            string? token = UserTokenStore.Current.Load();
            bool remembered = token is not null;
            if (token is null && savedSettings is not null && DeploymentSecurity.IsAdministrator)
            {
                token = SettingsStore.Load().Token;
                remembered = RememberToken(token);
            }
            if (token is not null) ShowSavedToken(token, remembered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SettingsException)
        { TokenHint.Text = "Windows could not read the remembered token. Restore the saved token or paste it again."; }
    }

    private void ShowSavedToken(string token, bool remembered = true)
    {
        settingTokenField = true;
        try { TokenBox.Password = token; } finally { settingTokenField = false; }
        TokenBox.PlaceholderText = "Paste the token from the admin panel";
        TokenHint.Text = remembered
            ? "Your token is remembered securely for this Windows user. Replace it to connect another account."
            : "Your token is loaded, but could not be remembered. You may need to enter it again next time.";
        ConnectionTitle.Text = "Your IPKeep connection.";
        ConnectionSubtitle.Text = remembered ? "Your saved token is restored. Choose a hostname from your account." : "Choose a hostname from your account.";
    }

    private bool RememberToken(string token)
    {
        try { UserTokenStore.Current.Save(token); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { ShowFeedback("Windows could not remember your token. You may need to enter it again next time.", true); return false; }
    }

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (settingTokenField || HostnameBox is null) return;
        hostSelection.Reset(); HostnameBox.ItemsSource = null;
        HostnameBox.PlaceholderText = "Load your hostnames first";
        HostListHint.Text = "Load the hostnames for this token before selecting one.";
        TokenHint.Text = "Load your hostnames to verify and securely remember this token for your Windows user.";
        ConnectionTitle.Text = "Connect this computer.";
        ConnectionSubtitle.Text = "Add your token, then choose a hostname from your account.";
        SetEditingState(!busy && DeploymentSecurity.IsAdministrator);
    }

    private void Hostname_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SaveButton is not null) SaveButton.IsEnabled = !busy && DeploymentSecurity.IsAdministrator && hostSelection.IsConnected && HostnameBox.SelectedItem is string;
    }

    private async void LoadHosts_Click(object sender, RoutedEventArgs e) => await LoadHostChoicesAsync();

    private async Task LoadHostChoicesAsync() => await RunAction(async () =>
    {
        string? previous = HostnameBox.SelectedItem as string ?? savedSettings?.Hostnames.FirstOrDefault();
        hostSelection.Reset(); HostnameBox.ItemsSource = null;
        SetEditingState(false);
        string token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            try { token = UserTokenStore.Current.Load() ?? ""; }
            catch (Exception ex) when (ex is CryptographicException or SettingsException) { token = ""; }
            if (token.Length == 0 && savedSettings is not null)
            {
                attemptedTokenRestore = true;
                TokenHint.Text = "Restoring your saved token for this Windows user…";
                if (DeploymentSecurity.IsAdministrator) token = SettingsStore.Load().Token;
                else
                {
                    await SavedTokenRestore.RestoreAsync(windowLifetime.Token);
                    token = UserTokenStore.Current.Load() ?? "";
                }
            }
            if (token.Length == 0) throw new SettingsException("Paste your API token from the IPKeep admin panel first.");
        }
        token = ClientSettings.ValidateToken(token);
        settingTokenField = true;
        try { TokenBox.Password = token; } finally { settingTokenField = false; }
        HostListHint.Text = "Loading your account's active hostnames…";
        using var http = NetworkClients.Create(1024 * 1024);
        try
        {
            if (!await hostSelection.ConnectAsync(token, new IpKeepClient(http, new PreviewLog()), windowLifetime.Token) || windowClosed) return;
            ShowSavedToken(token, RememberToken(token));
            HostnameBox.ItemsSource = hostSelection.Hostnames;
            HostnameBox.PlaceholderText = "Choose a hostname";
            if (previous is not null && hostSelection.Hostnames.Contains(previous)) HostnameBox.SelectedItem = previous;
            else if (hostSelection.Hostnames.Count == 1) HostnameBox.SelectedIndex = 0;
            HostListHint.Text = hostSelection.Hostnames.Count == 0
                ? "No active hostnames found. Create or enable a hostname in the admin panel, then load this list again."
                : "Choose an existing hostname for this computer. Create or manage hostnames in the admin panel.";
            if (savedSettings?.Hostnames.Length > 1) HostListHint.Text += " Saving replaces your previous hostname selections.";
        }
        catch
        {
            hostSelection.Reset();
            if (!windowClosed) { HostnameBox.ItemsSource = null; HostListHint.Text = "The hostname list could not be loaded. Check your token and try again."; }
            throw;
        }
    });

    private void Refresh()
    {
        if (busy) return;
        try
        {
            serviceStatus = ServiceManager.GetStatus();
            UpdateMaintenanceVisibility();
            var snapshot = FileStore.ReadJson<ServiceSnapshot>(AppPaths.StatusFile) ?? new();
            var settings = FileStore.ReadJson<ClientSettings>(AppPaths.SettingsFile);
            bool running = serviceStatus == ServiceControllerStatus.Running;
            bool configured = settings?.Hostnames.Length > 0;
            StatusLabel.Text = serviceStatus is null ? "Not installed" : running ? (snapshot.State == "Checking" ? "Checking now" : "Background service running") : "Updates paused";
            bool pendingDns = snapshot.LastCheck?.Hosts.Any(x => x.Success && !x.DnsUpdated) == true;
            bool attention = snapshot.State == "Needs attention";
            bool fullySkipped = snapshot.LastCheck is { Skipped: true, Hosts.Length: 0 };
            StatusTitle.Text = !configured ? "Keep your connections current." : !running ? "Ready when you are." : attention ? "Your connection needs attention." : fullySkipped ? "Update skipped on this network." : pendingDns ? "IP recorded. DNS publishing pending." : snapshot.LastCheck?.Success == true ? "Your connection is up to date." : "Getting your connection ready.";
            StatusMessage.Text = !configured ? "Add your IPKeep API token in Settings, then choose a hostname from your account." : !running ? "Enable updates to keep these hostnames current in the background." : snapshot.Message;
            StatusDot.Fill = new SolidColorBrush(!running || fullySkipped ? ColorHelper.FromArgb(255, 100, 116, 139) : attention || pendingDns ? ColorHelper.FromArgb(255, 180, 110, 10) : ColorHelper.FromArgb(255, 24, 128, 85));
            PrimaryAction.Content = !configured ? "Set up IPKeep" : running ? "Check now" : "Enable updates";
            PrimaryAction.IsEnabled = !busy && (!running || DeploymentSecurity.IsAdministrator) && snapshot.State != "Checking";
            // A stale saved Checking state must not disable setup after a crash/stop.
            if (!running) PrimaryAction.IsEnabled = !busy;
            PauseButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PauseButton.IsEnabled = DeploymentSecurity.IsAdministrator && !busy;
            LastCheckText.Text = FormatTime(snapshot.LastCheck?.CheckedAt);
            NextCheckText.Text = running ? snapshot.State == "Checking" ? "In progress" : FormatTime(snapshot.NextCheck) : "—";
            HostsList.ItemsSource = settings?.Hostnames.Select(name => new
            {
                Hostname = name,
                Message = snapshot.LastCheck?.Hosts.FirstOrDefault(h => h.Hostname == name)?.Message ?? "Waiting for its first update."
            }).ToArray();
            NoHostsText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
            var lines = ActivityLog.ReadTail(AppPaths.LogFile);
            var signature = string.Join('\n', lines);
            if (signature != lastLog) { lastLog = signature; logLines = lines; ApplyLogFilter(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or Win32Exception)
        { StatusLabel.Text = "Status unavailable"; StatusMessage.Text = "The service status could not be read. Open Settings or check the log folder."; }
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd MMM, HH:mm:ss") ?? "—";
    private void ShowFeedback(string message, bool error = false)
    { Feedback.Title = error ? "Needs attention" : "IPKeep"; Feedback.Message = message; Feedback.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; Feedback.IsOpen = true; }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            if (SettingsPage.Visibility == Visibility.Visible) start.ArgumentList.Add("--settings");
            Process.Start(start);
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { ShowFeedback("Administrator access was cancelled. You can continue viewing status."); }
        catch (Exception) { ShowFeedback("Windows could not open IPKeep with administrator access.", true); }
    }

    private async Task RunAction(Func<Task> action)
    {
        if (busy) return;
        // Feedback belongs to the current attempt. Clear it before retrying so a
        // successful hostname lookup cannot leave an earlier restore error visible.
        // New failures (including remembering the token) can still show feedback.
        Feedback.IsOpen = false;
        busy = true; BusyRing.IsActive = true; SetEditingState(false);
        PrimaryAction.IsEnabled = PauseButton.IsEnabled = RepairButton.IsEnabled = RemoveButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (windowClosed) { }
        catch (Exception ex)
        {
            if (windowClosed) return;
            if (TokenBox.Password.Length == 0 && attemptedTokenRestore)
                TokenHint.Text = "Your token is saved. Choose Load my hostnames to restore it, or paste your token here.";
            if (ex is UpdateException { AuthenticationFailure: true })
            { hostSelection.Reset(); HostnameBox.ItemsSource = null; }
            string message = ex is SettingsException or UpdateException ? ex.Message : ex is Win32Exception { NativeErrorCode: 1223 }
                ? "Restoring the saved token was cancelled. Choose Load my hostnames to try again, or paste your token."
                : ex is UnauthorizedAccessException ? "Choose Allow changes to manage IPKeep, or check the installation permissions." : "The action could not be completed. Check the installation files and Windows service status, then try again.";
            ShowFeedback(message, true);
        }
        finally
        {
            busy = false;
            if (!windowClosed)
            {
                BusyRing.IsActive = false; SetEditingState(DeploymentSecurity.IsAdministrator);
                RepairButton.IsEnabled = RemoveButton.IsEnabled = DeploymentSecurity.IsAdministrator;
                Refresh();
            }
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunAction(async () =>
        {
            if (double.IsNaN(IntervalBox.Value) || IntervalBox.Value != Math.Truncate(IntervalBox.Value)) throw new SettingsException("Enter a whole number of minutes.");
            var connection = hostSelection.Select(new ClientSettings { IntervalMinutes = (int)IntervalBox.Value, EnableIPv6 = IPv6Switch.IsOn, IpLookupProviderId = SelectedLookupProvider.Id, IgnoredNetworks = Lines(IgnoreBox.Text) }, HostnameBox.SelectedItem as string);
            using var http = NetworkClients.Create(1024 * 1024);
            var available = await new IpKeepClient(http, new PreviewLog()).ListHostsAsync(connection.Token, windowLifetime.Token);
            if (!available.Contains(connection.Settings.Hostnames[0]))
            { hostSelection.Reset(); HostnameBox.ItemsSource = null; throw new SettingsException("That hostname is no longer active. Load your hostnames again and choose one."); }
            await Task.Run(() =>
            {
                ServiceManager.SaveAndEnable(AppContext.BaseDirectory, connection.Settings, connection.Token);
                new ActivityLog(AppPaths.LogFile).Write("INFO", "Manager: connection saved; background updates enabled.");
            });
            if (windowClosed) return;
            ShowSavedToken(connection.Token, RememberToken(connection.Token));
            LoadSettings(); ShowPage("overview"); ShowFeedback("Token and hostname saved securely. Your first check is starting.");
        });
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (savedSettings is null || serviceStatus is null) { await OpenSettingsAsync(); return; }
        if (!DeploymentSecurity.IsAdministrator) { ShowPage("settings"); ShowFeedback("Choose Allow changes to enable or check updates."); return; }
        await RunAction(() => Task.Run(() => { if (serviceStatus == ServiceControllerStatus.Running) ServiceManager.CheckNow(); else ServiceManager.Start(); }));
    }
    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunAction(async () => { await Task.Run(ServiceManager.Pause); ShowFeedback("Updates paused, including after restart. Choose Enable updates to resume."); });
    private async void Repair_Click(object sender, RoutedEventArgs e) => await RunAction(async () => { await Task.Run(() => ServiceManager.Install(AppContext.BaseDirectory)); ShowFeedback("Background service installed. Save your connection to enable updates."); });
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Remove the background service?", Content = "IPKeep will stop updating on this computer. Your saved connection and activity will be kept.", PrimaryButtonText = "Remove service", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAction(async () => { await Task.Run(ServiceManager.Uninstall); ShowFeedback("Background service removed. Saved settings and activity have been kept."); });
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(AppPaths.RuntimeDirectory)) { ShowFeedback("The log folder is created when you set up IPKeep."); return; }
        try { Process.Start(new ProcessStartInfo(AppPaths.RuntimeDirectory) { UseShellExecute = true }); }
        catch { ShowFeedback("Windows could not open the log folder.", true); }
    }
    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private void UpdateMaintenanceVisibility() => ServiceMaintenanceExpander.Visibility =
        serviceStatus is not null || hostSelection.IsConnected ? Visibility.Visible : Visibility.Collapsed;

    private void SetEditingState(bool enabled)
    {
        UpdateMaintenanceVisibility();
        IgnoreBox.IsReadOnly = !enabled;
        TokenBox.IsEnabled = LoadHostsButton.IsEnabled = !busy;
        IntervalBox.IsEnabled = IPv6Switch.IsEnabled = enabled;
        bool hasVerifiedHosts = hostSelection.IsConnected && hostSelection.Hostnames.Count > 0;
        HostOptionsPanel.Visibility = hasVerifiedHosts ? Visibility.Visible : Visibility.Collapsed;
        if (!hasVerifiedHosts) { IpLookupBox.IsDropDownOpen = false; lookupProbes.Cancel(); }
        IpLookupBox.IsEnabled = !busy && hasVerifiedHosts;
        IPv6Switch.IsEnabled = enabled && SelectedLookupProvider.SupportsIPv6;
        HostnameBox.IsEnabled = !busy && hostSelection.IsConnected && hostSelection.Hostnames.Count > 0;
        SaveButton.IsEnabled = enabled && hostSelection.IsConnected && HostnameBox.SelectedItem is string;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (LogList is not null) ApplyLogFilter(); }
    private void ApplyLogFilter()
    {
        var rows = logLines.Select(line => ActivityRow.Parse(line)).Where(row => row.Matches(LogFilterBox.Text ?? "")).ToArray();
        LogList.ItemsSource = rows; EmptyLogText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLogText.Text = logLines.Length == 0 ? "Activity will appear after your first check." : "No activity matches this filter.";
    }
}
