using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Security.Cryptography;
using System.Text.Json;
using IPKeep.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace IPKeep.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer appUpdateTimer = new() { Interval = AppUpdateClient.CheckInterval };
    private bool checkingAppUpdates;
    private string? dismissedAppVersion;
    private AppRelease? availableAppRelease;
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
    private string[] preferredHostnames = [];
    private bool settingHostnameSelection;
    private bool settingTokenField;
    private bool attemptedTokenRestore;
    private readonly bool openSettingsOnLaunch;

    public MainWindow(bool openSettings = false)
    {
        openSettingsOnLaunch = openSettings;
        InitializeComponent();
        AppVersionText.Text = $"IPKeep for Windows · {AppUpdateClient.CurrentVersion.Text}";
        InstalledVersionText.Text = $"Installed version: {AppUpdateClient.CurrentVersion.Text}";
        AppUpdateCheckHint.Text = "Checks when you open the app and every six hours while it is open. "
            + (AppUpdateClient.CurrentVersion.IsPreview ? "This preview checks for newer preview and stable releases." : "Checks stable releases only.");
        AppUpdateBanner.CloseButtonClick += (_, _) => dismissedAppVersion = availableAppRelease?.Version.Text;
        AppWindow.Resize(new SizeInt32(1100, 850));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "IPKeep.ico"));
        InitializeTheme();
        InitializeAccessHint();
        SetEditingState(DeploymentSecurity.IsAdministrator);
        RepairButton.IsEnabled = RemoveButton.IsEnabled = DeploymentSecurity.IsAdministrator;
        IpLookupBox.ItemsSource = lookupProbes.Choices;
        IpLookupBox.SelectedIndex = 0;
        LoadSettings(); RestoreRememberedToken(); ShowPage(openSettings ? "settings" : "overview"); Refresh();
        loadingSettings = false; UpdateLookupHint();
        refresh.Tick += (_, _) => Refresh(); refresh.Start();
        appUpdateTimer.Tick += async (_, _) => await CheckAppUpdatesAsync();
        appUpdateTimer.Start();
        Root.Loaded += InitialAddressLookup;
        Closed += (_, _) => { windowClosed = true; StopAccessHint(); StopWatchingTheme(); refresh.Stop(); appUpdateTimer.Stop(); lookupProbes.Cancel(); windowLifetime.Cancel(); windowLifetime.Dispose(); };
    }

    private async void InitialAddressLookup(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= InitialAddressLookup;
        if (openSettingsOnLaunch) await Task.WhenAll(DiscoverAddressesAsync(), OpenSettingsAsync(), CheckAppUpdatesAsync());
        else await Task.WhenAll(DiscoverAddressesAsync(), CheckAppUpdatesAsync());
    }

    private void AppUpdates_Click(object sender, RoutedEventArgs e) => ShowPage("app-updates");
    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => await CheckAppUpdatesAsync();

    private async void DownloadAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool opened = await Windows.System.Launcher.LaunchUriAsync(AppUpdateClient.ReleasesPage);
            if (!opened && !windowClosed) AppUpdateStatus.Text = "Could not open your browser. Visit github.com/calxibe/ipkeep-windows/releases to download the update.";
        }
        catch (Exception)
        {
            if (!windowClosed) AppUpdateStatus.Text = "Could not open your browser. Visit github.com/calxibe/ipkeep-windows/releases to download the update.";
        }
    }

    private async Task CheckAppUpdatesAsync()
    {
        if (windowClosed || checkingAppUpdates) return;
        checkingAppUpdates = true;
        CheckAppUpdatesButton.IsEnabled = false;
        AppUpdateStatus.Text = "Checking for updates…";
        try
        {
            using var http = AppUpdateClient.CreateHttpClient();
            var result = await new AppUpdateClient(http).CheckAsync(AppUpdateClient.CurrentVersion, windowLifetime.Token);
            if (windowClosed) return;
            // Keep an already offered release during an outage; a complete check can withdraw it.
            availableAppRelease = result.NewRelease ?? (result.CheckIncomplete ? availableAppRelease : null);
            AvailableAppUpdate.Visibility = availableAppRelease is null ? Visibility.Collapsed : Visibility.Visible;
            if (availableAppRelease is { } release)
            {
                AvailableVersionText.Text = $"IPKeep {release.Version.Text} is available";
                AppReleaseDateText.Text = $"Released {release.ReleaseDate.ToLocalTime():d MMM yyyy}";
                AppReleaseNotes.Text = release.ReleaseNotes;
                AppUpdateBanner.Title = $"IPKeep {release.Version.Text} is available";
                AppUpdateBanner.IsOpen = AppUpdatesPage.Visibility != Visibility.Visible && dismissedAppVersion != release.Version.Text;
                AppUpdateStatus.Text = result.CheckIncomplete
                    ? "A newer version was found. Some release information could not be refreshed; try again later."
                    : "A newer version is ready to download.";
            }
            else
            {
                AppUpdateBanner.IsOpen = false;
                AppReleaseNotes.Text = "";
                AppUpdateStatus.Text = result.CheckIncomplete ? "Could not check for updates. Check your connection and try again."
                    : result.HasPublishedRelease ? "You're up to date."
                    : "No release is currently published for this channel.";
            }
            if (!result.CheckIncomplete) AppUpdateStatus.Text += $" Last checked {DateTime.Now:g}.";
        }
        catch (OperationCanceledException) when (windowClosed) { }
        finally
        {
            checkingAppUpdates = false;
            if (!windowClosed) CheckAppUpdatesButton.IsEnabled = true;
        }
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
                    preferredHostnames = settings.Hostnames ?? [];
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
        AppUpdatesPage.Visibility = page == "app-updates" ? Visibility.Visible : Visibility.Collapsed;
        AccessBanner.IsOpen = page == "settings" && !DeploymentSecurity.IsAdministrator;
        if (!AccessBanner.IsOpen) StopAccessHint();
        AppUpdateBanner.IsOpen = page != "app-updates" && availableAppRelease is not null && dismissedAppVersion != availableAppRelease.Version.Text;
        foreach (var (button, name) in new[] { (OverviewNav, "overview"), (SettingsNav, "settings"), (ActivityNav, "activity"), (AppUpdatesNav, "app-updates") })
            button.Style = (Style)Application.Current.Resources[name == page ? "SelectedNavButton" : "NavButton"];
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
        UpdateSetupState();
    }

    private bool RememberToken(string token)
    {
        try { UserTokenStore.Current.Save(token); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { ShowFeedback("Windows could not remember your token. You may need to enter it again next time.", true); return false; }
    }

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (settingTokenField || HostnameList is null) return;
        preferredHostnames = [];
        hostSelection.Reset(); ClearHostnameChoices();
        HostListHint.Text = "Load the hostnames for this token before selecting them.";
        TokenHint.Text = "Load your hostnames to verify and securely remember this token for your Windows user.";
        SetEditingState(!busy && DeploymentSecurity.IsAdministrator);
    }

    private string[] SelectedHostnames => HostnameList.SelectedItems.OfType<string>().ToArray();
    private bool HasValidHostnameSelection => hostSelection.IsConnected
        && HostnameList.SelectedItems.Count is >= 1 and <= ClientSettings.MaximumHostnames;

    private void ClearHostnameChoices()
    {
        settingHostnameSelection = true;
        try { HostnameList.ItemsSource = null; }
        finally { settingHostnameSelection = false; }
        HostnameFlyout.Hide();
        UpdateHostnameSelection();
    }

    private void Hostnames_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (settingHostnameSelection) return;
        settingHostnameSelection = true;
        try
        {
            // Also enforce the limit for keyboard selection, not only disabled rows.
            foreach (var added in e.AddedItems.Reverse())
            {
                if (HostnameList.SelectedItems.Count <= ClientSettings.MaximumHostnames) break;
                HostnameList.SelectedItems.Remove(added);
            }
        }
        finally { settingHostnameSelection = false; }
        preferredHostnames = SelectedHostnames;
        UpdateHostnameSelection();
    }

    private void Hostnames_Opening(object sender, object e)
    {
        HostnameFlyoutContent.Width = Math.Max(220, Math.Min(620, HostnameDropdown.ActualWidth - 24));
        UpdateHostnameSelection();
    }

    private void Hostnames_ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.IsEnabled = args.InRecycleQueue || (!busy &&
            (HostnameList.SelectedItems.Count < ClientSettings.MaximumHostnames || HostnameList.SelectedItems.Contains(args.Item)));
    }

    private void UpdateHostnameSelection()
    {
        var selected = SelectedHostnames;
        HostnameSummary.Text = selected.Length switch { 0 => "Choose hostnames", 1 => selected[0], _ => $"{selected.Length} hostnames selected" };
        ToolTipService.SetToolTip(HostnameDropdown, selected.Length == 0 ? "Choose up to 5 hostnames" : string.Join(Environment.NewLine, selected));
        HostnameSelectionCount.Text = selected.Length > ClientSettings.MaximumHostnames
            ? $"{selected.Length} selected. Reduce your selection to 5 before saving."
            : $"{selected.Length} of 5 selected" + (selected.Length == ClientSettings.MaximumHostnames ? ". Uncheck one to choose another." : "");
        foreach (var item in HostnameList.Items)
            if (HostnameList.ContainerFromItem(item) is ListViewItem container)
                container.IsEnabled = !busy && (selected.Length < ClientSettings.MaximumHostnames || HostnameList.SelectedItems.Contains(item));
        SaveButton.IsEnabled = !busy && DeploymentSecurity.IsAdministrator && HasValidHostnameSelection;
    }

    private async void LoadHosts_Click(object sender, RoutedEventArgs e) => await LoadHostChoicesAsync();

    private async Task LoadHostChoicesAsync() => await RunAction(async () =>
    {
        var previous = preferredHostnames;
        hostSelection.Reset(); ClearHostnameChoices();
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
            settingHostnameSelection = true;
            try
            {
                HostnameList.ItemsSource = hostSelection.Hostnames;
                foreach (var name in hostSelection.RestoreSelection(previous)) HostnameList.SelectedItems.Add(name);
            }
            finally { settingHostnameSelection = false; }
            preferredHostnames = SelectedHostnames;
            HostListHint.Text = hostSelection.Hostnames.Count == 0
                ? "No active hostnames found. Create or enable a hostname in the admin panel, then load this list again."
                : hostSelection.Hostnames.Count == 1 ? "Your hostname is selected automatically. Create or manage hostnames in the admin panel."
                : "Choose up to 5 hostnames to update with this computer's IP address. Create or manage hostnames in the admin panel.";
            if (preferredHostnames.Length > ClientSettings.MaximumHostnames)
                HostListHint.Text = "Your older setup has more than 5 hostnames. Open the list and reduce your selection before saving.";
            else if (previous.Except(hostSelection.Hostnames, StringComparer.Ordinal).Any())
                HostListHint.Text += " Some previous choices are no longer active. Review the selection before saving.";
        }
        catch
        {
            hostSelection.Reset();
            if (!windowClosed) { ClearHostnameChoices(); HostListHint.Text = "The hostname list could not be loaded. Check your token and try again."; }
            throw;
        }
    });

    // Value equality lets the list keep its containers when nothing about the hosts changed.
    private sealed record HostStatusRow(string Hostname, string Message);

    private ServiceSnapshot cachedSnapshot = new();
    private ClientSettings? cachedSettings;
    private (DateTime, long)? statusStamp, settingsStamp, logStamp;
    private bool logRead;
    private HostStatusRow[] hostRows = [];
    private ServiceControllerStatus? renderedStatus;
    private bool rendered;

    private void Refresh(bool force = false)
    {
        if (busy) return;
        try
        {
            serviceStatus = ServiceManager.GetStatus();
            UpdateMaintenanceVisibility();
            UpdateSetupState();

            // The status file changes about as often as a check runs, and the settings file
            // only when the user saves. Re-reading and re-parsing both every two seconds, plus
            // re-rendering from them, is work the screen cannot show. Compare the file stamps
            // first and skip everything when the inputs and the service state are unchanged.
            var currentStatusStamp = FileStore.Stamp(AppPaths.StatusFile);
            var currentSettingsStamp = FileStore.Stamp(AppPaths.SettingsFile);
            bool inputsChanged = force || !rendered || currentStatusStamp != statusStamp
                || currentSettingsStamp != settingsStamp || serviceStatus != renderedStatus;
            RefreshLog(force);
            if (!inputsChanged) return;
            if (force || currentStatusStamp != statusStamp) cachedSnapshot = FileStore.ReadJson<ServiceSnapshot>(AppPaths.StatusFile) ?? new();
            if (force || currentSettingsStamp != settingsStamp) cachedSettings = FileStore.ReadJson<ClientSettings>(AppPaths.SettingsFile);
            statusStamp = currentStatusStamp; settingsStamp = currentSettingsStamp;
            renderedStatus = serviceStatus; rendered = true;

            var snapshot = cachedSnapshot;
            var settings = cachedSettings;
            bool running = serviceStatus == ServiceControllerStatus.Running;
            bool configured = settings?.Hostnames.Length > 0;
            StatusLabel.Text = serviceStatus is null ? "Not installed" : running ? (snapshot.State == "Checking" ? "Checking now" : "Background service running") : "Updates paused";
            bool pendingDns = snapshot.LastCheck?.Hosts.Any(x => x.Success && !x.DnsUpdated) == true;
            bool attention = snapshot.State == "Needs attention";
            bool fullySkipped = snapshot.LastCheck is { Skipped: true, Hosts.Length: 0 };
            StatusTitle.Text = !configured ? "Keep your connections current." : !running ? "Ready when you are." : attention ? "Your connection needs attention." : fullySkipped ? "Update skipped on this network." : pendingDns ? "IP recorded. DNS publishing pending." : snapshot.LastCheck?.Success == true ? "Your connection is up to date." : "Getting your connection ready.";
            StatusMessage.Text = !configured ? "Add your IPKeep API token in Settings to connect your hostnames." : !running ? "Enable updates to keep these hostnames current in the background." : snapshot.Message;
            StatusDot.Style = (Style)Application.Current.Resources[!running || fullySkipped ? "NeutralStatusDot" : attention || pendingDns ? "WarningStatusDot" : "SuccessStatusDot"];
            PrimaryAction.Content = !configured ? "Set up IPKeep" : running ? "Check now" : "Enable updates";
            PrimaryAction.IsEnabled = !busy && (!running || DeploymentSecurity.IsAdministrator) && snapshot.State != "Checking";
            // A stale saved Checking state must not disable setup after a crash/stop.
            if (!running) PrimaryAction.IsEnabled = !busy;
            PauseButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PauseButton.IsEnabled = DeploymentSecurity.IsAdministrator && !busy;
            LastCheckText.Text = FormatTime(snapshot.LastCheck?.CheckedAt);
            NextCheckText.Text = running ? snapshot.State == "Checking" ? "In progress" : FormatTime(snapshot.NextCheck) : "—";
            var rows = settings?.Hostnames.Select(name => new HostStatusRow(
                name,
                snapshot.LastCheck?.Hosts.FirstOrDefault(h => h.Hostname == name)?.Message ?? "Waiting for its first update."
            )).ToArray() ?? [];
            // Records compare by value, so an unchanged list leaves the ItemsControl alone
            // instead of rebuilding every container.
            if (!rows.SequenceEqual(hostRows)) { hostRows = rows; HostsList.ItemsSource = rows; }
            NoHostsText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or Win32Exception)
        { StatusLabel.Text = "Status unavailable"; StatusMessage.Text = "The service status could not be read. Open Settings or check the log folder."; }
    }

    // Reading the log tail seeks and splits up to 100 KB. Only do it when the file moved.
    private void RefreshLog(bool force)
    {
        var stamp = FileStore.Stamp(AppPaths.LogFile);
        if (!force && logRead && stamp == logStamp) return;
        logStamp = stamp; logRead = true;
        var lines = ActivityLog.ReadTail(AppPaths.LogFile);
        var signature = string.Join('\n', lines);
        if (signature != lastLog) { lastLog = signature; logLines = lines; ApplyLogFilter(); }
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
            { hostSelection.Reset(); ClearHostnameChoices(); }
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
                Refresh(force: true);
            }
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunAction(async () =>
        {
            if (double.IsNaN(IntervalBox.Value) || IntervalBox.Value != Math.Truncate(IntervalBox.Value)) throw new SettingsException("Enter a whole number of minutes.");
            using var http = NetworkClients.Create(1024 * 1024);
            var connection = await hostSelection.SelectAsync(new ClientSettings { IntervalMinutes = (int)IntervalBox.Value, EnableIPv6 = IPv6Switch.IsOn, IpLookupProviderId = SelectedLookupProvider.Id, IgnoredNetworks = Lines(IgnoreBox.Text) }, SelectedHostnames, new IpKeepClient(http, new PreviewLog()), windowLifetime.Token);
            await Task.Run(() =>
            {
                ServiceManager.SaveAndEnable(AppContext.BaseDirectory, connection.Settings, connection.Token);
                new ActivityLog(AppPaths.LogFile).Write("INFO", "Manager: connection saved; background updates enabled.");
            });
            if (windowClosed) return;
            ShowSavedToken(connection.Token, RememberToken(connection.Token));
            LoadSettings(); ShowPage("overview");
            string savedHosts = connection.Settings.Hostnames.Length == 1 ? "hostname" : $"{connection.Settings.Hostnames.Length} hostnames";
            ShowFeedback($"Your token and {savedHosts} are saved securely. Your first check is starting.");
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
        serviceStatus is not null ? Visibility.Visible : Visibility.Collapsed;

    private void SetEditingState(bool enabled)
    {
        UpdateMaintenanceVisibility();
        UpdateSetupState();
        IgnoreBox.IsReadOnly = !enabled;
        TokenBox.IsEnabled = LoadHostsButton.IsEnabled = !busy;
        IntervalBox.IsEnabled = IPv6Switch.IsEnabled = enabled;
        bool hasVerifiedHosts = hostSelection.IsConnected && hostSelection.Hostnames.Count > 0;
        HostnameSection.Visibility = hasVerifiedHosts ? Visibility.Visible : Visibility.Collapsed;
        SingleHostnamePanel.Visibility = hasVerifiedHosts && hostSelection.Hostnames.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        SingleHostnameText.Text = hostSelection.Hostnames.Count == 1 ? hostSelection.Hostnames[0] : "";
        HostnameLabel.Text = hostSelection.Hostnames.Count == 1 ? "Hostname for this computer" : "Hostnames for this computer";
        HostnameDropdown.Visibility = hasVerifiedHosts && hostSelection.Hostnames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HostnameDropdown.IsEnabled = HostnameList.IsEnabled = !busy && hasVerifiedHosts;
        if (busy || !hasVerifiedHosts) HostnameFlyout.Hide();
        HostOptionsPanel.Visibility = hasVerifiedHosts ? Visibility.Visible : Visibility.Collapsed;
        if (!hasVerifiedHosts) { IpLookupBox.IsDropDownOpen = false; lookupProbes.Cancel(); }
        IpLookupBox.IsEnabled = !busy && hasVerifiedHosts;
        IPv6Switch.IsEnabled = enabled && SelectedLookupProvider.SupportsIPv6;
        UpdateHostnameSelection();
        SaveButton.IsEnabled = enabled && HasValidHostnameSelection;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (LogList is not null) ApplyLogFilter(); }
    private void ApplyLogFilter()
    {
        var rows = logLines.Select(line => ActivityRow.Parse(line)).Where(row => row.Matches(LogFilterBox.Text ?? "")).ToArray();
        LogList.ItemsSource = rows; EmptyLogText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLogText.Text = logLines.Length == 0 ? "Activity will appear after your first check." : "No activity matches this filter.";
    }
}
