using Ripcord.Core.Consoles;

namespace Ripcord.Presentation;

/// <summary>
/// The window-level operations a surface cannot perform on its own: showing and tearing down the stream layer,
/// and the fullscreen presenter.
///
/// <para>
/// A seam so that no page casts to a concrete window type: a page can be exercised without a window, a host
/// with a different shell does not get a silent null and a click that does nothing, and the shell is not
/// pinned to one WinUI type. A front end implements this on whatever its shell happens to be.
/// </para>
///
/// <para>
/// Deliberately small. The stream is a layer above the navigation chrome rather than a page inside it, so this
/// covers that layer and nothing else — page-to-page navigation stays with the front end's own frame.
/// </para>
/// </summary>
/// <summary>Where in settings a caller wants somebody to land.</summary>
public enum SettingsDestination
{
    /// <summary>The top of the page, as the gear button opens it.</summary>
    Top,

    /// <summary>
    /// The account section.
    ///
    /// <para>
    /// Worth naming, because "open settings" is not the same offer as "sign in". The pairing step's sign-in
    /// button opened settings at the top, where the account section is the last thing on a long page — so a
    /// button that said "Sign in to PlayStation Network" delivered somebody to a resolution dropdown and
    /// left them to go hunting. An offer that does not land where it promised is worse than no offer.
    /// </para>
    /// </summary>
    Account,
}

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

    /// <summary>
    /// Show the settings surface, at <paramref name="destination"/>.
    ///
    /// <para>
    /// Here because the shell owns navigation and a page must not know how another page is reached. The
    /// destination is part of the contract rather than the caller's problem: landing somebody on the right
    /// page is only half of taking them somewhere, and the half that gets forgotten.
    /// </para>
    /// </summary>
    void ShowSettings(SettingsDestination destination = SettingsDestination.Top);
}
