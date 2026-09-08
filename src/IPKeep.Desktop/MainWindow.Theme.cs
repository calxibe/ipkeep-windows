using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace IPKeep.Desktop;

public sealed partial class MainWindow
{
    private UISettings? themeSettings;
    private AccessibilitySettings? accessibilitySettings;
    private readonly BitmapImage lightLogo = new(new Uri("ms-appx:///Assets/Logo.png"));
    private readonly BitmapImage darkLogo = new(new Uri("ms-appx:///Assets/LogoDark.png"));

    private void InitializeTheme()
    {
        try
        {
            themeSettings = new UISettings();
            themeSettings.ColorValuesChanged += WindowsColors_Changed;
            accessibilitySettings = new AccessibilitySettings();
            accessibilitySettings.HighContrastChanged += WindowsContrast_Changed;
        }
        catch (Exception) { /* An unavailable Windows preference falls back to light. */ }
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        ApplyWindowsTheme();
    }

    private void WindowsColors_Changed(UISettings sender, object args) => QueueThemeRefresh();
    private void WindowsContrast_Changed(AccessibilitySettings sender, object args) => QueueThemeRefresh();

    private void QueueThemeRefresh() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!windowClosed) ApplyWindowsTheme();
    });

    private void ApplyWindowsTheme()
    {
        var theme = ElementTheme.Light;
        try
        {
            // Windows exposes a light foreground when apps should use a dark background.
            // Read it on the UI thread, including when Windows changes its colors at runtime.
            if (themeSettings is not null)
            {
                var foreground = themeSettings.GetColorValue(UIColorType.Foreground);
                if (5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128)
                    theme = ElementTheme.Dark;
            }
        }
        catch (Exception) { /* Keep the initial light theme if the preference cannot be read. */ }
        Root.RequestedTheme = theme;
        UpdateWindowColors();
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => UpdateWindowColors();

    private void UpdateWindowColors()
    {
        if (windowClosed) return;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        BrandLogo.Source = dark ? darkLogo : lightLogo;
        bool highContrast = false;
        try { highContrast = accessibilitySettings?.HighContrast == true; }
        catch (Exception) { }
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[highContrast ? "HighContrast" : dark ? "Dark" : "Light"];
        Windows.UI.Color Color(string key) => ((SolidColorBrush)palette[key]).Color;

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = titleBar.ButtonBackgroundColor = Color("Surface");
        titleBar.ForegroundColor = titleBar.ButtonForegroundColor = Color("Ink");
        titleBar.InactiveBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Color("Surface");
        titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = Color("Muted");
        titleBar.ButtonHoverBackgroundColor = titleBar.ButtonPressedBackgroundColor = Color("SelectedNavigation");
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = Color("Ink");
    }

    private void StopWatchingTheme()
    {
        if (themeSettings is not null) themeSettings.ColorValuesChanged -= WindowsColors_Changed;
        if (accessibilitySettings is not null) accessibilitySettings.HighContrastChanged -= WindowsContrast_Changed;
        Root.ActualThemeChanged -= Root_ActualThemeChanged;
    }

    // Explicit row hover also works with SelectionMode=None and selectable log text.
    // Reset when a container leaves the visual tree so virtualization cannot retain a highlight.
    private void ActivityRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((Border)sender).Style = (Style)Application.Current.Resources["HoveredActivityRow"];

    private void ActivityRow_Reset(object sender, RoutedEventArgs e) =>
        ((Border)sender).Style = (Style)Application.Current.Resources["ActivityRow"];
}
