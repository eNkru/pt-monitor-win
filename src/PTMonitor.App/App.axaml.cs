using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PTMonitor.App.Services;

namespace PTMonitor.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Subscribed here, not in Program.Main: by this point the platform has
        // installed its real dispatcher. Touching Dispatcher.UIThread earlier
        // pins the default implementation and Dispatcher.MainLoop throws
        // PlatformNotSupportedException before the first frame.
        Dispatcher.UIThread.UnhandledException += (_, e) => Program.LogCrash("ui", e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray agent: no main window. Closing the last window must not
            // terminate the app — ShutdownMode is set to OnExplicitShutdown.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = null;
            AppServices.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
