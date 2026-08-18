namespace Ripcord.Core.Accounts;

/// <summary>
/// What survives between runs after the user has signed in to their account.
///
/// <para>
/// <b>The refresh token is the whole credential.</b> Access tokens last about an hour and are never persisted —
/// there is no point, and every extra copy of a bearer token on disk is a liability. What the user experiences as
/// "the app remembered me" is exactly "the app still holds a valid refresh token", so this record is the single
/// thing standing between a signed-in install and a sign-in prompt.
/// </para>
///
/// <para>
/// <see cref="AccountId"/> and <see cref="DisplayName"/> are a cache, not authority. They exist so a surface can
/// say who is signed in — and so pairing can pre-fill the account id — without a network round trip on every
/// launch, and so an offline start still shows something truthful. Anything that matters is re-read from the
/// account service once a token has been refreshed.
/// </para>
/// </summary>
/// <param name="RefreshToken">The long-lived credential. Encrypted at rest by the store, never logged.</param>
/// <param name="AccountId">
/// The signed-in account's identifier, cached from the last successful account lookup. Null until one has
/// succeeded — a token can be perfectly valid before we have ever asked who it belongs to.
/// </param>
/// <param name="DisplayName">The account's user-visible name, cached for the same reason.</param>
/// <param name="SavedAt">When this record was written, for diagnostics and for staleness decisions.</param>
public sealed record StoredAccountSession(
    string RefreshToken,
    string? AccountId = null,
    string? DisplayName = null,
    DateTimeOffset SavedAt = default);

/// <summary>
/// Local persistence for the signed-in account.
///
/// <para>
/// Platform-neutral by construction, and deliberately so: <c>Ripcord.Core</c> knows nothing about PlayStation,
/// and nothing here does either. "An account, a refresh token, a cached identity" is the shape of every OAuth
/// sign-in, so the backend that fills it in stays above this layer.
/// </para>
/// </summary>
public interface IAccountTokenStore
{
    /// <summary>The stored session, or null when nobody is signed in.</summary>
    StoredAccountSession? Load();

    /// <summary>Write the session, replacing any existing one.</summary>
    void Save(StoredAccountSession session);

    /// <summary>
    /// Forget the stored session. This is sign-out, and it must leave nothing behind: a "signed out" state that
    /// still has a usable refresh token on disk is the kind of thing that gets reported as a security bug.
    /// </summary>
    void Clear();

    /// <summary>How the refresh token is protected at rest, so a settings surface can report it honestly.</summary>
    string ProtectionDescription { get; }

    /// <summary>True when the stored token is actually encrypted on this platform.</summary>
    bool TokenEncrypted { get; }
}
