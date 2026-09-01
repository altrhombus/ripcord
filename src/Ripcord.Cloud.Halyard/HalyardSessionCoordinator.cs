using System.Security.Cryptography;

namespace Ripcord.Cloud.Halyard;

/// <summary>A live cloud session context returned by <see cref="IHalyardSessionCoordinator.BeginAsync"/>.
///
/// <para><see cref="Data1"/>/<see cref="Data2"/> are the ephemeral field-cipher key/material (base64) this
/// session sent to the console in the connect command; the console encrypts the account registration seed
/// with them and returns it as <c>customData1</c> on the push channel, so the pairing layer needs them to
/// recover the seed (see the protocol layer's <c>HalyardAccountSeedDelivery</c>).</para></summary>
public sealed record HalyardConnectHandle(
    string SessionId, string ConsoleDuid, string AccountId, string Data1 = "", string Data2 = "");

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

        // The push context ties the session to the push (WebSocket) channel. That channel exists
        // (HalyardPushChannel, driven by HalyardWanRendezvous, which also carries the console's OFFER and the
        // customData1 seed); this simplified BeginAsync is the wake/offer path and does not itself run it — on
        // LAN we proceed via direct discovery, and the WAN connect uses the rendezvous. A fresh id keeps the
        // session-manager call well-formed.
        string pushContextId = Guid.NewGuid().ToString();
        HalyardCloudSession session = await _cloud.CreateSessionAsync(pushContextId, cancellationToken).ConfigureAwait(false);

        // The command carries `data1`/`data2` to the console: two ephemeral 16-byte values that are the
        // account-registration seed-delivery key/material. Both are fresh random per connect; the console
        // field-encrypts the registration seed with them and returns it as `customData1` (recovered by the
        // pairing layer). They are opaque base64 here — this REST layer holds no crypto — but are retained on
        // the handle so the pairing layer can decrypt the delivered seed. `data3` is unused (the command
        // carries only two). See docs/protocol/ps5-cloud-session-api.md.
        string data1 = RandomSeed(), data2 = RandomSeed();
        await _cloud.SendConnectCommandAsync(
            console.Duid, account.AccountId, session.SessionId, "Windows", (data1, data2, ""), cancellationToken).ConfigureAwait(false);

        await _cloud.SendOfferAsync(
            session.SessionId, account.AccountId, console.Duid, localCandidates, cancellationToken).ConfigureAwait(false);

        return new HalyardConnectHandle(session.SessionId, console.Duid, account.AccountId, data1, data2);
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
