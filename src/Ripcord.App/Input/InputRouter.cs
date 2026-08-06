using System;
using Ripcord.Core.Input;
using Ripcord.Core.Reactive;
using Ripcord.Core.Settings;
using Ripcord.Input;

namespace Ripcord_App.Input;

/// <summary>
/// The app's single reader of the controller, and the arbiter of who gets it.
///
/// <para>
/// <b>What this replaces: two sources for one pad.</b> The window ran its own
/// <c>GameInputControllerSource</c> for menu navigation while the session page ran a full
/// <c>ControllerSourceFactory.Create()</c> composite for forwarding. Two consequences, one of them a plain
/// bug. The composite includes the DualSense raw-HID path, so a DualSense worked in a stream and did not work
/// in the menus — the pad most likely to be attached was the one the interface ignored. And the same physical
/// device was being read through two APIs at once, which the factory's own comment warns against.
/// </para>
///
/// <para>
/// <b>The remap is applied exactly once, here.</b> This is load-bearing and easy to undo by accident:
/// <c>MergedInputSource</c> also knows how to apply it, and a remap is <em>not</em> idempotent — running it
/// twice swaps a button and then swaps it back, or worse, chains A→B→C. So the session's
/// <c>MergedInputSource</c> must be constructed with an <b>empty</b> remap. If a bound button ever starts
/// behaving as though it were unbound, this is the first place to look.
/// </para>
///
/// <para>
/// <b>Cost, stated honestly.</b> The DualSense HID source now polls for its device for the life of the app
/// rather than only during a session — one idle thread at 2 Hz. That is what buys DualSense buttons on chrome
/// pages, and it is the intended trade. If it ever shows up on a handheld battery trace the lever is
/// <see cref="Stop"/>/<see cref="Start"/> on window activation, not going back to two sources.
/// </para>
/// </summary>
public sealed class InputRouter : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly NavIntentReader _navIntents = new();

    /// <summary>
    /// Which input method is driving. Fed by the shell rather than from <see cref="OnFrame"/>, deliberately:
    /// frames arrive on a polling thread, and every consumer of the mode is a piece of interface that lives on
    /// the UI thread. Reporting from the shell keeps the tracker single-threaded and means <c>ModeChanged</c>
    /// is raised where a surface can act on it directly.
    ///
    /// <para>
    /// Nothing is lost by not reading raw frames: the tracker's contract is <em>deliberate</em> activity, and a
    /// navigation intent is produced on exactly that — a button edge, or a stick past its deadzone.
    /// </para>
    /// </summary>
    private readonly InputModeTracker _modes = new();

    private IControllerSource? _source;
    private IDisposable? _stateSubscription;
    private IDisposable? _connectionSubscription;

    public InputRouter(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _navIntents.StickDeadzone = settings.Current.UiStickDeadzone;
    }

    /// <summary>Who owns the pad. Surfaces push and pop; the router only reads the top.</summary>
    public InputScopeStack Scopes { get; } = new();

    /// <summary>
    /// Every attached pad, as the composite sees them. Exposed so the diagnostics overlay can report which
    /// engines and devices are live without opening a second source of its own.
    /// </summary>
    public IObservable<ControllerConnectionEvent>? Connections => _source?.Connections;

    /// <summary>The engines running, for the diagnostics readout.</summary>
    public string SourceName => _source?.SourceName ?? "none";

    /// <summary>Raw frames, after the remap, for whoever the top scope is.</summary>
    public event Action<ControllerStateFrame>? FrameReceived;

    /// <summary>Navigation intents, raised only while a non-Session scope owns the pad.</summary>
    public event Action<NavIntent>? IntentReceived;

    /// <summary>
    /// Begin reading.
    ///
    /// <para>
    /// <b>Must stay behind window realization.</b> Building a GameInput-backed WinRT component before the
    /// window content exists was implicated in a native <c>combase</c> <c>E_UNEXPECTED</c> crash on the first
    /// D-pad press. This is deferred construction on purpose, not laziness.
    /// </para>
    /// </summary>
    public void Start()
    {
        if (_source is not null)
        {
            return;
        }

        _source = ControllerSourceFactory.Create();
        _stateSubscription = _source.StateChanges(string.Empty)
            .Subscribe(new AnonymousObserver<ControllerStateFrame>(OnFrame));
    }

    public void Stop()
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;
        _connectionSubscription?.Dispose();
        _connectionSubscription = null;

        (_source as IDisposable)?.Dispose();
        _source = null;
    }

    /// <summary>How the person is driving the app right now.</summary>
    public InputMode Mode => _modes.Mode;

    /// <summary>Raised on the thread that reported the activity — in practice the UI thread. See <see cref="_modes"/>.</summary>
    public event Action<InputMode>? ModeChanged
    {
        add => _modes.ModeChanged += value;
        remove => _modes.ModeChanged -= value;
    }

    /// <summary>The pad did something deliberate. Called by the shell when it acts on an intent.</summary>
    public void ReportControllerActivity() => _modes.ReportControllerActivity(DateTimeOffset.UtcNow);

    /// <summary>A key was pressed.</summary>
    public void ReportKeyboardActivity() => _modes.ReportKeyboardActivity(DateTimeOffset.UtcNow);

    /// <summary>A pointer button went down or the wheel turned — never mere movement.</summary>
    public void ReportPointerActivity() => _modes.ReportPointerActivity(DateTimeOffset.UtcNow);

    /// <summary>A touch contact began.</summary>
    public void ReportTouchActivity() => _modes.ReportTouchActivity(DateTimeOffset.UtcNow);

    /// <summary>The deadzone is a user setting and can change while the app runs.</summary>
    public void UseDeadzone(double deadzone) => _navIntents.StickDeadzone = deadzone;

    private void OnFrame(ControllerStateFrame raw)
    {
        // Once, here. See the class note — MergedInputSource must therefore be given an empty remap.
        ControllerStateFrame frame =
            ControllerInputMerger.ApplyRemap(raw, _settings.Current.InputBindings.GamepadRemap);

        FrameReceived?.Invoke(frame);

        // A stream owns the pad outright: its frames go to the console, and menu navigation must not also
        // consume them or B would leave the stream instead of reaching the game. Reset rather than ignore, so
        // the buttons held at the moment the stream took over do not read as a fresh press on the way back.
        if (Scopes.IsActive(InputScopeKind.Session))
        {
            _navIntents.Reset(frame);
            return;
        }

        NavIntent intent = _navIntents.Read(frame, DateTimeOffset.UtcNow);
        if (!intent.IsEmpty)
        {
            IntentReceived?.Invoke(intent);
        }
    }

    public void Dispose() => Stop();
}
