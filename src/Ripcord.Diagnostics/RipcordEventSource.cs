using System.Diagnostics.Tracing;

namespace Ripcord.Diagnostics;

/// <summary>
/// ETW/EventSource provider for Ripcord.
///
/// <para>
/// Everything before this was <c>Debug.WriteLine</c>, which compiles out of Release builds entirely — so the
/// shipping app produced no trace of anything, and the only diagnostic for a stutter or a disconnect was
/// whatever the user happened to notice. An EventSource costs nothing when no-one is listening, can be captured
/// with standard tooling (<c>dotnet-trace</c>, PerfView, WPR) without attaching a debugger, and correlates
/// against system-wide GPU and network events on the same timeline — which is exactly what diagnosing a latency
/// problem needs.
/// </para>
///
/// <para>
/// Capture with:
/// <c>dotnet-trace collect --providers Ripcord --process-id &lt;pid&gt;</c>
/// </para>
///
/// <para>
/// Frame-rate events (<see cref="FrameDecoded"/>, <see cref="FramePresented"/>) are on the
/// <see cref="Keywords.Frames"/> keyword so they can be left off by default: at 60 fps they are high-volume,
/// and enabling them unconditionally would be its own performance problem.
/// </para>
/// </summary>
[EventSource(Name = "Ripcord")]
public sealed class RipcordEventSource : EventSource
{
    public static readonly RipcordEventSource Log = new();

    private RipcordEventSource()
    {
    }

    /// <summary>Event categories, so a capture can ask for only what it needs.</summary>
    public static class Keywords
    {
        /// <summary>Connect, reconnect, degrade, teardown.</summary>
        public const EventKeywords Session = (EventKeywords)0x1;

        /// <summary>Per-frame decode/present. High volume — opt in deliberately.</summary>
        public const EventKeywords Frames = (EventKeywords)0x2;

        /// <summary>Periodic loss/bitrate/RTT samples.</summary>
        public const EventKeywords Network = (EventKeywords)0x4;

        /// <summary>Decode path selection, device loss, adapter choice.</summary>
        public const EventKeywords Device = (EventKeywords)0x8;

        /// <summary>Audio pipeline.</summary>
        public const EventKeywords Audio = (EventKeywords)0x10;
    }

    // ---- session lifecycle (keyword: Session) ----

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Session,
        Message = "Session state -> {0} ({1})")]
    public void SessionStateChanged(string lifecycle, string detail)
    {
        if (IsEnabled())
        {
            WriteEvent(1, lifecycle, detail);
        }
    }

    [Event(2, Level = EventLevel.Warning, Keywords = Keywords.Session,
        Message = "Reconnect attempt {0} in {1} ms: {2}")]
    public void ReconnectScheduled(int attempt, double delayMs, string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(2, attempt, delayMs, reason);
        }
    }

    [Event(3, Level = EventLevel.Error, Keywords = Keywords.Session,
        Message = "Session failed: {0}")]
    public void SessionFailed(string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(3, reason);
        }
    }

    // ---- frames (keyword: Frames — high volume) ----

    [Event(10, Level = EventLevel.Verbose, Keywords = Keywords.Frames,
        Message = "Frame decoded ({0} bytes, key={1})")]
    public void FrameDecoded(int byteCount, bool isKeyFrame)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Frames))
        {
            WriteEvent(10, byteCount, isKeyFrame);
        }
    }

    [Event(11, Level = EventLevel.Verbose, Keywords = Keywords.Frames,
        Message = "Frame presented (demux->present {0} ms, queue {1})")]
    public void FramePresented(double latencyMs, int queueDepth)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Frames))
        {
            WriteEvent(11, latencyMs, queueDepth);
        }
    }

    [Event(12, Level = EventLevel.Warning, Keywords = Keywords.Frames,
        Message = "Backlog {0} frames — resynchronising on the next key frame")]
    public void BacklogResync(int queueDepth)
    {
        if (IsEnabled())
        {
            WriteEvent(12, queueDepth);
        }
    }

    [Event(13, Level = EventLevel.Warning, Keywords = Keywords.Frames,
        Message = "Frame skipped: {0}")]
    public void FrameSkipped(string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(13, reason);
        }
    }

    // ---- network (keyword: Network) ----

    [Event(20, Level = EventLevel.Informational, Keywords = Keywords.Network,
        Message = "Network sample: {0} kbps, {1:P2} loss, {2:F2} ms RTT, rxq {3}")]
    public void NetworkSample(int bitrateKbps, double lossRatio, double rttMs, int receiveQueueDepth)
    {
        if (IsEnabled())
        {
            WriteEvent(20, bitrateKbps, lossRatio, rttMs, receiveQueueDepth);
        }
    }

    [Event(21, Level = EventLevel.Informational, Keywords = Keywords.Network,
        Message = "Quality changed to {0}x{1}@{2} / {3} kbps: {4}")]
    public void QualityChanged(int width, int height, int fps, int bitrateKbps, string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(21, width, height, fps, bitrateKbps, reason);
        }
    }

    [Event(22, Level = EventLevel.Warning, Keywords = Keywords.Network,
        Message = "Key frame requested: {0}")]
    public void KeyFrameRequested(string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(22, reason);
        }
    }

    // ---- device (keyword: Device) ----

    [Event(30, Level = EventLevel.Informational, Keywords = Keywords.Device,
        Message = "Decode path: {0} on {1} ({2}x{3})")]
    public void DecodePathSettled(string path, string adapter, int width, int height)
    {
        if (IsEnabled())
        {
            WriteEvent(30, path, adapter, width, height);
        }
    }

    [Event(31, Level = EventLevel.Critical, Keywords = Keywords.Device,
        Message = "Graphics device lost (0x{0:X8})")]
    public void DeviceLost(int hresult)
    {
        if (IsEnabled())
        {
            WriteEvent(31, hresult);
        }
    }

    // ---- audio (keyword: Audio) ----

    [Event(40, Level = EventLevel.Error, Keywords = Keywords.Audio,
        Message = "Audio init failed: {0}")]
    public void AudioInitFailed(string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(40, reason);
        }
    }

    [Event(41, Level = EventLevel.Warning, Keywords = Keywords.Audio,
        Message = "Audio frame skipped: {0}")]
    public void AudioFrameSkipped(string reason)
    {
        if (IsEnabled())
        {
            WriteEvent(41, reason);
        }
    }
}
