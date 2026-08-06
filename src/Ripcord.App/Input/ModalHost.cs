using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ripcord.Core.Input;

namespace Ripcord_App.Input;

/// <summary>
/// The one way a dialog gets shown. Wraps <see cref="ContentDialog.ShowAsync()"/> so that everything modality
/// implies happens whether or not the caller remembered it.
///
/// <para>
/// <b>Why this is central rather than a rule people follow.</b> Seven call sites showed a dialog; exactly one
/// of them pushed a Modal input scope. That one was written because someone hit the bug it fixes — opening a
/// prompt mid-stream while the session still owned the pad, so the controller could not reach the dialog it
/// had just raised. Nothing distinguished it from the other six except which one had been found. Pushing the
/// scope in the helper means "a dialog owns the pad while it is up" stops being something to remember.
/// </para>
///
/// <para>
/// <b>What the scope buys, restated because it is not obvious from the push.</b> Whatever is underneath gets a
/// deactivation edge: a session suspends forwarding and pushes one neutral frame, so the buttons that were
/// held when the dialog opened are released inside the game rather than staying down. And it remembers its
/// focused element, so dismissing the dialog puts the caret back where it was instead of at the top of the
/// page. Both fall out of the push; neither is written here.
/// </para>
///
/// <para>
/// <b>Keyboard gets the same trap.</b> <c>Cycle</c> is the native half — Tab wraps inside the dialog rather
/// than walking out into the page behind it — and <c>XYFocusKeyboardNavigation</c> gives arrow keys the same
/// directional model the pad uses, so the two input methods cannot disagree about where Down goes.
/// </para>
/// </summary>
public static class ModalHost
{
    /// <summary>
    /// Show <paramref name="dialog"/> as a modal. The caller sets <c>XamlRoot</c>, since only it knows which
    /// tree the dialog belongs to.
    /// </summary>
    /// <remarks>
    /// Does not catch. Callers already have their own handling for the two real failures — a torn-down
    /// XamlRoot, and WinUI's one-dialog-at-a-time rule — and what they do about them differs: the disconnect
    /// prompt must fall through and leave the session anyway, while a rename must abandon quietly. Swallowing
    /// here would take that decision away from them. The scope is popped on every path regardless.
    /// </remarks>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        if (dialog.XamlRoot is null)
        {
            // Showing without one throws, and during teardown that is a routine race rather than a bug worth
            // crashing for. "None" is what a dismissed dialog returns, which is the correct reading: it never
            // appeared, so the user chose nothing.
            return ContentDialogResult.None;
        }

        dialog.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        dialog.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

        // focusRoot so a modal remembers its own focus too — for a soft keyboard opening over a dialog, which
        // is the next thing to land on top of one.
        var scope = new ShellInputScope(InputScopeKind.Modal, focusRoot: () => dialog.XamlRoot);
        App.Input.Scopes.Push(scope);

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            App.Input.Scopes.Pop(scope);
        }
    }
}
