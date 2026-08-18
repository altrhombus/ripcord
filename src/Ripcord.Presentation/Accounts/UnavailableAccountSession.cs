namespace Ripcord.Presentation.Accounts;

/// <summary>
/// The account seam for a build that cannot sign in — no OAuth client credential, which is the shipping default,
/// or a host that cannot supply a stable device id.
///
/// <para>
/// A null object rather than a null reference, so "this build cannot sign in" is a state every surface renders
/// from <see cref="CanSignIn"/> rather than a null check each of them has to remember — and so
/// <see cref="RipcordAppServices.Account"/> can be non-nullable.
/// </para>
///
/// <para>
/// Portable rather than living with the PlayStation backend, because nothing about it is PlayStation-specific:
/// it is the answer for any front end, any backend, and every test that does not care about accounts.
/// </para>
///
/// <para>
/// The two members that only make sense once signing in has begun throw rather than returning something empty.
/// Reaching them means a caller ignored <see cref="CanSignIn"/>, which is a wiring bug and deserves a stack
/// trace; the rest degrade quietly, because being signed out is ordinary.
/// </para>
/// </summary>
public sealed class UnavailableAccountSession : IAccountSession
{
    public bool CanSignIn => false;

    public AccountIdentity? Current => null;

    public bool HasStoredSession => false;

    public Task<AccountIdentity?> RestoreAsync(CancellationToken cancellationToken)
        => Task.FromResult<AccountIdentity?>(null);

    public string BeginSignIn() => throw new InvalidOperationException(
        "This build cannot sign in (no OAuth client credential is configured). Check CanSignIn first.");

    public bool IsCompletionRedirect(Uri navigated) => false;

    public Task<AccountIdentity> CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "This build cannot sign in, so there is no sign-in to complete.");

    public Task<IReadOnlyList<CloudConsole>> ListConsolesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CloudConsole>>([]);

    public Task WakeAsync(string cloudDeviceId, CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "This build cannot sign in, so it cannot wake a console through the account service.");

    public void SignOut()
    {
        // Signing out of nothing is not an error, and making it one would force every caller to branch.
    }
}
