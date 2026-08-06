using Ripcord.Core.Input;
using Ripcord.Core.Reactive;

namespace Ripcord.Input;

/// <summary>
/// Runs every input engine at once and merges their frames, instead of choosing one at start-up.
///
/// <para>
/// The previous factory picked DualSense raw HID when a DualSense was present and GameInput otherwise, decided
/// once when the session page loaded. Two confirmed consequences: swapping from a DualSense to an Xbox pad
/// mid-session was never noticed (the app kept reporting raw HID until the session was closed and reopened), and
/// with a DualSense attached an Xbox pad could not be used at all. Running both engines fixes the first and the
/// act of choosing; it does <b>not</b> fix the second, and this comment used to claim otherwise.
/// </para>
///
/// <para>
/// <b>Known limitation, measured on hardware 2026-08-06.</b> With a DualSense and an Xbox pad both attached,
/// the Xbox pad produces no input at all — not one frame with a button set reaches this class. The cause is
/// below us: <see cref="GameInputControllerSource"/> polls for "the first connected GameInput gamepad" via
/// <c>GetCurrentReading(GameInputKindGamepad, nullptr, …)</c>, and <c>nullptr</c> means "most recent reading
/// from any device" — a Bluetooth DualSense, which GameInput also enumerates, reports continuously and wins
/// that race essentially always. Disconnecting the DualSense is not enough on its own either; the Xbox pad had
/// to be re-plugged before GameInput would read it, so the binding is not re-evaluated when the competing
/// device goes away.
/// </para>
///
/// <para>
/// So the merging below is correct and does what it says — it is simply never handed the second pad's frames.
/// The fix belongs in the GameInput engine (enumerate devices and read each explicitly rather than passing
/// <c>nullptr</c>), and is tracked in ROADMAP rather than worked around here.
/// </para>
///
/// <para>
/// Merging is safe even when two engines see the SAME physical pad, which happens with a DualSense that GameInput
/// also enumerates: buttons are OR-ed, so identical bits give the same result, and axes take whichever is further
/// from centre, so identical values give that value. Duplicate reporting is therefore a no-op rather than something
/// to detect and suppress. It also means several controllers act as one pad, which is what a single console session
/// wants — whatever the user presses, on whichever device, reaches the console.
/// </para>
/// </summary>
public sealed class CompositeControllerSource : IControllerSource, IDisposable
{
    private readonly IControllerSource[] _sources;
    private readonly SimpleObservable<ControllerStateFrame> _frames = new();
    private readonly SimpleObservable<ControllerConnectionEvent> _connections = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, ControllerStateFrame> _latest = [];

    // Every device currently reported connected, keyed by engine + id. Replayed to new subscribers: replaying only
    // the LAST event would be wrong here, because several pads can be attached and a late subscriber would learn
    // about exactly one of them.
    private readonly Dictionary<string, ControllerConnectionEvent> _connected = [];
    private readonly Lock _gate = new();

    public CompositeControllerSource(params IControllerSource[] sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));

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
    /// Without the replay a controller attached before the UI subscribed was never reported, which showed up as a
    /// working pad described as "none attached" — and only on hardware whose controller is present from boot.
    /// </para>
    /// </summary>
    public IObservable<ControllerConnectionEvent> Connections => new ReplayingConnections(this);

    public IObservable<ControllerStateFrame> StateChanges(string controllerId) => _frames;

    /// <summary>
    /// Extended features from whichever engine offers them. Only the raw-HID engine can (adaptive triggers,
    /// haptics, lightbar), so this resolves to it when a DualSense is attached and to null otherwise.
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

        lock (_gate)
        {
            if (tagged.Connected)
            {
                _connected[key] = tagged;
            }
            else
            {
                _connected.Remove(key);
            }
        }

        _connections.Publish(tagged);
    }

    /// <summary>Subscribes, then replays the connected set, so nothing is missed and nothing stale is invented.</summary>
    private sealed class ReplayingConnections(CompositeControllerSource owner) : IObservable<ControllerConnectionEvent>
    {
        public IDisposable Subscribe(IObserver<ControllerConnectionEvent> observer)
        {
            // Subscribe FIRST: taking the snapshot first would let an event arriving in between be lost.
            IDisposable subscription = owner._connections.Subscribe(observer);

            ControllerConnectionEvent[] snapshot;
            lock (owner._gate)
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

    private void OnFrame(string sourceName, ControllerStateFrame frame)
    {
        ControllerStateFrame merged;
        lock (_gate)
        {
            _latest[sourceName] = frame;

            // Rebuilt from the latest of every engine rather than accumulated, so a released button on one device
            // is actually released: OR-ing into a running total would latch every button ever pressed.
            merged = _latest.Values.Aggregate(ControllerInputMerger.Merge);
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
