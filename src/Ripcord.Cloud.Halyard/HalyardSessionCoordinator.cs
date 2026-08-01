using System.Security.Cryptography;

namespace Ripcord.Cloud.Halyard;

/// <summary>A live cloud session context returned by <see cref="IHalyardSessionCoordinator.BeginAsync"/>.</summary>
public sealed record HalyardConnectHandle(string SessionId, string ConsoleDuid, string AccountId);

/// <summary>
/// Orchestrates the cloud side of connecting: create the session, send the wake/trigger command to
/// the selected console, and offer our candidates over the signaling channel. Ordering from
/// docs/protocol/ps5-cloud-session-api.md. The direct-console session (transport + handshake) then
/// takes over separately.
/// </summary>
public interface IHalyardSessionCoordinator
{
    Task<HalyardConnectHandle> BeginAsync(
        HalyardConsoleClient console,
        IReadOnlyList<HalyardCandidate> localCandidates,
        CancellationToken cancellationToken);

    Task EndAsync(HalyardConnectHandle handle, CancellationToken cancellationToken);
}

public sealed class HalyardSessionCoordinator(HalyardCloudClient cloud) : IHalyardSessionCoordinator
{
    private readonly HalyardCloudClient _cloud = cloud;

    public async Task<HalyardConnectHandle> BeginAsync(
        HalyardConsoleClient console,
        IReadOnlyList<HalyardCandidate> localCandidates,
        CancellationToken cancellationToken)
    {
        HalyardAccountInfo account = await _cloud.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);

        // The push context ties the session to the push (WebSocket) channel. That channel isn't
        // implemented yet (a documented gap); on LAN we still proceed via direct discovery. A fresh
        // id keeps the session-manager call well-formed.
        string pushContextId = Guid.NewGuid().ToString();
        HalyardCloudSession session = await _cloud.CreateSessionAsync(pushContextId, cancellationToken).ConfigureAwait(false);

        // The client generates the per-session seed material handed to the console. Generation is
        // trivial; how these seeds derive the session keys is Stage 5 crypto (they're carried in the
        // clear here, which is what makes that derivation approachable).
        (string, string, string) seeds = (RandomSeed(), RandomSeed(), RandomSeed());
        await _cloud.SendConnectCommandAsync(
            console.Duid, account.AccountId, session.SessionId, "Windows", seeds, cancellationToken).ConfigureAwait(false);

        await _cloud.SendOfferAsync(
            session.SessionId, account.AccountId, console.Duid, localCandidates, cancellationToken).ConfigureAwait(false);

        return new HalyardConnectHandle(session.SessionId, console.Duid, account.AccountId);
    }

    public Task EndAsync(HalyardConnectHandle handle, CancellationToken cancellationToken)
        => _cloud.LeaveSessionAsync(handle.SessionId, cancellationToken);

    private static string RandomSeed() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
}
