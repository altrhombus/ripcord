using Ripcord.Core.Input;
using Ripcord.Core.Reactive;
using Ripcord.Core.Threading;

namespace Ripcord.Input;

/// <summary>
/// Runs every input engine at once and merges their frames, instead of choosing one at start-up.
///
/// <para>
/// Choosing once (DualSense raw HID when a DualSense is present, GameInput otherwise) would mean a swap from a
/// DualSense to an Xbox pad mid-session is not noticed until the session is reopened.
/// </para>
///
/// <para>
/// Running both engines does not by itself let an Xbox pad work beside a DualSense. Measured on hardware
/// 2026-08-06: with both attached and GameInput asked for "the most recent reading from any gamepad", not one
/// Xbox frame with a button set reached this class, because a Bluetooth DualSense wins essentially always.
/// <see cref="GameInputControllerSource"/> therefore reads every GameInput pad from its own device and merges
/// them before they get here, since this class keeps one frame per engine, not per device.
/// </para>
///
/// <para>
/// Merging is safe even when two engines see the SAME physical pad, which happens with a DualSense that GameInput
/// also enumerates: buttons are OR-ed, so identical bits give the same result, and axes take whichever is further
/// from centre, so identical values give that value. Duplicate reporting is therefore a no-op. It also means
/// several controllers act as one pad, which is what a single console session wants.
/// </para>
/// </summary>
public sealed class CompositeControllerSource : IControllerSource, IDisposable
{
    private readonly IControllerSource[] _sources;
    private readonly SimpleObservable<ControllerStateFrame> _frames = new();
    private readonly SimpleObservable<ControllerConnectionEvent> _connections = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly EngineFrames _latest;

    // Every device currently reported connected, keyed by engine + id. Replayed to new subscribers: replaying only
    // the LAST event would be wrong here, because several pads can be attached and a late subscriber would learn
    // about exactly one of them.
    private readonly Dictionary<string, ControllerConnectionEvent> _connected = [];
    // A SpinGate, not a lock: the UI thread enters it, and a contended lock there lets XAML re-enter and fail
    // fast. See SpinGate.
    private readonly SpinGate _gate = new();

    public CompositeControllerSource(params IControllerSource[] sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));

        // In the order given, which is the precedence for a press two engines both report: the factory lists the
        // raw-HID engine first, so a DualSense GameInput also reads is named as a DualSense (see EngineFrames).
        _latest = new EngineFrames([.. _sources.Select(s => s.SourceName)]);

        foreach (IControllerSource source in _sources)
        {
            IControllerSource captured = source;

            _subscriptions.Add(captured.StateChanges(string.Empty).Subscribe(
                new AnonymousObserver<ControllerStateFrame>(frame => OnFrame(captured.SourceName, frame))));

            // Connection events are re-published with the engine that saw the device, so the diagnostics overlay
            // can distinguish "an Xbox pad via GameInput" from "a DualSense via raw HID" when both are attached.
            _subscriptions.Add(captured.Connections.Subscribe(
                new AnonymousObserver<ControllerConnectionEvent>(evt => OnConnectionEvent(captured.SourceName, evt))));
        }
    }

    /// <summary>Every engine that is running, so the overlay reports capability rather than a single choice.</summary>
    public string SourceName => string.Join(" + ", _sources.Select(s => s.SourceName));

    /// <summary>
    /// Connection events, with every already-connected device replayed to a new subscriber.
    ///
    /// <para>
    /// Without the replay a controller attached before the UI subscribed is never reported, which shows up as a
    /// working pad described as "none attached" — and only on hardware whose controller is present from boot.
    /// </para>
    /// </summary>
    public IObservable<ControllerConnectionEvent> Connections => new ReplayingConnections(this);

    public IObservable<ControllerStateFrame> StateChanges(string controllerId) => _frames;

    /// <summary>
    /// Extended features (adaptive triggers, haptics, lightbar) from the first engine that offers them for
    /// <paramref name="controllerId"/>, or null when none does.
    /// </summary>
    public IHapticsAndExtendedFeatures? GetExtendedFeatures(string controllerId)
    {
        foreach (IControllerSource source in _sources)
        {
            if (source.GetExtendedFeatures(controllerId) is { } features)
            {
                return features;
            }
        }

        return null;
    }

    private void OnConnectionEvent(string sourceName, ControllerConnectionEvent evt)
    {
        ControllerConnectionEvent tagged = evt with { Source = sourceName };
        string key = $"{sourceName}|{tagged.ControllerId}";
        bool forgot = false;
        ControllerStateFrame merged = default;

        using (_gate.Enter())
        {
            if (tagged.Connected)
            {
                _connected[key] = tagged;
            }
            else
            {
                _connected.Remove(key);

                // The engine's last device went, so its last frame goes too: kept, it would be merged into every
                // frame after, and whatever was held when the pad was pulled would stay held (see EngineFrames).
                string prefix = sourceName + "|";
                if (!_connected.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    forgot = _latest.Forget(sourceName, DateTime.UtcNow.Ticks, out merged);
                }
            }
        }

        _connections.Publish(tagged);

        if (forgot)
        {
            _frames.Publish(merged);
        }
    }

    /// <summary>Subscribes, then replays the connected set, so nothing is missed and nothing stale is invented.</summary>
    private sealed class ReplayingConnections(CompositeControllerSource owner) : IObservable<ControllerConnectionEvent>
    {
        public IDisposable Subscribe(IObserver<ControllerConnectionEvent> observer)
        {
            // Subscribe FIRST: taking the snapshot first would let an event arriving in between be lost.
            IDisposable subscription = owner._connections.Subscribe(observer);

            ControllerConnectionEvent[] snapshot;
            using (owner._gate.Enter())
            {
                snapshot = [.. owner._connected.Values];
            }

            foreach (ControllerConnectionEvent evt in snapshot)
            {
                observer.OnNext(evt);
            }

            return subscription;
        }
    }

    /// <summary>
    /// The engine in use changed: someone pressed something on a pad from a different engine than the last
    /// (<see cref="EngineFrames.Active"/>). Raised on that engine's reader thread, outside the gate.
    /// </summary>
    public event Action<string>? ActiveEngineChanged;

    private void OnFrame(string sourceName, ControllerStateFrame frame)
    {
        ControllerStateFrame merged;
        string? activeBefore;
        string? activeNow;
        using (_gate.Enter())
        {
            // Rebuilt from the latest of every engine rather than accumulated, so a released button on one device
            // is actually released: OR-ing into a running total would latch every button ever pressed.
            activeBefore = _latest.Active;
            merged = _latest.Set(sourceName, frame);
            activeNow = _latest.Active;
        }

        if (activeNow is not null && activeNow != activeBefore)
        {
            ActiveEngineChanged?.Invoke(activeNow);
        }

        // Published outside the lock: subscribers include the session send path, which must never be able to stall
        // an input reader thread.
        _frames.Publish(merged);
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        foreach (IControllerSource source in _sources)
        {
            (source as IDisposable)?.Dispose();
        }
    }
}
