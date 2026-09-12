using Microsoft.UI.Xaml;

namespace IPKeep.Desktop;

public partial class App : Application
{
    private Window? window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Contains(SavedTokenRestore.Argument))
        {
            Environment.ExitCode = SavedTokenRestore.Run(arguments);
            Exit();
            return;
        }
#if DEBUG
        if (arguments.Contains("--diagnostics-preview")) { window = DiagnosticPreview.Open(); return; }
#endif
        window = new MainWindow(arguments.Contains("--settings")); window.Activate();
    }
}
