using System.Numerics;
using IPKeep.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace IPKeep.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer accessHintTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private SpriteVisual? accessGlow;
    private DropShadow? accessGlowShadow;

    private void InitializeAccessHint()
    {
        // Controls can consume taps; disabled controls instead hit their containing card.
        SettingsPage.AddHandler(UIElement.TappedEvent, new TappedEventHandler(SettingsField_Tapped), true);
        // Popup controls may handle their opening gesture outside the Settings visual tree.
        HostnameFlyout.Opened += (_, _) => ShowAccessHint();
        IpLookupBox.DropDownOpened += (_, _) => ShowAccessHint();
        accessHintTimer.Tick += (_, _) => StopAccessHint();
        AllowChangesButton.SizeChanged += (_, _) => UpdateAccessGlow();
        Root.ActualThemeChanged += (_, _) => UpdateAccessGlow();
    }

    private void SettingsField_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (windowClosed || busy || DeploymentSecurity.IsAdministrator || SettingsPage.Visibility != Visibility.Visible) return;
        Point position = e.GetPosition(SettingsPage);
        FrameworkElement[] fields = [TokenBox, HostnameDropdown, IpLookupBox, IntervalBox, IPv6Switch, IgnoreBox];
        foreach (var field in fields)
        {
            if (!IsVisibleSettingsField(field)) continue;
            var bounds = field.TransformToVisual(SettingsPage).TransformBounds(new Rect(0, 0, field.ActualWidth, field.ActualHeight));
            if (!bounds.Contains(position)) continue;

            ShowAccessHint();
            break;
        }
    }

    private void ShowAccessHint()
    {
        if (windowClosed || busy || DeploymentSecurity.IsAdministrator || !AccessBanner.IsOpen ||
            SettingsPage.Visibility != Visibility.Visible) return;

        // The banner stays at the top. Keep the user's scroll position, focus and input intact.
        accessHintTimer.Stop();
        if (accessGlow is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(AccessGlowHost).Compositor;
            accessGlowShadow = compositor.CreateDropShadow();
            accessGlowShadow.BlurRadius = 20;
            accessGlow = compositor.CreateSpriteVisual();
            accessGlow.Shadow = accessGlowShadow;
            ElementCompositionPreview.SetElementChildVisual(AccessGlowHost, accessGlow);
        }
        UpdateAccessGlow();
        accessGlow.Opacity = 1;
        accessHintTimer.Start();
    }

    private void UpdateAccessGlow()
    {
        if (windowClosed || accessGlow is null || accessGlowShadow is null) return;
        // The glow is a sibling behind the opaque button, preserving its face and focus visuals.
        var origin = AccessGlowHost.TransformToVisual(AllowChangesButton).TransformPoint(default);
        accessGlow.Size = new Vector2((float)AllowChangesButton.ActualWidth, (float)AllowChangesButton.ActualHeight);
        accessGlow.Offset = new Vector3(-(float)origin.X, -(float)origin.Y, 0);
        accessGlowShadow.Color = ((SolidColorBrush)AllowChangesButton.Resources["AccessHintGlowBrush"]).Color;
        accessGlowShadow.Opacity = 0.85f;
    }

    private bool IsVisibleSettingsField(FrameworkElement field)
    {
        for (DependencyObject? element = field; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (element is FrameworkElement { ActualHeight: <= 0 } or FrameworkElement { ActualWidth: <= 0 }) return false;
            if (ReferenceEquals(element, SettingsPage)) return true;
        }
        return false;
    }

    private void StopAccessHint()
    {
        accessHintTimer.Stop();
        if (accessGlow is not null) accessGlow.Opacity = 0;
    }
}
