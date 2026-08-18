using Ripcord.Presentation.Consoles;

namespace Ripcord.Presentation.Accounts;

/// <summary>Where the account surface currently is.</summary>
public enum AccountStep
{
    /// <summary>No credential in this build — sign-in cannot be offered at all.</summary>
    Unavailable,

    /// <summary>Signed out, with sign-in available.</summary>
    SignedOut,

    /// <summary>Restoring a stored session, or exchanging a fresh authorization code.</summary>
    Working,

    /// <summary>Signed in.</summary>
    SignedIn,
}

/// <summary>
/// Everything the account surface currently shows, as one immutable value.
/// </summary>
public sealed record AccountViewState(
    AccountStep Step,
    string Heading,
    string Detail,
    StatusTone Tone,
    bool CanSignIn,
    bool CanSignOut,
    bool IsBusy,
    string? Error,
    string AccountName,
    string AccountId,
    IReadOnlyList<CloudConsole> Consoles,
    bool ConsolesLoaded)
{
    /// <summary>
    /// True when the account id is known, which is what lets pairing stop asking for it. Deliberately not
    /// "is signed in": a cached identity restored while the network was down is signed in for this purpose,
    /// because the id it carries is exactly as good as a freshly fetched one.
    /// </summary>
    public bool HasAccountId => AccountId.Length > 0;
}
