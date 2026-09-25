using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Ripcord.Presentation.Accounts;
using Ripcord_App.Dialogs;
using Ripcord_App.Input;

namespace Ripcord_App.Services;

/// <summary>
/// Runs the account sign-in, wherever the user asked for it.
///
/// <para>
/// <b>Why this is not a method on the settings page any more.</b> It was, and that is the only reason the
/// pairing flow's sign-in invitation navigated to Settings to use it — an implementation detail that became a
/// journey. Signing in halfway through pairing then left the user on a settings page with the pairing flow
/// abandoned behind them, and the way back was to start pairing again from the beginning. Sign-in is a modal
/// over whatever surface asked for it; it never needed to be somewhere.
/// </para>
/// </summary>
public static class AccountSignIn
{
    /// <summary>
    /// Show the sign-in web view, complete the exchange, and load the account's console list.
    ///
    /// <para>
    /// The exchange happens <em>after</em> the dialog has closed, deliberately. Awaiting a network call while a
    /// modal is still up means the dialog owns the failure, and a dialog that has to render an error is a
    /// dialog that has to stay open — which is how the user ends up looking at a spent authorization code.
    /// </para>
    /// </summary>
    /// <param name="account">A view-model over the shared session; every caller may hold its own.</param>
    /// <param name="xamlRoot">The root to host the dialog in — the calling page's.</param>
    public static async Task<AccountSignInResult> RunAsync(AccountViewModel account, XamlRoot? xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(account);

        var dialog = new AccountSignInDialog(account) { XamlRoot = xamlRoot };

        try
        {
            // Through ModalHost, never ContentDialog.ShowAsync directly: the dialog needs a Modal input scope
            // pushed for it, or the surface underneath keeps the pad and a controller-only user is left
            // looking at a sign-in page they cannot reach. ModalHostTests enforces this.
            await ModalHost.ShowAsync(dialog);
        }
        catch (Exception ex)
        {
            // Another dialog already up, most likely. Nothing has been started, so there is nothing to undo.
            Debug.WriteLine($"[Ripcord] sign-in dialog failed to show: {ex}");
            return AccountSignInResult.Cancelled;
        }

        if (dialog.Failure is { } failure)
        {
            return AccountSignInResult.Failed(failure);
        }

        if (dialog.CompletedRedirect is not { } redirect)
        {
            // Cancelled. Not worth reporting: the user closed a window they opened.
            return AccountSignInResult.Cancelled;
        }

        await account.CompleteSignInAsync(redirect);

        if (account.State.Step != AccountStep.SignedIn)
        {
            // The view-model has already put the reason in its own state, which the settings page renders. A
            // caller with nowhere to show one gets a plain "it did not happen".
            return AccountSignInResult.Cancelled;
        }

        await account.LoadConsolesAsync();

        return AccountSignInResult.Succeeded;
    }
}

/// <summary>
/// What came of a sign-in attempt. <c>Failure</c> is a sentence to show the user and is set only when the
/// sign-in surface itself could not run — a missing web view runtime, say. A cancelled sign-in is neither
/// signed in nor a failure, because nothing went wrong.
/// </summary>
public readonly record struct AccountSignInResult(bool SignedIn, string? Failure)
{
    /// <summary>The user closed the sign-in page, or it ended without an identity and said why itself.</summary>
    public static AccountSignInResult Cancelled => new(false, null);

    /// <summary>Signed in, and the account's console list has been loaded.</summary>
    public static AccountSignInResult Succeeded => new(true, null);

    /// <summary>The sign-in surface could not run, with a sentence saying so.</summary>
    public static AccountSignInResult Failed(string message) => new(false, message);
}
