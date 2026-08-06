using System.Text;
using Ripcord.Client;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Diagnostics;

namespace Ripcord.Presentation.Sessions;

/// <summary>
/// The facts about the host that only the front end can answer, for the saved diagnostics report.
/// </summary>
/// <param name="AppVersion">The build, as a user would quote it in a bug report.</param>
/// <param name="OperatingSystem">OS description and architecture, already formatted.</param>
/// <param name="InputSourceName">Which input engine composition is running, or "none".</param>
/// <param name="Lifecycle">Null when there is no session, which is itself worth recording.</param>
public sealed record DiagnosticsHostInfo(
    string AppVersion,
    string OperatingSystem,
    string InputSourceName,
    SessionLifecycle? Lifecycle = null,
    string LifecycleDetail = "",
    int ReconnectAttempt = 0);

/// <summary>
/// Builds the text file behind "save diagnostics".
///
/// <para>
/// Its own type rather than a method on <see cref="SessionViewModel"/>: the report is a snapshot serialiser with
/// no state of its own, and keeping it separate means the view-model does not grow a second reason to hold the
/// settings, the histories and the host facts all at once. It is also the piece most likely to be wanted by
/// something that is not a live session — a crash handler, say.
/// </para>
///
/// <para>
/// Timestamped rather than rolling, at the call site: comparing two runs is the usual reason to capture one, and
/// a single overwritten file makes that impossible.
/// </para>
/// </summary>
public static class SessionDiagnosticsReport
{
    public static string Build(
        DateTimeOffset capturedAt,
        DiagnosticsHostInfo host,
        RipcordSettings settings,
        SessionViewState state,
        SessionTelemetry telemetry,
        IVideoPipelineStats pipeline,
        MetricHistory fps,
        MetricHistory loss,
        MetricHistory rtt,
        MetricHistory bitrate)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(pipeline);

        var report = new StringBuilder();

        report.AppendLine("Ripcord diagnostics");
        report.AppendLine($"captured           {capturedAt:u}");
        report.AppendLine($"app version        {host.AppVersion}");
        report.AppendLine($"os                 {host.OperatingSystem}");
        report.AppendLine();

        report.AppendLine("[requested]");
        report.AppendLine($"resolution         {settings.Width}x{settings.Height} @ {settings.TargetFps}");
        report.AppendLine($"bitrate cap        {settings.BitrateKbps} kbps");
        report.AppendLine($"codec              {settings.Codec}   hdr={settings.RequestHdr}");
        report.AppendLine(
            $"adaptive quality   {settings.AdaptiveQuality}   report quality={settings.ReportConnectionQuality}");
        report.AppendLine($"gpu preference     {settings.GpuPreference} (luid {settings.GpuLuid:X})");
        report.AppendLine($"upscale            {settings.UpscaleMode}");
        report.AppendLine();

        report.AppendLine("[input]");
        report.AppendLine($"source             {host.InputSourceName}");
        report.AppendLine($"controller         {state.ConnectedControllers}");
        report.AppendLine($"keyboard enabled   {settings.InputBindings.KeyboardEnabled}");
        report.AppendLine($"keys bound         {settings.InputBindings.Keyboard.Count}");
        report.AppendLine($"gamepad remaps     {settings.InputBindings.GamepadRemap.Count}");
        report.AppendLine($"exit gesture       {settings.ExitGesture}");
        report.AppendLine();

        if (!telemetry.HasSession || !pipeline.IsReady)
        {
            report.AppendLine("[session]  no live session");
            report.AppendLine();
        }
        else
        {
            VideoPipelineSnapshot s = pipeline.Read();
            SessionStatistics stats = telemetry.Statistics;
            SessionDiagnosticsState d = state.Diagnostics;

            report.AppendLine("[session]");
            report.AppendLine($"lifecycle          {host.Lifecycle} — {host.LifecycleDetail}");
            report.AppendLine($"reconnect attempt  {host.ReconnectAttempt}");
            report.AppendLine($"health             {d.Health} — {d.HealthTip}");
            report.AppendLine($"decoded            {s.DecodedWidth}x{s.DecodedHeight}");
            report.AppendLine($"decoder            {s.Decoder}");
            report.AppendLine($"decoder diagnostic {s.DecoderDiagnostic}");
            report.AppendLine($"colour matrix      {s.ColorMatrix}");
            report.AppendLine($"decode path        {s.DecodeMode} (2=zero-copy, 1=readback, 0=software)");
            report.AppendLine($"audio              {s.AudioFormat}");
            report.AppendLine($"audio frames       {s.AudioFramesDecoded} decoded, {s.AudioFramesSkipped} skipped");
            report.AppendLine($"frames             {s.DecodedFrames} decoded, {s.PresentedFrames} presented");
            report.AppendLine($"queues             decode {s.QueueDepth}, receive {stats.ReceiveQueueDepth}");
            report.AppendLine($"pipeline latency   {s.PipelineLatencyMs:F1} ms");
            report.AppendLine($"bitrate            {stats.BitrateKbps} kbps");
            report.AppendLine(
                $"rtt                {stats.RoundTripTimeMs:F2} ms   loss {stats.PacketLossRatio * 100:F2}%");
            report.AppendLine(
                $"declared link      mtu {stats.DeclaredMtu}, rtt {stats.DeclaredRttMs?.ToString("F2") ?? "not measured"}");
            report.AppendLine($"adapter            {pipeline.AdapterDescription}");
            report.AppendLine($"quality reason     {telemetry.QualityReason}");
            report.AppendLine();

            report.AppendLine("[last 30 seconds: latest / peak]");
            report.AppendLine($"fps                {fps.Latest:F0} / {fps.Max():F0}");
            report.AppendLine($"loss %             {loss.Latest:F2} / {loss.Max():F2}");
            report.AppendLine($"rtt ms             {rtt.Latest:F2} / {rtt.Max():F2}");
            report.AppendLine($"bitrate Mbps       {bitrate.Latest:F1} / {bitrate.Max():F1}");
            report.AppendLine();
        }

        if (telemetry.Power is { } power)
        {
            report.AppendLine("[power]");
            report.AppendLine(
                $"source             {power.Source}   battery {power.BatteryPercent?.ToString() ?? "n/a"}");
            report.AppendLine($"energy saver       {power.EnergySaverActive}   critical {power.BatteryCritical}");
            report.AppendLine();
        }

        return report.ToString();
    }
}
