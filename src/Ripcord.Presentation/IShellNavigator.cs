using Ripcord.Core.Consoles;

namespace Ripcord.Presentation;

/// <summary>
/// The window-level operations a surface cannot perform on its own: showing and tearing down the stream layer,
/// and the fullscreen presenter.
///
/// <para>
/// This replaces three <c>App.MainWindow as MainWindow</c> casts. Each was a page reaching up through a static
/// for a concrete window type, which had three costs: a page could not be exercised without a window; the cast
/// silently produced null in any host that was not exactly that class, so the failure mode was a click that did
/// nothing; and it pinned "the shell" to one WinUI type, which is precisely the coupling Stage A exists to
/// remove. A front end implements this on whatever its shell happens to be.
/// </para>
///
/// <para>
/// Deliberately small. The stream is a layer above the navigation chrome rather than a page inside it, so this
/// covers that layer and nothing else — page-to-page navigation stays with the front end's own frame, which is
/// the thing front ends differ on most and agree on least.
/// </para>
/// </summary>
public interface IShellNavigator
{
    /// <summary>True while the stream layer is showing.</summary>
    bool IsStreaming { get; }

    /// <summary>True while the shell is in its fullscreen presentation.</summary>
    bool IsFullScreen { get; }

    /// <summary>Show the stream layer for <paramref name="console"/>.</summary>
    void ShowStream(PairedConsole console);

    /// <summary>
    /// Tear the stream layer down and restore normal navigation. <paramref name="closedConsoleHost"/> and
    /// <paramref name="restRequested"/> carry the just-ended session's console and whether it was asked to rest,
    /// so the console list can re-probe on return and show a rest-requested console settling.
    /// </summary>
    void CloseStream(string? closedConsoleHost = null, bool restRequested = false);

    /// <summary>Enter or leave fullscreen. Owned by the shell, because the presenter belongs to it.</summary>
    void SetFullScreen(bool fullScreen);
}
