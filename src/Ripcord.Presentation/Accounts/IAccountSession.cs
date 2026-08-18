namespace Ripcord.Presentation.Accounts;

/// <summary>Who is signed in.</summary>
/// <param name="AccountId">
/// The account's identifier, in the form the pairing exchange wants. This is the value that used to be typed by
/// hand on the link step.
/// </param>
/// <param name="DisplayName">The user-visible account name, for a surface to show.</param>
/// <param name="Region">The account's region, or empty when it is not known (a cached, offline identity).</param>
public sealed record AccountIdentity(string AccountId, string DisplayName, string Region);

/// <summary>
/// A console the account owns, as the cloud reports it — distinct from a console found by scanning the local
/// network, and the reason both exist: this list is available from anywhere, includes consoles that are asleep,
/// and uniquely says whether one can be woken remotely. It carries no address, because the cloud does not give
/// one; reaching the console is a separate negotiation.
/// </summary>
/// <param name="Id">The console's stable device id, used to target it for wake and signaling.</param>
/// <param name="Name">Its display name — the same string a local scan reports.</param>
/// <param name="RemotePlayEnabled">Whether the console is set up for remote play at all.</param>
/// <param name="CanWakeRemotely">Whether its standby settings permit a remote wake.</param>
public sealed record CloudConsole(string Id, string Name, bool RemotePlayEnabled, bool CanWakeRemotely);

/// <summary>
/// The account tier: signing in, staying signed in, and what that makes available.
///
/// <para>
/// <b>The browser stays in the front end.</b> This seam deliberately does not open a web view, because a web view
/// is a device in the sense of the layer's third rule — it is a platform control with a native handle, and the
/// portable layer must not name one. So sign-in is expressed as a conversation instead: this layer says which URL
/// to open (<see cref="BeginSignIn"/>), the front end drives whatever browser it has, and hands back the URL it
/// landed on (<see cref="CompleteSignInAsync"/>). A console harness satisfies the same seam by printing the URL
/// and reading a pasted redirect, with no browser at all.
/// </para>
///
/// <para>
/// <b>Every member may fail, and callers guard each independently.</b> This is a network service behind an OAuth
/// credential that a given build may not even have — <see cref="CanSignIn"/> is false in that case and every
/// other member declines. Being signed out is an ordinary state, not an error state: LAN play against a paired
/// console needs none of this.
/// </para>
/// </summary>
public interface IAccountSession
{
    /// <summary>
    /// Whether this build can sign in at all. False when no OAuth client credential was configured, which is the
    /// shipping default — surfaces check this before offering a sign-in affordance, so the user is told up front
    /// rather than after typing a password.
    /// </summary>
    bool CanSignIn { get; }

    /// <summary>The signed-in account, or null.</summary>
    AccountIdentity? Current { get; }

    /// <summary>Whether a stored credential exists to restore from, answered without touching the network.</summary>
    bool HasStoredSession { get; }

    /// <summary>
    /// Re-establish a session from the stored credential. Null means "not signed in" — including the case where a
    /// stored credential turned out to be stale, which is not exceptional and clears itself.
    /// </summary>
    Task<AccountIdentity?> RestoreAsync(CancellationToken cancellationToken);

    /// <summary>The URL the front end should open to begin sign-in.</summary>
    /// <exception cref="InvalidOperationException">When <see cref="CanSignIn"/> is false.</exception>
    string BeginSignIn();

    /// <summary>
    /// Whether a URL the front end has navigated to is the one carrying the result. Asked on each navigation, so
    /// the front end knows when to stop watching and close its browser.
    /// </summary>
    bool IsCompletionRedirect(Uri navigated);

    /// <summary>Finish sign-in from the redirect the front end landed on.</summary>
    Task<AccountIdentity> CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken);

    /// <summary>The account's consoles, from the cloud. Empty when signed out.</summary>
    Task<IReadOnlyList<CloudConsole>> ListConsolesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ask the account service to wake the console with this id — the only wake that works when you are not on
    /// the console's network, since the local one is a broadcast that cannot leave the subnet.
    ///
    /// <para>
    /// Completing means the request was accepted, not that the console is awake. Whether it came up is settled
    /// by probing it, the same way the local path settles it.
    /// </para>
    /// </summary>
    Task WakeAsync(string cloudDeviceId, CancellationToken cancellationToken);

    /// <summary>Destroy the stored credential and forget the session.</summary>
    void SignOut();
}
