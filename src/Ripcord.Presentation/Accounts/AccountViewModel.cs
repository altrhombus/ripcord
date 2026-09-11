using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Accounts;

/// <summary>
/// The account surface: sign in, stay signed in, sign out, and list the account's consoles.
///
/// <para>
/// One view-model shared by every surface that cares who is signed in — the settings page shows it, the pairing
/// flow reads its account id, and the console picker merges its console list. That sharing is the point: an
/// account id fetched twice by two surfaces is two chances to disagree about who is signed in, which is the class
/// of bug the single-graph rule exists to prevent.
/// </para>
///
/// <para>
/// <b>Freshness is decided synchronously.</b> Every asynchronous entry point here captures its own generation
/// before awaiting and compares it after, on the calling continuation, never inside a posted <c>Mutate</c>
/// closure — the layer's second rule, and the one this repo has broken twice. A sign-out that lands while a
/// restore is in flight must not have the restore's result overwrite it a moment later.
/// </para>
/// </summary>
public sealed class AccountViewModel : ObservableState<AccountViewState>
{
    private readonly IAccountSession _session;

    /// <summary>
    /// The paired-console store, so that loading the account's console list can repair stored consoles that
    /// predate the cloud id. Optional: a surface that only wants to show who is signed in supplies nothing and
    /// the repair simply does not happen.
    /// </summary>
    private readonly IPairedConsoleStore? _consoles;

    private AccountIdentity? _identity;
    private bool _isBusy;
    private string? _error;
    private IReadOnlyList<CloudConsole> _cloudConsoles = [];
    private bool _consolesLoaded;

    /// <summary>
    /// Bumped by every operation that replaces the session, so a result arriving from a superseded one is
    /// discarded rather than applied. A counter rather than a CancellationTokenSource because the operations are
    /// short and the question is "is this answer still wanted", not "stop doing that".
    /// </summary>
    private int _generation;

    public AccountViewModel(
        IAccountSession session,
        IUiDispatcher dispatcher,
        IPairedConsoleStore? consoles = null)
        : base(dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _consoles = consoles;
        _identity = session.Current;
    }

    /// <summary>
    /// Raised on the dispatcher whenever the signed-in account changes, including to null on sign-out. The
    /// pairing flow listens so its account id follows a sign-in that happens while it is open.
    /// </summary>
    public event Action<AccountIdentity?>? IdentityChanged;

    /// <summary>The signed-in account's id, or empty. The value pairing uses instead of asking the user.</summary>
    public string AccountId => _identity?.AccountId ?? string.Empty;

    /// <summary>
    /// Restore a stored session at startup. Safe to call when nothing is stored or when the build has no
    /// credential — both are ordinary and return without touching the network.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.CanSignIn || !_session.HasStoredSession)
        {
            return;
        }

        int generation = BeginWork();

