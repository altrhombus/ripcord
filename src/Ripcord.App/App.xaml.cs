using System;
using System.IO;
using Microsoft.UI.Xaml;
using Ripcord.Core.Platform;

namespace Ripcord_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// The main window, for pages that need window-level behaviour a Page cannot reach on its own — chiefly
    /// SessionPage switching the presenter to fullscreen for an immersive stream.
    /// </summary>
    public static Window? MainWindow { get; private set; }

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
        _window = new MainWindow();
        MainWindow = _window;
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
            string path = Path.Combine(new DefaultPlatformPaths().StateDirectory, "crash.log");
            File.AppendAllText(path, report + Environment.NewLine + new string('-', 80) + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics must never themselves crash the app; the Debug output above is the fallback.
        }
    }
}
