namespace Ripcord.Core.Sessions;

/// <summary>Severity of a <see cref="StreamHealthVerdict"/>, for colour/iconography in the UI.</summary>
public enum StreamHealthLevel
{
    Healthy,
    Info,
    Warning,
    Critical,
}

/// <summary>
/// The runtime signals the health assessor reasons over. Gathered from the decode pipeline
/// (fps/queues/latency/decode path) and the session (packet loss, receive-queue depth).
/// </summary>
public readonly record struct StreamHealthSignals(
    double DecodeFps,
    double PresentFps,
    int TargetFps,
    int ReceiveQueueDepth,   // A/V datagrams waiting for the processing thread (normally ~0)
    int DecodeQueueDepth,    // decoded-frame backlog (normally 0)
    double PipelineLatencyMs, // demux→present latency of the newest frame
    double PacketLossRatio,   // 0..1, A/V unit loss over the last congestion window
    int DecodeMode,           // 0 = software, 1 = hardware + CPU readback, 2 = hardware zero-copy
    // Liveness. These are deliberately NOT defaulted: they gate every rule below, and when they were optional
    // a caller that forgot them silently pinned the verdict to "Starting…" forever with a live stream on
    // screen. Making them required turns that into a compile error.
    bool HasReceivedFrames,          // has ANY frame ever arrived this session?
    double MillisecondsSinceConnect, // since the handshake completed (drives the startup grace period)
    double MillisecondsSinceLastFrame = 0, // since the newest frame (only meaningful once HasReceivedFrames)
    double RoundTripTimeMs = 0);           // measured network RTT in ms (fractional; 0 = not yet known)

/// <summary>A plain-language health verdict: severity, a short headline, and an actionable tip.</summary>
/// <param name="Level">How bad it is.</param>
/// <param name="Headline">What is wrong, in the player's words. The same sentence at every rung.</param>
/// <param name="Tip">What to do about it, in full. Rung 3, and rung 2's expanded reading.</param>
/// <param name="Remedy">
/// The shortest useful form of <paramref name="Tip"/> — a clause, not a sentence, and empty when there is
/// nothing a player can do.
///
/// <para>
/// <b>Why a third string and not just a shorter tip.</b> Rung 1 is one line over a running game, and a
/// headline on its own is a diagnosis with no remedy: "Losing packets on the network" tells a player what is
/// happening and nothing about what to try. The full tip is two sentences and belongs at a rung someone has
/// chosen to open. This is the clause that fits on the line they did not choose to see — and it stays
/// separate so the headline is still identical at all three rungs, which is what lets rung 2 and rung 3 be
/// recognisably the same verdict rather than a second opinion.
/// </para>
/// </param>
public readonly record struct StreamHealthVerdict(
    StreamHealthLevel Level,
    string Headline,
    string Tip,
    string Remedy = "")
{
    /// <summary>
    /// The rung-1 line: the verdict and, when there is one, what to try. Composed here rather than at the
    /// call site so the em dash and the empty-remedy case have one answer.
    /// </summary>
    public string Notice => Remedy.Length == 0 ? Headline : $"{Headline} — {Remedy}";
}

/// <summary>
/// Turns raw stream telemetry into a single plain-language verdict a non-technical user can act on ("why is
/// my stream bad?"). Pure and deterministic so it is fully unit-testable off-device; the UI just renders the
/// result. Rules are evaluated most-actionable-first and the first match wins, so the user sees the one thing
/// most worth fixing rather than a wall of numbers.
/// </summary>
public static class StreamHealthAssessor
{
    // Loss over the last window. Below Warn is normal jitter; Bad is "clearly broken".
    public const double LossWarnRatio = 0.02; // 2%
    public const double LossBadRatio = 0.10;  // 10%

