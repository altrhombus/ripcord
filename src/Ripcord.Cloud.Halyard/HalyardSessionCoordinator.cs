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

        // Three 16-byte values the command carries to the console.
        //
        // [X] Their role is open. An earlier reading had them seeding the direct session's key agreement; that
        // was a guess and the later work did not bear it out — v1 derives its control key from the registration
        // key and the console's own nonce alone, and a session establishes against a console with no internet
        // access at all. They are high-entropy and real, but unattached to anything we have established, so
        // random values of the right shape are as good as anything until that changes. Do not restate the
        // superseded reading here; see docs/protocol/ps5-cloud-session-api.md.
        (string, string, string) seeds = (RandomSeed(), RandomSeed(), RandomSeed());
        await _cloud.SendConnectCommandAsync(
            console.Duid, account.AccountId, session.SessionId, "Windows", seeds, cancellationToken).ConfigureAwait(false);

        await _cloud.SendOfferAsync(
            session.SessionId, account.AccountId, console.Duid, localCandidates, cancellationToken).ConfigureAwait(false);

        return new HalyardConnectHandle(session.SessionId, console.Duid, account.AccountId);
    }

    public Task EndAsync(HalyardConnectHandle handle, CancellationToken cancellationToken)
        => _cloud.LeaveSessionAsync(handle.SessionId, cancellationToken);

    /// <summary>
    /// Wake a console without going on to negotiate a connection: create the session and send the command,
    /// then stop.
    ///
    /// <para>
    /// This is the half of <see cref="BeginAsync"/> that works today. The command reaches the console through
    /// PSN's own server-side fan-out rather than over any path we hold open, so waking a console does not depend
    /// on the push channel — which matters, because the push channel is the one cloud piece still undecoded. It
    /// is also the only way to wake a console you are not on the same network as; the LAN wake needs a broadcast
    /// that will not leave the subnet.
    /// </para>
    ///
    /// <para>
    /// Returns once PSN has accepted the command, which is not the same as the console being awake. Whether it
    /// came up is answered by probing it, exactly as the LAN path does.
    /// </para>
    /// </summary>
    public async Task WakeAsync(string consoleDuid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consoleDuid);

        HalyardAccountInfo account = await _cloud.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        HalyardCloudSession session = await _cloud
            .CreateSessionAsync(Guid.NewGuid().ToString(), cancellationToken)
            .ConfigureAwait(false);

        (string, string, string) seeds = (RandomSeed(), RandomSeed(), RandomSeed());
        await _cloud.SendConnectCommandAsync(
            consoleDuid, account.AccountId, session.SessionId, "Windows", seeds, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RandomSeed() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
}
