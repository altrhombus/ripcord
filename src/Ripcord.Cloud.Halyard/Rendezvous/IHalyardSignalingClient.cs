namespace Ripcord.Cloud.Halyard.Rendezvous;

/// <summary>
/// The cloud calls the WAN rendezvous makes, as a seam — so the orchestration's sequencing and timing can be
/// tested without a live PSN backend. <see cref="HalyardCloudSignalingClient"/> is the real adapter over
/// <see cref="HalyardCloudClient"/>.
/// </summary>
public interface IHalyardSignalingClient
{
    /// <summary>Create the account-scoped session; returns its id.</summary>
    Task<string> CreateSessionAsync(string pushContextId, CancellationToken cancellationToken);

    /// <summary>Send the wake/trigger command to the console, which brings it into the session.</summary>
    Task SendConnectCommandAsync(
        string consoleDuid,
        string accountId,
        string sessionId,
        string clientType,
        (string Data1, string Data2, string Data3) seeds,
        CancellationToken cancellationToken);

    /// <summary>POST our OFFER (our candidates) to the console over the signaling channel.</summary>
    Task SendOfferAsync(
        string sessionId,
        string accountId,
        string consoleDuid,
        IReadOnlyList<HalyardCandidate> candidates,
        CancellationToken cancellationToken);

    /// <summary>Read back one session by id — to confirm ours exists and see who has joined.</summary>
    Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Leave the session — the disconnect.</summary>
    Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>The real signaling client, adapting <see cref="HalyardCloudClient"/> to the rendezvous seam.</summary>
public sealed class HalyardCloudSignalingClient(HalyardCloudClient cloud) : IHalyardSignalingClient
{
    private readonly HalyardCloudClient _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));

    public async Task<string> CreateSessionAsync(string pushContextId, CancellationToken cancellationToken)
        => (await _cloud.CreateSessionAsync(pushContextId, cancellationToken).ConfigureAwait(false)).SessionId;

    public Task SendConnectCommandAsync(
        string consoleDuid, string accountId, string sessionId, string clientType,
        (string Data1, string Data2, string Data3) seeds, CancellationToken cancellationToken)
        => _cloud.SendConnectCommandAsync(consoleDuid, accountId, sessionId, clientType, seeds, cancellationToken);

    public Task SendOfferAsync(
        string sessionId, string accountId, string consoleDuid,
        IReadOnlyList<HalyardCandidate> candidates, CancellationToken cancellationToken)
        => _cloud.SendOfferAsync(sessionId, accountId, consoleDuid, candidates, cancellationToken);

    public Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
        => _cloud.GetSessionAsync(sessionId, cancellationToken);

    public Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken)
        => _cloud.LeaveSessionAsync(sessionId, cancellationToken);
}