    // The receive queue is normally ~0 (the processor drains instantly).
    //
    // It is NOT a usable discriminator and is no longer used as one. Measured on ARM64 over four sessions
    // (~2100 live samples, 2026-09-19) it never once pointed at the right cause, and in the device-starved
    // run it pointed confidently at the wrong one: every sample carrying real loss read a depth of exactly 0
    // while the machine was demonstrably the bottleneck, and every excursion above 16 happened on a sample
    // with exactly zero loss. What it actually records is an instantaneous depth sampled at 2 Hz against
    // frame-sized arrival bursts - 0 → 34 → 0 inside two seconds, peaking at 266 during a stall whose
    // neighbouring samples both read 0. No threshold survives that, which is why this is a shape problem and
    // retuning the number was the wrong fix. Kept as a displayed diagnostic and as the low-water guard on
    // rule 4, where a spurious reading suppresses a warning rather than inventing one.
    public const int ReceiveQueueBusyDepth = 16;

    /// <summary>
    /// Presenting below this share of what we decode means the device is shedding frames it has already paid
    /// to decode — the signal that actually separates "this device cannot keep up" from "the network is
    /// dropping packets".
    ///
    /// <para>
    /// The logic is mechanical rather than tuned: if the <em>network</em> is the bottleneck the frames never
    /// arrive, so decode and present fall together and the ratio stays near 1. If the <em>device</em> is the
    /// bottleneck the frames arrive and decode fine and we fail to put them on screen, so the ratio collapses.
    /// Measured on the device-starved run, all six samples carrying loss sat between 0.00 and 0.11 against a
    /// session median of 0.87–0.93, so 0.5 has daylight on both sides rather than being fitted to the data.
    /// </para>
    /// </summary>
    public const double PresentedShareBusyRatio = 0.5;

    // demux→present latency this high (with the queues near empty) means the GPU decode itself is slow.
    public const double PipelineLatencyWarnMs = 60;

    // Presenting below this fraction of the target frame rate counts as "below target".
    public const double FpsShortfallFactor = 0.75;

    /// <summary>How long the first frame may take before silence stops being normal startup latency.</summary>
    public const double StartupGraceMs = 5_000;

    /// <summary>No new frame for this long, after frames HAD been flowing, means the stream has stalled.</summary>
    public const double StallMs = 2_000;

    /// <summary>RTT at or above this makes the stream feel laggy however clean the picture is.</summary>
    public const double RttWarnMs = 60;
    public const double RttBadMs = 120;

