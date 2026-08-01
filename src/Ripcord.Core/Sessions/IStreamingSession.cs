namespace Ripcord.Core.Sessions;

public enum SessionState
{
    Connecting,
    Streaming,
    Degraded,
    Reconnecting,
    Closed,
}

public sealed record SessionHandshakeResult(bool Succeeded, string? FailureReason);

/// <param name="BitrateKbps">
/// The <em>measured</em> incoming bitrate over the last sampling window, not the requested rate. 0 before the
/// first sample.
/// </param>
public sealed record SessionStatistics(
    double RoundTripTimeMs,
    int BitrateKbps,
    double Fps,
    double PacketLossRatio,
    double DecodeTimeMs,
    int ReceiveQueueDepth = 0,
    /// <summary>MTU declared to the console in the launchSpec, measured where possible. Session-constant rather
    /// than per-sample, but carried here because this is the record that already reaches the overlay.</summary>
    int DeclaredMtu = 0,
    /// <summary>Round-trip time declared at launch, or null if the bring-up could not measure one. Distinct from
    /// <see cref="RoundTripTimeMs"/>, which is the live per-sample figure.</summary>
    double? DeclaredRttMs = null,
    /// <summary>Whether the declared MTU was VERIFIED by the senkusha probe on the real path, as opposed to
    /// inferred from the local interface. Surfaced because a measurement and an assumption should not read
    /// identically.</summary>
    bool MtuConfirmed = false);

/// <summary>
/// The seam every protocol backend implements. This must stay free of any platform-specific
/// concept - it's what lets a second console family's backend slot in later without rearchitecting
/// the first one.
/// </summary>
public interface IStreamingSession : IAsyncDisposable
{
    Task<SessionHandshakeResult> ConnectAsync(SessionConfig config, CancellationToken cancellationToken);

    IObservable<EncodedVideoFrame> VideoFrames { get; }

    IObservable<EncodedAudioFrame> AudioFrames { get; }

    ISessionInputSink InputSink { get; }

    IBandwidthController BandwidthController { get; }

    SessionState State { get; }

    /// <summary>
    /// Milliseconds since ANYTHING arrived from the console, or null if nothing has yet.
    ///
    /// <para>
    /// Deliberately separate from video-frame liveness. A static scene gives the encoder nothing to encode, so
    /// frames stop while the session is healthy — a stall watchdog that cannot tell those apart will reconnect a
    /// working session because the user paused their game.
    /// </para>
    /// </summary>
    double? MillisecondsSinceConsoleActivity { get; }

    IObservable<SessionStatistics> Statistics { get; }

    /// <summary>
    /// Ask the console for a fresh key frame (IDR). Used to resynchronise after frames have been dropped: the
    /// reference chain is broken at that point, so a clean restart point is the fastest route back to a correct
    /// picture. Best-effort and expected to be rate-limited by the implementation — callers may invoke it
    /// whenever they detect corruption without worrying about flooding the uplink.
    /// </summary>
    void RequestKeyFrame();
}
