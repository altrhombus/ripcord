using System;
using Ripcord.Core.Input;

namespace Ripcord_App.Input;

/// <summary>
/// A surface's claim on the controller, as this front end expresses it.
///
/// <para>
/// The portable arbiter only needs <see cref="IInputScope.Kind"/> and the two edges. What a WinUI surface adds
/// is what to do on them — a stream stops forwarding and releases held buttons, a page re-seeds focus. Passing
/// those as callbacks rather than subclassing keeps the claim a value the surface owns, so pushing and popping
/// stay ordinary lifecycle statements next to the rest of its set-up.
/// </para>
/// </summary>
public sealed class ShellInputScope(
    InputScopeKind kind,
    Action? onActivated = null,
    Action? onDeactivated = null) : IInputScope
{
    public InputScopeKind Kind { get; } = kind;

    public void OnActivated() => onActivated?.Invoke();

    public void OnDeactivated() => onDeactivated?.Invoke();
}
