namespace Ripcord.Core.Input;

/// <summary>What kind of surface is claiming the pad.</summary>
public enum InputScopeKind
{
    /// <summary>App furniture: the console list, settings, about. Directional focus and activation.</summary>
    Chrome,

    /// <summary>A running stream. Frames are forwarded to the console rather than steering the interface.</summary>
    Session,

    /// <summary>
    /// Something over the top of whatever is underneath — a dialog, a keyboard overlay. Covers a Session, which
    /// is the case that used to have no representation at all.
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
/// <b>What this replaces.</b> Ownership used to be a poll: the window asked, on every frame, whether the page
/// currently in the stream layer had a bool set. That had three problems, and the third is the one people felt.
/// It forced the window to know the page's type. It derived one object's state from a third object's status.
/// And <em>it was true during modals</em> — so opening a dialog mid-stream left the session still claiming the
/// pad, and a controller-only user could not reach the dialog they had just opened. On a handheld with no
/// keyboard that is a dead end with no way out but killing the process.
/// </para>
///
/// <para>
/// Push and pop are <b>edges</b>, raised by whoever changed the state, rather than a condition re-evaluated by
/// an observer. "Modal covers Session" stops being two objects agreeing and becomes the shape of the data.
/// </para>
/// </summary>
public sealed class InputScopeStack
{
    private readonly List<IInputScope> _scopes = [];

    /// <summary>Whoever owns the pad right now, or null when nothing has claimed it.</summary>
    public IInputScope? Top => _scopes.Count > 0 ? _scopes[^1] : null;

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
