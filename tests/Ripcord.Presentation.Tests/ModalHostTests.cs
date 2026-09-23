using System.Text.RegularExpressions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on modality: nothing calls <c>ContentDialog.ShowAsync</c> except <c>ModalHost</c>.
///
/// <para>
/// <b>Why this is a test and not a convention.</b> Seven call sites showed a dialog and exactly one pushed a
/// Modal input scope — the one where somebody had hit the bug, mid-stream, and found that the session still
/// owned the pad so the controller could not reach the prompt it had just raised. The other six were not
/// wrong on purpose; they were written by someone who had no reason to know the rule existed. That is exactly
/// the shape of thing a comment does not fix, and this repo already governs itself this way
/// (<c>PageStyleTests</c>, <c>PresentationPortabilityTests</c>).
/// </para>
///
/// <para>
/// The cost of getting it wrong is not cosmetic: a dialog shown without a scope leaves whatever is underneath
/// still holding the pad, so on a handheld with no keyboard the user is looking at a prompt they cannot answer.
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
    /// <b>Why this needs a guard.</b> Six paths seed focus — window activation, navigation, chrome scope
    /// activation, region cycling, a pad direction with nothing focused, and the focus watchdog — and all six
    /// end at <c>FocusFirstContentElement</c>, which focuses the content BEHIND the modal. The first attempt
    /// at this bug guarded one caller and left the watchdog unguarded, so the symptom (a PSN password box that
    /// deselects the instant it is clicked, because a WebView2's native focus reads as no XAML focus at all)
    /// survived the fix untouched. Guarding callers one at a time is how one gets missed; the check belongs at
    /// the chokepoint, and this asserts it is still there.
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
        // this fails, which is the case that matters — an earlier return would skip it.
        var guarded = new Regex(
            @"private void FocusFirstContentElement\(\)[^{]*\{[^;]*if \(ModalOwnsFocus\)",
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
