using System.Diagnostics.Tracing;
using Ripcord.Diagnostics;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The ETW provider. An EventSource with a malformed event definition fails <em>silently</em> — the provider
/// simply emits nothing, and you find out when a capture you needed turns out to be empty. These tests attach a
/// listener and assert events actually arrive, which is the only way to know the definitions are valid.
/// </summary>
public class RipcordEventSourceTests
{
    /// <summary>Captures events from the Ripcord provider for the lifetime of the instance.</summary>
    private sealed class Listener : EventListener
    {
        private readonly List<EventWrittenEventArgs> _events = [];
        private readonly Lock _gate = new();
        private readonly EventLevel _level;
        private readonly EventKeywords _keywords;

        public Listener(EventLevel level = EventLevel.Verbose, EventKeywords keywords = EventKeywords.All)
        {
            _level = level;
            _keywords = keywords;

            // Any source created before this listener existed needs enabling explicitly.
            if (RipcordEventSource.Log is { } log)
            {
                EnableEvents(log, _level, _keywords);
            }
        }

        public IReadOnlyList<EventWrittenEventArgs> Events
        {
            get { lock (_gate) return [.. _events]; }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Ripcord")
            {
                EnableEvents(eventSource, _level, _keywords);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (_gate)
            {
                _events.Add(eventData);
            }
        }
    }

    [Fact]
    public void ProviderIsWellFormed()
    {
        // EventSource validates its own definitions when constructed under this switch; a duplicate event id or
        // a WriteEvent argument mismatch shows up here rather than as silent nothing at capture time.
        Exception? failure = Record.Exception(() =>
        {
            using var listener = new Listener();
            RipcordEventSource.Log.SessionStateChanged("Streaming", "Connected.");
        });

        Assert.Null(failure);
        Assert.False(RipcordEventSource.Log.ConstructionException is not null,
            $"provider failed to construct: {RipcordEventSource.Log.ConstructionException}");
    }

    [Fact]
    public void SessionEvents_AreEmittedWithTheirPayload()
    {
        using var listener = new Listener();

        RipcordEventSource.Log.SessionStateChanged("Reconnecting", "Connection failed.");
        RipcordEventSource.Log.ReconnectScheduled(2, 2000, "network unreachable");
        RipcordEventSource.Log.SessionFailed("gave up");

        var names = listener.Events.Select(e => e.EventName).ToList();
        Assert.Contains(nameof(RipcordEventSource.SessionStateChanged), names);
        Assert.Contains(nameof(RipcordEventSource.ReconnectScheduled), names);
        Assert.Contains(nameof(RipcordEventSource.SessionFailed), names);

        EventWrittenEventArgs reconnect = listener.Events
            .First(e => e.EventName == nameof(RipcordEventSource.ReconnectScheduled));
        Assert.Equal(2, reconnect.Payload![0]);
        Assert.Equal(2000.0, reconnect.Payload[1]);
        Assert.Equal("network unreachable", reconnect.Payload[2]);
    }

    [Fact]
    public void NetworkAndDeviceEvents_AreEmitted()
    {
        using var listener = new Listener();

        RipcordEventSource.Log.NetworkSample(7200, 0.03, 12, 0);
        RipcordEventSource.Log.QualityChanged(1280, 720, 60, 6000, "stepped down");
        RipcordEventSource.Log.DecodePathSettled("zero-copy", "Test GPU", 1920, 1080);
        RipcordEventSource.Log.DeviceLost(unchecked((int)0x887A0005));

        var names = listener.Events.Select(e => e.EventName).ToList();
        Assert.Contains(nameof(RipcordEventSource.NetworkSample), names);
        Assert.Contains(nameof(RipcordEventSource.QualityChanged), names);
        Assert.Contains(nameof(RipcordEventSource.DecodePathSettled), names);
        Assert.Contains(nameof(RipcordEventSource.DeviceLost), names);

        EventWrittenEventArgs sample = listener.Events
            .First(e => e.EventName == nameof(RipcordEventSource.NetworkSample));
        Assert.Equal(7200, sample.Payload![0]);
        Assert.Equal(0.03, (double)sample.Payload[1]!, precision: 6);
    }

    [Fact]
    public void FrameEvents_AreOffUnlessTheFramesKeywordIsRequested()
    {
        // The whole point of putting per-frame events behind a keyword: at 60 fps, capturing them
        // unconditionally would itself be a performance problem.
        using var listener = new Listener(EventLevel.Verbose, RipcordEventSource.Keywords.Session);

        RipcordEventSource.Log.FrameDecoded(4096, isKeyFrame: true);
        RipcordEventSource.Log.FramePresented(16.7, 0);

        var names = listener.Events.Select(e => e.EventName).ToList();
        Assert.DoesNotContain(nameof(RipcordEventSource.FrameDecoded), names);
        Assert.DoesNotContain(nameof(RipcordEventSource.FramePresented), names);
    }

    [Fact]
    public void FrameEvents_AreEmittedWhenTheFramesKeywordIsRequested()
    {
        using var listener = new Listener(EventLevel.Verbose, RipcordEventSource.Keywords.Frames);

        RipcordEventSource.Log.FrameDecoded(4096, isKeyFrame: true);
        RipcordEventSource.Log.FramePresented(16.7, 2);

        var names = listener.Events.Select(e => e.EventName).ToList();
        Assert.Contains(nameof(RipcordEventSource.FrameDecoded), names);
        Assert.Contains(nameof(RipcordEventSource.FramePresented), names);
    }

    [Fact]
    public void LoggingWithNoListener_IsHarmless()
    {
        // The common case in production: nothing attached, so every call must be a cheap no-op that cannot throw.
        RipcordEventSource.Log.SessionStateChanged("Idle", "no listener attached");
        RipcordEventSource.Log.FrameDecoded(1, false);
        RipcordEventSource.Log.NetworkSample(0, 0, 0, 0);
    }
}
