namespace Ripcord.Cloud.Halyard.Rendezvous;

/// <summary>
/// A completed WAN rendezvous: the console's candidates, and ownership of the session and push channel for the
/// life of the stream.
///
/// <para>
/// The rendezvous returns this rather than just the candidates because the session and the push channel must
/// stay live <em>after</em> the candidates are known — the push channel carries ongoing signaling (further
/// OFFER/ACCEPT/RESULT rounds and any renegotiation), and the session must be left cleanly on disconnect.
/// Disposing this is the disconnect: it stops the push loop, closes the socket, and removes us from the
/// session.
/// </para>
/// </summary>
public sealed class HalyardWanConnection : IAsyncDisposable
{
    private readonly IHalyardSignalingClient _signaling;
    private readonly HalyardPushChannel _pushChannel;
    private readonly Task _pushLoop;
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;

    internal HalyardWanConnection(
        string sessionId,
        IReadOnlyList<HalyardSignalingCandidate> consoleCandidates,
        IHalyardSignalingClient signaling,
        HalyardPushChannel pushChannel,
        Task pushLoop,
        CancellationTokenSource lifetime)
    {
        SessionId = sessionId;
        ConsoleCandidates = consoleCandidates;
        _signaling = signaling;
        _pushChannel = pushChannel;
        _pushLoop = pushLoop;
        _lifetime = lifetime;
    }

    /// <summary>The cloud session id, for the leave-on-disconnect call and diagnostics.</summary>
    public string SessionId { get; }

    /// <summary>
    /// The console's reachable candidates, in the order it advertised them — what the direct transport connects
    /// to. Typically a reflexive/relay <c>STATIC</c> and a <c>LOCAL</c> on port 9303, and possibly a
    /// <c>STUN</c> candidate.
    /// </summary>
    public IReadOnlyList<HalyardSignalingCandidate> ConsoleCandidates { get; }

    /// <summary>The still-running push channel, for a session that wants to observe ongoing signaling.</summary>
    public HalyardPushChannel PushChannel => _pushChannel;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Leave the session first — a best-effort courtesy so the console and PSN learn we are gone promptly,
        // but never allowed to throw out of teardown.
        try
        {
            using var leaveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _signaling.LeaveSessionAsync(SessionId, leaveTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pushLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await _pushChannel.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
