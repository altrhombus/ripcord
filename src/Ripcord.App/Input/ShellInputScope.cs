using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ripcord.Core.Input;

namespace Ripcord_App.Input;

/// <summary>
/// A surface's claim on the controller, as this front end expresses it.
///
/// <para>
/// The portable arbiter only needs <see cref="IInputScope.Kind"/> and the two edges. What a WinUI surface adds
/// is what to do on them — a stream stops forwarding and releases held buttons, a page re-seeds focus. They
/// are callbacks rather than subclass overrides, so the claim stays a value the surface owns.
/// </para>
///
/// <para>
/// <b>Focus is remembered across a covering scope</b>, which is why <paramref name="focusRoot"/> exists. Open a
/// dialog over a page and close it again and WinUI restores nothing: the caret comes back at the top of the
/// page, or nowhere. The stack knows the exact moments a surface stops and starts owning the pad, so focus is
/// saved and restored on those edges, for every surface.
/// </para>
///
/// <para>
/// Restoring takes precedence over <paramref name="onActivated"/>, and that ordering is the contract: a scope's
/// activation callback is what to do when there is <em>nothing to come back to</em>. Otherwise the two would
/// race on every dialog dismissal, with the seed usually winning.
/// </para>
/// </summary>
public sealed class ShellInputScope(
    InputScopeKind kind,
    Action? onActivated = null,
    Action? onDeactivated = null,
    Func<XamlRoot?>? focusRoot = null,
    IReadOnlyList<InputPrompt>? prompts = null,
    bool readsPad = false) : IInputScope
{
    /// <summary>
    /// Weak on purpose. A scope can outlive the page it belongs to during a fast exit, and a strong reference
    /// here would hold that page's whole visual tree alive for as long as the scope existed.
    /// </summary>
    private WeakReference<Control>? _remembered;

    public InputScopeKind Kind { get; } = kind;

    /// <summary>What the hint bar shows while this scope is on top. The kind's default unless told otherwise.</summary>
    public IReadOnlyList<InputPrompt> Prompts { get; } = prompts ?? ButtonLabels.DefaultFor(kind);

    /// <inheritdoc />
    public bool ReadsPad { get; } = readsPad;

    public void OnActivated()
    {
        if (TryRestoreFocus())
        {
            return;
        }

        onActivated?.Invoke();
    }

    public void OnDeactivated()
    {
        Remember();
        onDeactivated?.Invoke();
    }

    private void Remember()
    {
        _remembered = null;

        if (focusRoot?.Invoke() is not { } root)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(root) is Control focused)
        {
            _remembered = new WeakReference<Control>(focused);
        }
    }

    private bool TryRestoreFocus()
    {
        if (_remembered is null || !_remembered.TryGetTarget(out Control? target))
        {
            return false;
        }

        _remembered = null;

        // Everything that could have happened to it while it was covered. A control that has been collapsed,
        // disabled or detached from the tree is not somewhere focus can go, and asking anyway either throws or
        // succeeds invisibly — both worse than falling through to the scope's own seeding.
        if (target.XamlRoot is null
            || !target.IsLoaded
            || !target.IsEnabled
            || target.Visibility != Visibility.Visible)
        {
            return false;
        }

        // FocusState.Keyboard, never Programmatic: Programmatic draws no focus visual, so a restored caret
        // would be invisible and the user would be back to guessing where they are.
        return target.Focus(FocusState.Keyboard);
    }
}
