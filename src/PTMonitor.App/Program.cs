using Avalonia;
using Avalonia.Threading;
using PTMonitor.Core.Diagnostics;
using PTMonitor.Core.Store;
using System;
using System.Runtime.InteropServices;

namespace PTMonitor.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // The log is always on: a sink enabled after the fact cannot diagnose a
        // crash that already happened. It is event-only and size-capped, so the
        // cost stays bounded (see Core/Diagnostics/Log.cs).
        Log.Initialize(AppPaths.LogFile);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("appdomain", e.ExceptionObject as Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => LogCrash("ui", e.Exception);
        Log.Info("startup",
            $"PTMonitor {Version()}, {RuntimeInformation.OSDescription}, data {AppPaths.DataDirectory}");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        Log.Shutdown();
    }

    /// <summary>
    /// Records a crash and lets it terminate the process: <c>Handled</c> is
    /// deliberately never set, so the only change is that the failure is now on
    /// disk instead of only in the Windows event log. <c>ToString()</c> rather
    /// than <c>Message</c>: the cause of a crash is almost always the inner
    /// exception, and the whole chain is flattened onto one line by the sink.
    /// </summary>
    private static void LogCrash(string area, Exception? exception) =>
        Log.Error(area, exception?.ToString() ?? "unhandled exception");

    private static string Version() => typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
