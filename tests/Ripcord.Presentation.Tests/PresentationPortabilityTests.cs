using System.Reflection;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Structural guard on the whole point of <c>Ripcord.Presentation</c>: it must stay linkable by a front end
/// that is not WinUI.
///
/// <para>
/// This is deliberately a test rather than a comment. The repo has precedent both ways — the interop-constants
/// bundle is protected by <c>BundledInteropConstantsTests</c> and holds, while the "styles live in Styles/, not
/// in a page's resources" rule was written only as prose and was broken by four separate pages. A layering rule
/// that nothing checks is a rule that decays, and the specific decay here is silent: the first stray
/// <c>using Microsoft.UI.Xaml;</c> in the wrong file would undo the entire extraction without any visible
/// symptom until someone tried to build the macOS front end.
/// </para>
/// </summary>
public class PresentationPortabilityTests
{
    /// <summary>
    /// Assembly-name prefixes that mean a UI framework has been let in. Matched case-insensitively against
    /// referenced assembly names.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.UI",        // WinUI 3
        "Microsoft.Windows",   // Windows App SDK
        "WinRT",               // WinRT projections
        "Windows.",            // Windows metadata projections
        "System.Windows",      // WPF
        "PresentationCore",    // WPF
        "PresentationFramework",
        "Avalonia",
        "Gtk",
        "GtkSharp",
        "Xamarin",
        "Microsoft.Maui",
    ];

    [Fact]
    public void Presentation_ReferencesNoUiFrameworkAssembly()
    {
        Assembly presentation = typeof(IUiDispatcher).Assembly;

        var offenders = presentation.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{presentation.GetName().Name} must stay free of UI-framework references so a non-WinUI front end "
            + $"can link it, but it references: {string.Join(", ", offenders)}. Presentation concerns belong in "
            + "portable enums and bools that each front end maps to its own types.");
    }

    [Fact]
    public void Presentation_TargetsAPortableFramework()
    {
        // A Windows-specific TFM would defeat the split just as surely as a WinUI reference, and would do it
        // without adding any assembly reference for the check above to catch.
        string? framework = typeof(IUiDispatcher).Assembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            ?.FrameworkName;

        Assert.NotNull(framework);
        Assert.DoesNotContain("windows", framework, StringComparison.OrdinalIgnoreCase);
    }
}
