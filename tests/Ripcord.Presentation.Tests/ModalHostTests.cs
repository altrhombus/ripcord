using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on modality: nothing calls <c>ContentDialog.ShowAsync</c> except <c>ModalHost</c>.
///
/// <para>
/// A dialog shown without a Modal input scope leaves whatever is underneath still holding the pad, so on a
/// handheld with no keyboard the user is looking at a prompt they cannot answer. A convention does not hold
/// that across every call site that shows a dialog, so this is a test, as with
/// <c>PageStyleTests</c> and <c>PresentationPortabilityTests</c>.
/// </para>
///
/// <para>
/// Text matching rather than reflection, because the app assembly is Windows-only and this suite is not — the
/// same reason <c>PageStyleTests</c> reads XAML off disk instead of loading it.
/// </para>
/// </summary>
public class ModalHostTests
{
    [Fact]
    public void OnlyModalHostShowsADialog()
    {
        string appRoot = Path.Combine(RepositoryRoot(), "src", "Ripcord.App");
        Assert.True(Directory.Exists(appRoot), $"Could not locate the app project at {appRoot}.");

        // Any ShowAsync() with no arguments. ModalHost's own wrapper takes one, so calls THROUGH it read as
        // ShowAsync(dialog) and do not match; the raw ContentDialog call reads as dialog.ShowAsync().
        var rawShow = new Regex(@"\.ShowAsync\(\s*\)", RegexOptions.Singleline);

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGenerated(file) || Path.GetFileName(file) == "ModalHost.cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);

            foreach (Match match in rawShow.Matches(text))
            {
                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A dialog was shown without going through ModalHost, so nothing pushed a Modal input scope for it. "
            + "Whatever is underneath keeps the pad, and a controller-only user cannot answer the prompt they "
            + "just raised. Use ModalHost.ShowAsync(dialog). Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The shell never puts focus into the page while a modal is up.
    ///
    /// <para>
    /// Six paths seed focus — window activation, navigation, chrome scope activation, region cycling, a pad
    /// direction with nothing focused, and the focus watchdog — and all six end at
    /// <c>FocusFirstContentElement</c>, which focuses the content BEHIND the modal. The symptom is a PSN
    /// password box that deselects the instant it is clicked, because a WebView2's native focus reads as no
    /// XAML focus at all. Guarding callers one at a time is how one gets missed; the check belongs at the
    /// chokepoint, and this asserts it is there.
    /// </para>
    /// </summary>
    [Fact]
    public void ShellNeverSeedsFocusBehindAModal()
    {
        string window = Path.Combine(RepositoryRoot(), "src", "Ripcord.App", "MainWindow.xaml.cs");
        Assert.True(File.Exists(window), $"Could not locate {window}.");

        string text = File.ReadAllText(window);

        // [^;] rather than a newline class: it spans the leading comment block but stops at the first
        // statement, so the guard has to BE that statement. Slide it below the ChromeFrame lookup and
        // this fails, which is the case that matters — an earlier return would skip it. A closed-window check
        // may share the condition, ahead of it: once the window has closed there is no modal to ask about.
        var guarded = new Regex(
            @"private void FocusFirstContentElement\(\)[^{]*\{[^;]*if \((_closed \|\| )?ModalOwnsFocus\)",
            RegexOptions.Singleline);

        Assert.True(
            guarded.IsMatch(text),
            "FocusFirstContentElement must refuse while ModalOwnsFocus. It focuses the page inside "
            + "ChromeFrame, which while a modal is showing is the content behind it — and every seeding path "
            + "in the shell ends here, so this is the only place the rule holds for all of them.");

        // Nothing asks the pilot directly except the one shell predicate that adds the guard. The watchdog
        // did, which is precisely how it slipped past.
        var direct = new Regex(@"_focus\.NeedsFocusSeed\(\)");
        int asks = direct.Matches(text).Count;

        Assert.True(
            asks == 1,
            $"NeedsFocusSeed is asked {asks} times in MainWindow; it must be asked once, inside "
            + "ShellShouldSeedFocus, which is where the ModalOwnsFocus guard lives. A second caller is a "
            + "seeding path with no guard.");

        Assert.Contains("private bool ShellShouldSeedFocus() => !ModalOwnsFocus", text, StringComparison.Ordinal);
    }

    private static bool IsGenerated(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ripcord.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find Ripcord.slnx above {AppContext.BaseDirectory}.");
    }
}
