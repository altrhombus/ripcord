using System;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Ripcord.Core.Platform;
using Ripcord.Presentation;
using Ripcord.Presentation.Halyard;
using Ripcord_App.Threading;

namespace Ripcord_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    private static RipcordAppServices? _services;

    /// <summary>
    /// The main window. Kept for the two things that genuinely need the WinUI <see cref="Window"/> itself —
    /// its content root, for window-wide key routing, and its <c>Activated</c> event. Everything a page used to
    /// reach through here by casting to <c>MainWindow</c> now goes through <see cref="RipcordAppServices.Shell"/>
    /// instead.
    /// </summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// The application graph. Built in <see cref="OnLaunched"/> before anything can navigate, so a surface
    /// reaching it always finds it complete.
    ///
    /// <para>
    /// Throws rather than returning null, and that is the design: a page constructor asking for services before
    /// the graph exists is a startup-ordering bug, and it should fail there with a stack trace naming the page
    /// rather than propagate a null into a field that is dereferenced three interactions later.
    /// </para>
    /// </summary>
    public static RipcordAppServices Services => _services ?? throw new InvalidOperationException(
        "RipcordAppServices was requested before App.OnLaunched built it.");

    /// <summary>Full text of the most recent unhandled exception, for the diagnostics UI to surface.</summary>
    public static string? LastCrashReport { get; private set; }

    public App()
    {
        InitializeComponent();

        // The app previously had no crash reporting of any kind: an unhandled exception — a XAML parse error on
        // a page, a bad resource lookup, a faulted event handler — terminated the process with nothing written
        // anywhere, so "it crashes when I click X" was the entire available diagnostic. Capture it instead.
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // The graph first, the window second. MainWindow's own constructor reads the settings store, and every
        // page it can navigate to resolves from here before its InitializeComponent runs, so there is no ordering
        // in which a partially-built graph is observable.
        _services = HalyardAppServices.Create(
            new DispatcherQueueUiDispatcher(DispatcherQueue.GetForCurrentThread()));

        var window = new MainWindow();
        _services.AttachShell(window);

        _window = window;
        MainWindow = window;
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        string report =
            $"""
            Ripcord unhandled exception
            When:    {DateTimeOffset.Now:O}
            Message: {e.Message}

            {e.Exception}
            """;

        LastCrashReport = report;
        System.Diagnostics.Debug.WriteLine(report);
        TryWriteCrashLog(report);

        // Keep the process alive so the report can actually be read and the user can navigate away. Without
        // this the window vanishes and the exception goes with it.
        e.Handled = true;
    }

    private static void TryWriteCrashLog(string report)
    {
        try
        {
            // Deliberately NOT App.Services.Paths. Every other surface goes through the graph, but this one runs
            // when something has already gone wrong — including, possibly, the graph's own construction — and a
            // crash reporter that needs the composition root to be intact cannot report the crash you most want
            // to read. Constructing paths directly costs nothing and depends on nothing.
            string path = Path.Combine(new DefaultPlatformPaths().StateDirectory, "crash.log");
            File.AppendAllText(path, report + Environment.NewLine + new string('-', 80) + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics must never themselves crash the app; the Debug output above is the fallback.
        }
    }
}