        try
        {
            AccountIdentity? identity = await _session.RestoreAsync(cancellationToken).ConfigureAwait(false);

            // Judged here, synchronously, before any Mutate — see the class remarks.
            if (generation != _generation)
            {
                return;
            }

            Settle(identity, error: null);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                Settle(_identity, error: null);
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                // A failed restore is not a failed sign-in: the user did nothing and there is nothing for them
                // to fix, so it reports as signed out with the reason available rather than as an error state.
                Settle(null, string.Format(Strings.Account_RestoreFailed, ex.Message));
            }
        }
    }

    /// <summary>
    /// The URL the front end should open. Returns null when sign-in is unavailable, so a caller does not have to
    /// pre-check and then handle the throw as well.
    /// </summary>
    public string? BeginSignIn()
    {
        if (!_session.CanSignIn)
        {
            return null;
        }

        // The generation bump matters here: it abandons any restore still in flight, so its result cannot land
        // on top of the sign-in the user has just started.
        BeginWork();
        return _session.BeginSignIn();
    }

    /// <summary>Whether a URL the front end navigated to carries the sign-in result.</summary>
    public bool IsCompletionRedirect(Uri navigated)
        => _session.CanSignIn && _session.IsCompletionRedirect(navigated);

    /// <summary>
    /// Finish sign-in with the redirect the front end caught. Reports failure through the state rather than
    /// throwing: the caller is a web-view event handler, and an exception escaping one goes to the message loop.
    /// </summary>
    public async Task CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirected);

        int generation = _generation;
        Mutate(() =>
        {
            _isBusy = true;
            _error = null;
        });

        try
        {
            AccountIdentity identity = await _session
                .CompleteSignInAsync(redirected, cancellationToken)
                .ConfigureAwait(false);

            if (generation != _generation)
            {
                return;
            }

            Settle(identity, error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _generation)
            {
                Settle(null, string.Format(Strings.Account_SignInIncomplete, ex.Message));
            }
        }
    }

    /// <summary>
    /// Load the account's consoles. A failure leaves whatever was already loaded in place and reports the reason:
    /// a transient cloud error should not empty a list the user is looking at.
    /// </summary>
    public async Task LoadConsolesAsync(CancellationToken cancellationToken = default)
    {
        if (_identity is null)
        {
            return;
        }

        int generation = _generation;

        try
        {
            IReadOnlyList<CloudConsole> consoles = await _session
                .ListConsolesAsync(cancellationToken)
                .ConfigureAwait(false);

            if (generation != _generation)
            {
                return;
            }

            // Before publishing: this is the only moment the app holds the account's console list and the
            // stored pairings at the same time, which is what the match needs.
            RepairStoredConsoles(consoles);

            Mutate(() =>
            {
                _cloudConsoles = consoles;
                _consolesLoaded = true;
                _error = null;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _generation)
            {
                Mutate(() => _error = string.Format(Strings.Account_LoadConsolesFailed, ex.Message));
            }
        }
    }

    /// <summary>Sign out, destroying the stored credential.</summary>
    public void SignOut()
    {
        BeginWork();
        _session.SignOut();
        Settle(null, error: null);
    }

    /// <summary>
    /// Stamp the account service's id onto stored consoles paired before that id was recorded, so remote wake
    /// works for them without the user re-pairing.
    ///
    /// <para>
    /// Never throws outward. A store that cannot be written is a real problem, but not one worth failing a
    /// console list over — the only thing lost is a remote wake, and the repair runs again on the next load.
    /// </para>
    /// </summary>
    private void RepairStoredConsoles(IReadOnlyList<CloudConsole> cloudConsoles)
    {
        if (_consoles is null)
        {
            return;
        }

        try
        {
            CloudConsoleMatch.Backfill(_consoles, cloudConsoles);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Take ownership of the surface's state for a new operation, abandoning any in flight.</summary>
    private int BeginWork()
    {
        int generation = ++_generation;
        Mutate(() =>
        {
            _isBusy = true;
            _error = null;
        });
        return generation;
    }

    /// <summary>
    /// Land on a final identity. One place so that clearing the console list on identity change cannot be
    /// forgotten — showing the previous account's consoles to the next one would be a genuine leak.
    /// </summary>
    private void Settle(AccountIdentity? identity, string? error)
    {
        bool changed = !Equals(_identity?.AccountId, identity?.AccountId);

        Mutate(() =>
        {
            _identity = identity;
            _isBusy = false;
            _error = error;

            if (changed)
            {
                _cloudConsoles = [];
                _consolesLoaded = false;
            }
        });

        if (changed)
        {
            IdentityChanged?.Invoke(identity);
        }
    }

    protected override AccountViewState Compose()
    {
        AccountStep step = !_session.CanSignIn ? AccountStep.Unavailable
            : _isBusy ? AccountStep.Working
            : _identity is not null ? AccountStep.SignedIn
            : AccountStep.SignedOut;

        (string heading, string detail, StatusTone tone) = step switch
        {
            AccountStep.Unavailable => (
                Strings.Account_SignInUnavailableTitle,
                Strings.Account_SignInUnavailableBody,
                StatusTone.Neutral),

            AccountStep.Working => (
                Strings.Account_SigningIn,
                Strings.Account_CheckingWithPsn,
                StatusTone.Unknown),

            AccountStep.SignedIn => (
                _identity!.DisplayName.Length > 0 ? _identity.DisplayName : Strings.Account_SignedIn,
                _identity.Region.Length > 0
                    ? string.Format(Strings.Account_SignedInWithRegion, _identity.Region)
                    : Strings.Account_SignedInNoRegion,
                StatusTone.Positive),

            _ => (
                Strings.Account_NotSignedIn,
                Strings.Account_SignInPitch,
                StatusTone.Neutral),
        };

        return new AccountViewState(
            Step: step,
            Heading: heading,
            Detail: detail,
            Tone: tone,
            CanSignIn: _session.CanSignIn && _identity is null && !_isBusy,
            CanSignOut: _identity is not null && !_isBusy,
            IsBusy: _isBusy,
            Error: _error,
            AccountName: _identity?.DisplayName ?? string.Empty,
            AccountId: _identity?.AccountId ?? string.Empty,
            Consoles: _cloudConsoles,
            ConsolesLoaded: _consolesLoaded);
    }
}