    public static StreamHealthVerdict Assess(in StreamHealthSignals s)
    {
        // 0) Liveness comes first: a stalled or never-started stream makes every throughput rule below
        //    meaningless (they would all read as a healthy zero).
        if (s.HasReceivedFrames && s.MillisecondsSinceLastFrame >= StallMs)
        {
            return new StreamHealthVerdict(StreamHealthLevel.Critical,
                "The stream has stopped",
                "Video stopped arriving from the console. Check your network connection — if the console went to "
                + "sleep or another player took over the session, you'll need to reconnect.",
                "try reconnecting");
        }

        if (!s.HasReceivedFrames)
        {
            // Still inside the startup window: genuinely just waiting.
            if (s.MillisecondsSinceConnect < StartupGraceMs)
            {
                return new StreamHealthVerdict(StreamHealthLevel.Info, "Starting…", "Waiting for video from the console.");
            }

            // Past it: the handshake succeeded but no picture ever came. That is a real failure, and saying
            // "Starting…" forever is the single most misleading thing this class could do.
            return new StreamHealthVerdict(StreamHealthLevel.Critical,
                "No video from the console",
                "The connection succeeded but no video arrived. Make sure a user is logged in on the console and "
                + "that its remote-play setting is enabled, then try reconnecting.",
                "check someone is logged in on the console");
        }

        // 1) Packet loss is the most impactful problem. Split "our device can't keep up" from "the network is
        //    dropping packets" by asking whether we are presenting what we decode - see IsSheddingFrames.
        if (s.PacketLossRatio >= LossWarnRatio)
        {
            StreamHealthLevel level = s.PacketLossRatio >= LossBadRatio ? StreamHealthLevel.Critical : StreamHealthLevel.Warning;
            if (IsSheddingFrames(s))
            {
                return new StreamHealthVerdict(level,
                    "Your device is struggling to keep up",
                    "Close other apps, or lower the stream resolution/quality. A more powerful device will also help.",
                    "closing other apps will help");
            }

            return new StreamHealthVerdict(level,
                "Losing packets on the network",
                "If you're on Wi‑Fi, switch to a wired connection, move closer to the router, or turn off your Wi‑Fi adapter's power saving.",
                "a wired connection will help");
        }

        // 2) Software decode — high CPU/battery, and it can't hold higher resolutions.
        if (s.DecodeMode == 0)
        {
            return new StreamHealthVerdict(StreamHealthLevel.Warning,
                "Video is decoding on the CPU (software)",
                "Update your graphics drivers to enable hardware decoding — software decode uses far more CPU and battery.",
                "updating your graphics drivers will help");
        }

        // 3) GPU decode is slow (queues aren't backed up, so it's the decode itself, not delivery).
        if (s.PipelineLatencyMs >= PipelineLatencyWarnMs && s.DecodeQueueDepth < ReceiveQueueBusyDepth && s.ReceiveQueueDepth < ReceiveQueueBusyDepth)
        {
            return new StreamHealthVerdict(StreamHealthLevel.Warning,
                "Video is decoding slowly on this device",
                "Try a lower resolution; a more capable GPU will reduce the delay.",
                "try a lower resolution");
        }

        // 3b) Decoding fine, failing to present. Ranked above the readback note below because that note says
        //     "This is fine", and it was reaching the screen while the stream presented 4 fps out of 60
        //     decoded - 16-17% of samples in both device-starved runs got a Healthy/Info verdict while
        //     presenting under 45 fps. A verdict that says everything is fine during a visible stutter is
        //     worse than no verdict, because it is the one the player checks before giving up.
        if (IsSheddingFrames(s))
        {
            return new StreamHealthVerdict(StreamHealthLevel.Warning,
                "This device is dropping frames it already decoded",
                "The video is arriving and decoding, but not reaching the screen fast enough. Try a lower "
                + "resolution, close other apps, or pick a GPU that supports the zero-copy path.",
                "try a lower resolution");
        }

        // 4) Hardware decode works but isn't zero-copy — a minor extra memory copy per frame.
        if (s.DecodeMode == 1)
        {
            return new StreamHealthVerdict(StreamHealthLevel.Info,
                "Hardware decode active (with a memory copy)",
                "This is fine. A graphics-driver or GPU update may enable the faster zero‑copy path.");
        }

        // 5) Frame rate below target with no loss — usually the console sending fewer frames, or mild strain.
        if (s.TargetFps > 0 && s.PresentFps > 0 && s.PresentFps < s.TargetFps * FpsShortfallFactor)
        {
            return new StreamHealthVerdict(StreamHealthLevel.Info,
                "Frame rate is below target",
                "The console may be sending fewer frames than requested, or the stream is under mild strain.");
        }

        // 6) A clean picture can still feel laggy. Ranked last because it degrades feel rather than fidelity —
        //    but it is the thing players describe as "input lag", so it must be sayable.
        if (s.RoundTripTimeMs >= RttWarnMs)
        {
            return new StreamHealthVerdict(
                s.RoundTripTimeMs >= RttBadMs ? StreamHealthLevel.Warning : StreamHealthLevel.Info,
                $"Network delay is high ({s.RoundTripTimeMs:F0} ms)",
                "The picture is clean but controls will feel delayed. A wired connection, or moving closer to the "
                + "router, is the only thing that helps — this is round-trip time, not a device limitation.",
                "a wired connection will help");
        }

        return new StreamHealthVerdict(StreamHealthLevel.Healthy, "Connection healthy", "Everything looks good.");
    }

    /// <summary>
    /// Are we decoding frames and then failing to put them on screen?
    ///
    /// <para>
    /// <c>DecodeFps &gt; 1</c> guards the degenerate cases: a stream that has stopped, and the first sample of
    /// one that has not started, both present zero out of zero and would otherwise read as total shedding.
    /// </para>
    /// </summary>
    private static bool IsSheddingFrames(in StreamHealthSignals s)
        => s.DecodeFps > 1 && s.PresentFps < s.DecodeFps * PresentedShareBusyRatio;
}
