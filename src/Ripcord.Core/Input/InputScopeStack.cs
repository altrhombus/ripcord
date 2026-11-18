namespace Ripcord.Core.Input;

/// <summary>What kind of surface is claiming the pad.</summary>
public enum InputScopeKind
{
    /// <summary>App furniture: the console list, settings, about. Directional focus and activation.</summary>
    Chrome,

    /// <summary>A running stream. Frames are forwarded to the console rather than steering the interface.</summary>
    Session,

    /// <summary>
    /// Something over the top of whatever is underneath — a dialog, a keyboard overlay. It can cover a Session
    /// as well as a page.
    /// </summary>
    Modal,
}

/// <summary>
/// One claim on the controller. Implementations add whatever their front end needs — a focus root, prompts —
/// but the arbiter only ever sees this.
/// </summary>
public interface IInputScope
{
    InputScopeKind Kind { get; }

    /// <summary>
    /// What this surface offers the user right now, for the hint bar to draw.
    ///
    /// <para>
    /// Declared on the claim itself rather than pushed to the bar by each page, so "who owns the pad" and
    /// "what does the pad do" are one object and cannot drift apart.
    /// </para>
    ///
    /// <para>
    /// <b>The Enter/Escape contract, shared by all three inputs.</b> The South button, Enter, and a primary
    /// click all mean <em>activate what has focus</em>. The East button and Escape both mean <em>undo the
    /// last thing that took over the screen</em> — dismiss a popup if one is up, else leave the current page,
    /// else nothing. Neither ever means "commit a form", which is why no surface in this app has a default
    /// button that a mis-timed press can fire.
    /// </para>
    /// </summary>
    IReadOnlyList<InputPrompt> Prompts => ButtonLabels.DefaultFor(Kind);

    /// <summary>
    /// True when the surface reads the pad's buttons itself, so they must not also move focus. The login
    /// passcode prompt is the case: its digits are the d-pad and shoulders, which would otherwise navigate.
    /// </summary>
    bool ReadsPad => false;

    /// <summary>
    /// This scope now owns the pad. Raised on push, and again when a scope above it is popped.
    /// </summary>
    void OnActivated();

    /// <summary>
    /// This scope no longer owns the pad. Raised when something is pushed over it, and when it is popped.
    ///
    /// <para>
    /// A Session scope uses this edge to stop forwarding and release whatever is physically held — which is
    /// what stops the buttons that opened a dialog staying down inside the game underneath.
    /// </para>
    /// </summary>
    void OnDeactivated();
}

/// <summary>
/// Decides who owns the controller, as a stack.
///
/// <para>
/// Push and pop are <b>edges</b>, raised by whoever changed the state, rather than a condition re-evaluated by
/// an observer. A modal pushed over a session therefore takes the pad from it: ownership polled from the
/// stream page would stay true during modals, so a controller-only user could not reach a dialog opened
/// mid-stream. The window also needs no knowledge of the page's type.
/// </para>
/// </summary>
public sealed class InputScopeStack
{
    private readonly List<IInputScope> _scopes = [];

    /// <summary>Whoever owns the pad right now, or null when nothing has claimed it.</summary>
    public IInputScope? Top => Volatile.Read(ref _top);

    // The top, published whole after every change. Push and Pop run on the UI thread, but Top is read from the
    // pad's input threads too (InputRouter, the passcode prompt), and reading the list there would check its count
    // and then index it: a pop in between throws on a thread-pool timer, which ends the process.
    private IInputScope? _top;

    /// <summary>How many claims are outstanding. Exposed for assertions and diagnostics.</summary>
    public int Count => _scopes.Count;

    /// <summary>Raised after any change, with the new top, so a hint bar can redraw once per edge.</summary>
    public event Action<IInputScope?>? TopChanged;

    /// <summary>
    /// Claim the pad. The previous top is deactivated first, so at no point do two scopes both believe they own
    /// it — which matters because a Session's deactivate releases held buttons into the console.
    /// </summary>
    public void Push(IInputScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (_scopes.Contains(scope))
        {
            throw new InvalidOperationException(
                "That scope is already on the stack. Pushing twice would make the matching pop ambiguous, and "
                + "a surface that pushes twice has a lifecycle bug worth failing on rather than absorbing.");
        }

        IInputScope? previous = Top;
        _scopes.Add(scope);
        Volatile.Write(ref _top, scope);

        previous?.OnDeactivated();
        scope.OnActivated();
        TopChanged?.Invoke(scope);
    }

    /// <summary>
    /// Release a claim.
    ///
    /// <para>
    /// Takes the scope rather than popping blindly, and tolerates it not being on top. Page teardown order is
    /// not something a surface controls — a dialog can outlive the page that opened it during a fast exit — and
    /// blind popping in that situation removes the wrong claim, which is far worse than removing none.
    /// </para>
    /// </summary>
    /// <returns>True if the scope was on the stack.</returns>
    public bool Pop(IInputScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        int index = _scopes.LastIndexOf(scope);
        if (index < 0)
        {
            return false;
        }

        bool wasTop = index == _scopes.Count - 1;
        _scopes.RemoveAt(index);
        Volatile.Write(ref _top, _scopes.Count > 0 ? _scopes[^1] : null);

        if (!wasTop)
        {
            // Removed from underneath: whoever is on top never stopped owning the pad, so no edges fire and
            // nothing observable changes.
            return true;
        }

        scope.OnDeactivated();
        Top?.OnActivated();
        TopChanged?.Invoke(Top);
        return true;
    }

    /// <summary>True when the top scope is of this kind. The question most callers actually have.</summary>
    public bool IsActive(InputScopeKind kind) => Top?.Kind == kind;
}
