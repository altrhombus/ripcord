using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Ripcord_App.Input;
using Ripcord.Presentation.Accounts;

namespace Ripcord_App.Dialogs;

/// <summary>
/// Hosts the account web sign-in and catches the authorization code out of the redirect.
///
/// <para>
/// <b>Why an embedded web view rather than the system browser.</b> The OAuth redirect goes to a URL on Sony's
/// own domain, not to a loopback address this process could listen on, so there is no way to receive the code
/// out of band — something has to watch the navigation. A web view we own can do that; the user's default
/// browser cannot hand it back.
/// </para>
///
/// <para>
/// <b>The window this is the "device" the portable layer refuses to name.</b> Everything decided here is
/// mechanical — navigate, watch, close. Which URL to open and what the code means belong to
/// <see cref="IAccountSession"/>, and this dialog asks it both questions rather than knowing either.
/// </para>
///
/// <para>
/// The dialog closes itself the moment the redirect appears, so the user never sees the blank page it lands on.
/// </para>
/// </summary>
public sealed partial class AccountSignInDialog : ContentDialog
{
    private readonly AccountViewModel _account;

    /// <summary>
    /// Null unless <c>RIPCORD_TRACE_FOCUS=1</c>. This surface is where focus theft was found twice and
    /// mis-diagnosed twice, so it is the one place worth being able to switch a trace on for.
    /// </summary>
    private FocusTrace? _focusTrace;

    private bool _completed;

    public AccountSignInDialog(AccountViewModel account)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _focusTrace?.Dispose();
            _focusTrace = null;
        };
    }

    /// <summary>
    /// The redirect the flow completed on, or null if the user cancelled. The caller hands this to the
    /// view-model — this dialog deliberately does not complete the sign-in itself, so that a dialog which has
    /// already closed is never the thing awaiting a network call.
    /// </summary>
    public Uri? CompletedRedirect { get; private set; }

    /// <summary>Set when the web view could not start at all, so the caller can say why rather than "cancelled".</summary>
    public string? Failure { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Before the web view exists, so the trace covers the dialog opening as well as everything after it.
        _focusTrace = FocusTrace.StartIfEnabled("account sign-in");

        try
        {
            // Explicit rather than implicit-on-first-navigate, so a missing or broken WebView2 runtime surfaces
            // here as a sentence we can show instead of an empty dialog the user stares at.
            await Browser.EnsureCoreWebView2Async();

            string? url = _account.BeginSignIn();
            if (url is null)
            {
                Fail("Sign-in isn't available in this build.");
                return;
            }

            // A private, throwaway profile would be ideal so a sign-in never inherits a stale session; the
            // WebView2 profile API is per-environment rather than per-control, so instead the sign-in request
            // itself carries prompt=always, which makes the page re-authenticate regardless of what is cached.
            Browser.Source = new Uri(url);

            // Hand XAML focus to the web view and leave it there.
            //
            // A WebView2 is a native child HWND, so once its content has focus XAML focus reads as NOWHERE -
            // FocusManager.GetFocusedElement returns null. Anything that treats that as "focus needs
            // seeding" pulls focus back out, and the symptom is a password box that deselects the instant it
            // is clicked. The shell's own seeding is now barred while a modal is up; this is the other half,
            // so nothing in the dialog is holding focus for the platform to snap back to either.
            _ = Browser.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            // Most often a missing WebView2 runtime. Naming it is worth doing: it is installed with Windows on
            // any current build, so its absence is unusual enough that a user would otherwise have no idea.
            Debug.WriteLine($"[Ripcord] sign-in web view failed: {ex}");
            Fail("Couldn't open the sign-in page. Ripcord needs the Microsoft Edge WebView2 runtime, which is "
                 + "normally part of Windows.");
        }
    }

    /// <summary>
    /// Watches every navigation for the redirect carrying the code.
    ///
    /// <para>
    /// <c>NavigationStarting</c> rather than <c>NavigationCompleted</c>, and cancelled once matched: the
    /// redirect target is a page on Sony's site that has nothing to do with us, and letting it load means a
    /// visible flash of someone else's content and a pointless request.
    /// </para>
    /// </summary>
    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;

        if (_completed || !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        if (!_account.IsCompletionRedirect(uri))
        {
            return;
        }

        _completed = true;
        args.Cancel = true;
        CompletedRedirect = uri;

        Hide();
    }

    private void Fail(string message)
    {
        Failure = message;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = message;
    }
}
