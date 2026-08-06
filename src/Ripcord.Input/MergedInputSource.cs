using Ripcord.Core.Input;
using Ripcord.Core.Reactive;

namespace Ripcord.Input;

/// <summary>
/// One frame stream out of a gamepad and a keyboard, with the gamepad remap applied.
///
/// <para>
/// The session consumes a single stream of <see cref="ControllerStateFrame"/>, and each frame is <em>absolute
/// state</em> rather than a delta. Simply forwarding both sources would mean the pad's resting frame cancelling a
/// held key at whatever rate the pad reports, so the latest of each is kept and merged on every change. That is
/// also what allows a pad and keyboard to be used together, which is the normal case for a handheld on a desk.
/// </para>
///
/// <para>
/// Keyboard events arrive from the UI thread and pad frames from a device thread, so the held state and the last
/// pad frame are both guarded. Frames are published outside the lock: a subscriber that blocks (the session send
/// path) must not be able to stall the key handler and make typing feel stuck.
/// </para>
/// </summary>
public sealed class MergedInputSource : IObservable<ControllerStateFrame>, IDisposable
{
    private readonly SimpleObservable<ControllerStateFrame> _frames = new();
    private readonly KeyboardInputTranslator _keyboard;
    private readonly IDisposable? _padSubscription;
    private readonly Lock _gate = new();

    private ControllerStateFrame _lastPad;
    private bool _havePad;

    // Buttons asserted by on-screen controls. A MASK held across frames rather than a one-shot pulse, because a
    // tap and a hold mean different things to the console — holding PS opens the power menu — so press and release
    // are distinct events here just as they are on a physical pad.
    private ControllerButtons _virtualButtons;
    private InputBindings _bindings;

    /// <param name="pad">The gamepad frame stream, or null when only a keyboard is available.</param>
    /// <param name="bindings">Key map and gamepad remap; may be replaced later via <see cref="Bindings"/>.</param>
    public MergedInputSource(IObservable<ControllerStateFrame>? pad, InputBindings bindings)
    {
        _bindings = bindings;
        _keyboard = new KeyboardInputTranslator(bindings);
        _padSubscription = pad?.Subscribe(new AnonymousObserver<ControllerStateFrame>(OnPadFrame));
    }

    public InputBindings Bindings
    {
        get { lock (_gate) return _bindings; }
        set
        {
            lock (_gate)
            {
                _bindings = value;
                _keyboard.Bindings = value;
            }
        }
    }

    public IDisposable Subscribe(IObserver<ControllerStateFrame> observer) => _frames.Subscribe(observer);

    /// <summary>
    /// Assert or release a button from an on-screen control.
    ///
    /// <para>
    /// This is the third input path alongside the pad and the keyboard, and it exists because some buttons are
    /// simply unreachable otherwise: an Xbox pad has no PS button, and Windows claims its Guide button for the shell
    /// before we ever see it. Held state is kept rather than pulsed so a long press behaves like a long press.
    /// </para>
    /// </summary>
    public void SetVirtualButton(ControllerButtons button, bool pressed)
    {
        ControllerStateFrame frame;
        lock (_gate)
        {
            ControllerButtons updated = pressed ? _virtualButtons | button : _virtualButtons & ~button;
            if (updated == _virtualButtons)
            {
                return;
            }

            _virtualButtons = updated;
            frame = Compose();
        }

        _frames.Publish(frame);
    }

    /// <summary>Release every on-screen button. Used when the controls are dismissed mid-press.</summary>
    public void ReleaseVirtualButtons()
    {
        ControllerStateFrame frame;
        lock (_gate)
        {
            if (_virtualButtons == ControllerButtons.None)
            {
                return;
            }

            _virtualButtons = ControllerButtons.None;
            frame = Compose();
        }

        _frames.Publish(frame);
    }

    /// <summary>Feed a key press. Returns true if it was bound and consumed, so the caller can mark the event handled.</summary>
    public bool KeyDown(int virtualKey)
    {
        bool changed;
        bool bound;
        ControllerStateFrame frame;
        lock (_gate)
        {
            if (!_bindings.KeyboardEnabled)
            {
                return false;
            }

            // Read under the lock: Bindings can be replaced from the settings page while keys are being pressed.
            bound = _bindings.Keyboard.ContainsKey(virtualKey);
            changed = _keyboard.KeyDown(virtualKey);
            frame = Compose();
        }

        if (changed)
        {
            _frames.Publish(frame);
        }

        // Report consumption even for a repeat, so auto-repeat of a bound key is not also handled as a shortcut.
        return bound;
    }

    /// <summary>Feed a key release.</summary>
    public bool KeyUp(int virtualKey)
    {
        bool changed;
        ControllerStateFrame frame;
        lock (_gate)
        {
            changed = _keyboard.KeyUp(virtualKey);
            frame = Compose();
        }

        if (changed)
        {
            _frames.Publish(frame);
        }

        return changed;
    }

    /// <summary>
    /// Release every key. Must be called on focus loss: the OS stops delivering key-up to a deactivated window,
    /// so a key held at the moment of alt-tab would otherwise stay held indefinitely — full stick deflection into
    /// the game with no way for the user to correct it.
    /// </summary>
    public void ReleaseAllKeys()
    {
        ControllerStateFrame frame;
        lock (_gate)
        {
            _keyboard.Clear();
            frame = Compose();
        }

        _frames.Publish(frame);
    }

    private void OnPadFrame(ControllerStateFrame frame)
    {
        ControllerStateFrame composed;
        lock (_gate)
        {
            _lastPad = ControllerInputMerger.ApplyRemap(frame, _bindings.GamepadRemap);
            _havePad = true;
            composed = Compose();
        }

        _frames.Publish(composed);
    }

    /// <summary>Caller must hold the lock.</summary>
    private ControllerStateFrame Compose()
    {
        ControllerStateFrame keys = _keyboard.ToFrame(DateTime.UtcNow.Ticks);
        ControllerStateFrame merged = _havePad ? ControllerInputMerger.Merge(_lastPad, keys) : keys;

        // On-screen buttons are additive to whatever the pad and keyboard are doing, so tapping PS while holding a
        // stick direction sends both. The rule lives in Core so it can be tested.
        return ControllerInputMerger.WithVirtualButtons(merged, _virtualButtons);
    }

    public void Dispose() => _padSubscription?.Dispose();
}
