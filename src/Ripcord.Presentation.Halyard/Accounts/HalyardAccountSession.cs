using Ripcord.Cloud.Halyard;
using Ripcord.Presentation.Accounts;

namespace Ripcord.Presentation.Halyard.Accounts;

/// <summary>
/// The PlayStation backend for <see cref="IAccountSession"/>: a thin adapter over
/// <see cref="HalyardAccountGateway"/>.
///
/// <para>
/// Thin on purpose. The lifecycle logic — what a failed refresh means, which refresh token to persist, when to
/// fall back to a cached identity — lives in the gateway, where the console harness can reach it without
/// referencing a presentation assembly. What is left here is the translation between Halyard's vocabulary and
/// the portable one, which is exactly the job this project's other backend seams do.
/// </para>
/// </summary>
public sealed class HalyardAccountSession(HalyardAccountGateway gateway) : IAccountSession
{
    private readonly HalyardAccountGateway _gateway = gateway
        ?? throw new ArgumentNullException(nameof(gateway));

    public bool CanSignIn => _gateway.CanSignIn;

    public AccountIdentity? Current => Translate(_gateway.Account);

    public bool HasStoredSession => _gateway.HasStoredSession;

    public async Task<AccountIdentity?> RestoreAsync(CancellationToken cancellationToken)
        => Translate(await _gateway.RestoreAsync(cancellationToken).ConfigureAwait(false));

    public string BeginSignIn() => _gateway.BeginSignIn();

    public bool IsCompletionRedirect(Uri navigated) => _gateway.IsCompletionRedirect(navigated);

    public async Task<AccountIdentity> CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken)
    {
        HalyardAccount account = await _gateway
            .CompleteSignInAsync(redirected, cancellationToken)
            .ConfigureAwait(false);
        return Translate(account)!;
    }

    public async Task<IReadOnlyList<CloudConsole>> ListConsolesAsync(CancellationToken cancellationToken)
    {
        // Restores the session if it is only on disk. Reading IsSignedIn here returned an empty list on a
        // fresh launch, and an empty list is indistinguishable from "the account has no consoles" — which is
        // what made a console away from home read as Offline rather than Away. Away is decided by asking
        // whether the account lists it, and the answer was always no because nobody had signed the gateway in.
        if (!await _gateway.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        IReadOnlyList<HalyardConsoleClient> clients = await _gateway.Cloud
            .ListConsolesAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. clients.Select(c => new CloudConsole(
            Id: c.Duid,
            Name: c.Device.Name,
            RemotePlayEnabled: c.RemotePlayEnabled,
            CanWakeRemotely: c.CanWake))];
    }

    public async Task WakeAsync(string cloudDeviceId, CancellationToken cancellationToken)
    {
        // Same restore, same reason: a cloud wake on a fresh launch threw "not signed in" at a signed-in user.
        if (!await _gateway.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Not signed in, so the account service cannot be asked to wake a console.");
        }

        await new HalyardSessionCoordinator(_gateway.Cloud)
            .WakeAsync(cloudDeviceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public void SignOut() => _gateway.SignOut();

    /// <summary>
    /// Halyard's account record in portable terms. The online id becomes the display name because it is what the
    /// user recognises as "their account"; the numeric id is real but means nothing to them.
    /// </summary>
    private static AccountIdentity? Translate(HalyardAccount? account)
        => account is null ? null : new AccountIdentity(account.AccountId, account.OnlineId, account.Region);
}
