namespace Ripcord.Presentation.Sessions;

using System.Globalization;
using Ripcord.Core.Sessions;

/// <summary>
/// One row of the session trace: the raw numbers behind a single stats tick.
/// </summary>
/// <param name="ElapsedSeconds">Seconds since the trace started, so two runs can be lined up.</param>
/// <param name="PresentFps">Frames actually presented, measured over the interval.</param>
/// <param name="DecodeFps">Frames decoded over the same interval. Diverges from presented when frames are dropped.</param>
/// <param name="LossPercent">Packet loss over the console's window, as a percentage.</param>
/// <param name="RttMs">Live round-trip time.</param>
/// <param name="BitrateMbps">Measured bitrate, not the requested one.</param>
/// <param name="ReceiveQueueDepth">
/// The reading this trace exists for. It is the discriminator between "the network is dropping packets" and
/// "this device cannot keep up", and the threshold it is compared against is not known to be right on ARM64.
/// </param>
/// <param name="DecodeQueueDepth">Frames waiting on the decoder.</param>
/// <param name="PipelineLatencyMs">Demux to present, end to end.</param>
/// <param name="DecodeMode">2 zero-copy, 1 readback, 0 software.</param>
/// <param name="HealthLevel">What the assessor made of this sample.</param>
public sealed record SessionSample(
    double ElapsedSeconds,
    double PresentFps,
    double DecodeFps,
    double LossPercent,
    double RttMs,
    double BitrateMbps,
    int ReceiveQueueDepth,
    int DecodeQueueDepth,
    double PipelineLatencyMs,
    int DecodeMode,
    StreamHealthLevel HealthLevel);

/// <summary>
/// The session trace: every stats tick, as CSV.
///
/// <para>
/// <b>Why this exists.</b> Answering "is the receive-queue threshold right on this hardware" needs a
/// distribution, not a glance. The only way to read that number today is to watch a twice-a-second figure on
/// rung 3 and try to catch the peak by eye, or to press F8 repeatedly and diff text files. Both are a way of
/// asking someone to be a chart recorder, and neither survives the moment they look away.
/// </para>
///
/// <para>
/// <b>What it deliberately does not contain.</b> No console name, no address, no host id, no account. A trace
/// is the thing a person is most likely to paste into an issue, so it carries measurements and nothing that
/// identifies who took them. The GPU model is the one piece of machine detail included, because "which
/// adapter" changes the answer and it names a product rather than a person.
/// </para>
///
/// <para>
/// Formatting is invariant-culture throughout: a trace written on a machine with comma decimals and read on
/// one without is a silent corruption, and this is a file written specifically to be sent somewhere else.
/// </para>
/// </summary>
public static class SessionSampleLog
{
    /// <summary>The CSV header, matching <see cref="Row"/> field for field.</summary>
    public const string Header =
        "elapsed_s,present_fps,decode_fps,loss_pct,rtt_ms,bitrate_mbps," +
        "receive_queue,decode_queue,pipeline_ms,decode_mode,health";

    /// <summary>
    /// The comment block that opens a trace. Hash-prefixed so it can be skipped by anything reading the CSV,
    /// and present so a file that arrives on its own still says what it was a trace OF — which run this is
    /// can otherwise only be reconstructed from the numbers.
    /// </summary>
    public static IReadOnlyList<string> Preamble(
        string appVersion, string adapter, string codec, int width, int height, int targetFps, int bitrateCapKbps)
        =>
        [
            "# Ripcord session trace",
            FormattableString.Invariant($"# app: {appVersion}"),
            FormattableString.Invariant($"# adapter: {adapter}"),
            FormattableString.Invariant($"# requested: {width}x{height}@{targetFps} {codec}, cap {bitrateCapKbps / 1000.0:F0} Mbps"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"# thresholds: loss_warn={StreamHealthAssessor.LossWarnRatio * 100:F0}% loss_bad={StreamHealthAssessor.LossBadRatio * 100:F0}% receive_queue_busy={StreamHealthAssessor.ReceiveQueueBusyDepth} rtt_warn={StreamHealthAssessor.RttWarnMs:F0}ms"),
            "#",
            Header,
        ];

    /// <summary>One sample as a CSV row.</summary>
    public static string Row(SessionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sample.ElapsedSeconds:F1},{sample.PresentFps:F1},{sample.DecodeFps:F1},{sample.LossPercent:F2}," +
            $"{sample.RttMs:F1},{sample.BitrateMbps:F2},{sample.ReceiveQueueDepth},{sample.DecodeQueueDepth}," +
            $"{sample.PipelineLatencyMs:F1},{sample.DecodeMode},{sample.HealthLevel}");
    }
}
