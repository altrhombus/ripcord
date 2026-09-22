using System.Collections.ObjectModel;
using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Pairing;

/// <summary>
/// The add-a-console flow: pick a family, find the console on the network, enter the link code from its screen,
/// pair, and name it.
///
/// <para>
/// Extracted from <c>AddConsolePage</c>'s code-behind, where the same state machine ran on mutable fields and
/// could only be exercised by launching the app against real hardware. Three defects are fixed structurally
/// rather than guarded, because each was a consequence of living in a page:
/// </para>
///
/// <para>
/// <b>1. The scan was <c>async void</c>.</b> Nothing could await it, so a test had to sleep and hope, and an
/// exception escaping it would have gone to the message loop. Every transition here returns a Task.
/// </para>
///
/// <para>
/// <b>2. Discovery results could land after the user had left.</b> The page marshalled each result onto the
/// dispatcher and had nowhere to ask "is this scan still the current one?", so a console answering late mutated a
/// collection belonging to a dead page. There is now one generation check — <see cref="IsCurrentScan"/> — and one
/// rule about when to ask it: synchronously, as each event happens, never from inside a posted closure.
/// </para>
///
/// <para>
/// <b>3. Leaving mid-pairing leaked the request.</b> The page's <c>using var cts</c> inside its pair method was a
/// timeout, not an ownership handle, so navigating away left the exchange running and its result landing on a
/// page that no longer existed. The flow owns both cancellation sources and <see cref="DisposeAsync"/> cancels
/// them.
/// </para>
/// </summary>
public sealed class AddConsoleFlow : ObservableState<AddConsoleFlowState>, IAsyncDisposable
{
    private readonly IConsoleScanner _scanner;
    private readonly IConsoleRegistrar _registrar;
    private readonly IPairedConsoleStore _store;
    private readonly AddConsoleFlowOptions _options;

    /// <summary>
    /// The signed-in account, when there is one. Optional: pairing by hand is still a first-class path — a build
    /// with no OAuth credential has no other one — so this seam being absent must change nothing except who
    /// supplies the account id.
    /// </summary>
    private readonly IAccountSession? _account;

    /// <summary>
    /// Pairing through the account, when the graph composed one. Optional for the same reason
    /// <see cref="_account"/> is: a build with no credential has only the code route, and must behave exactly as
    /// it did before this existed.
    /// </summary>
    private readonly IAccountConsolePairing? _accountPairing;

    private AddConsoleStep _step = AddConsoleStep.Family;
    private ConsoleFamily _family = ConsoleFamily.Ps5;
    private string? _familyNote;
    private string _findSubheading = ScanningMessage;
    private bool _isScanning;
    private bool _manualEntryOpen;

    private DiscoveredConsoleCard? _selected;
    private string _host = string.Empty;
    private string _passcode = string.Empty;
    private string _typedAccountId = string.Empty;
    private string? _linkError;
    private PairedConsole? _paired;

    /// <summary>
    /// Whether this build can pair through an account, as answered once on arrival at the link step.
    ///
    /// <para>
    /// Cached rather than asked from <c>Compose</c>, which runs on every state change: the answer reads files,
    /// and the affordance has to be honest <em>before</em> the user commits to it — the same reasoning that puts
    /// the code route's availability check ahead of its pairing panel rather than inside it.
    /// </para>
    /// </summary>
    private AccountPairingAvailability? _accountPairingCapability;

    /// <summary>Which route the in-flight pairing is taking, so the progress text can say the right thing.</summary>
    private bool _accountRoute;

    /// <summary>
    /// The account id actually used for pairing: the signed-in account's when there is one, otherwise whatever
    /// was typed. Derived rather than stored so that signing in while this flow is open takes effect without the
    /// flow having to subscribe to anything — the console's own link step is where it is read, and by then the
    /// answer is current.
    /// </summary>
    private string EffectiveAccountId
        => AccountIdIsAutomatic ? _account!.Current!.AccountId : _typedAccountId;

    /// <summary>Whether the account id is coming from a signed-in account rather than from the user.</summary>
    private bool AccountIdIsAutomatic
        => _account?.Current is { AccountId.Length: > 0 };

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _pairCts;

    /// <summary>
    /// The account console-list lookup, owned separately from the scan that starts it.
    ///
    /// <para>
    /// It used to ride the scan's token, and picking a console cancels the scan — so a cloud that had not
    /// answered by the moment the user clicked never answered at all, and the console was saved with no cloud
    /// id. That was invisible while the id only bought a remote wake. It is not invisible now: whether the
    /// account already knows this console is what decides whether account pairing is offered, so a lookup
    /// cancelled by the user's own click would present as the route silently not being available.
    /// </para>
    /// </summary>
    private CancellationTokenSource? _cloudCts;

    /// <summary>
    /// The account's consoles as the cloud reports them, fetched alongside the local scan when signed in.
    ///
    /// <para>
    /// Not shown to the user — the local scan is what they pick from, because pairing needs an address and the
    /// cloud does not give one. This list exists to answer a single question at the moment a console is saved:
    /// what does the account service call this box? Learning that now is what makes a remote wake possible
    /// later, and now is the only convenient time to ask, because it is the one moment we have the console's
    /// local name and the account's list side by side.
    /// </para>
    /// </summary>
    private IReadOnlyList<CloudConsole> _cloudConsoles = [];

    private static string ScanningMessage => Strings.Pairing_ScanningHint;

    private static string NoAccountPairingMessage => Strings.Pairing_NoAccountPairing;

    /// <summary>
    /// Why a signed-in user still cannot take the account route. Says what to do about it, because the fix is
    /// on the console rather than in this app: the account only lists a console once it has been signed in to
    /// with that account.
    /// </summary>
    private static string NotInAccountListMessage => Strings.Pairing_NotInAccountList;

    /// <summary>
    /// How long past the search window to keep waiting for the scanner to say it has finished. Enough that a
    /// well-behaved scan's own completion normally wins the race, short enough that a misbehaving one is not
    /// noticeable. Results already stream in as they arrive, so nothing is lost either way.
    /// </summary>
    private static readonly TimeSpan ScanGrace = TimeSpan.FromSeconds(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AddConsoleFlow(
        IConsoleScanner scanner,
        IConsoleRegistrar registrar,
        IPairedConsoleStore store,
        IUiDispatcher dispatcher,
        AddConsoleFlowOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IAccountSession? account = null,
        IAccountConsolePairing? accountPairing = null)
        : base(dispatcher)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new AddConsoleFlowOptions();
        _delay = delay ?? Task.Delay;
        _account = account;
        _accountPairing = accountPairing;
    }

    /// <summary>
    /// Consoles found so far, in the order the flow wants them shown. An observable collection rather than a
    /// list inside the state record: results arrive one at a time, and replacing the whole collection on each
    /// arrival would rebuild the list and destroy focus — which matters because this list is navigated with a
    /// gamepad, and the user may be moving through it while more results land.
    /// </summary>
    public ObservableCollection<DiscoveredConsoleCard> Discovered { get; } = [];

    /// <summary>
    /// Raised once, on the dispatcher, when the flow ends with a saved console. Navigation stays with the front
    /// end — this layer has no opinion about frames or windows.
    /// </summary>
    public event Action<AddConsoleCompletion>? Completed;

    // ---- transitions ---------------------------------------------------------------------------

    /// <summary>
    /// Choose a family. An unsupported one surfaces its caveat and goes no further; a supported one starts the
    /// scan. A family with a caveat that <em>is</em> usable shows the caveat and proceeds — being honest about
    /// rough edges is not the same as refusing.
    /// </summary>
    public Task SelectFamilyAsync(ConsoleFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);

        Mutate(() =>
        {
            _family = family;
            _familyNote = family.SupportNote;
        });

        return family.IsSelectable ? EnterFindAsync() : Task.CompletedTask;
    }

    /// <summary>Search again from scratch, discarding what the previous scan found.</summary>
    public Task RescanAsync() => StartScanAsync();

    /// <summary>Reveal the type-an-address panel. Also opened automatically when a scan finds nothing.</summary>
    public void OpenManualEntry() => Mutate(() => _manualEntryOpen = true);

    /// <summary>
    /// Use a hand-typed address. The chosen family is kept: nothing has told us otherwise, unlike a console
    /// picked from the scan, which reports what it actually is.
    /// </summary>
    public void UseTypedAddress(string host)
    {
        string typed = (host ?? string.Empty).Trim();
        if (typed.Length == 0)
        {
            return;
        }

        CancelScan();

        // Resolved here rather than inside the closure: Mutate posts, so anything read in there is read later,
        // and this one reads files. Same rule the scan's freshness check follows.
        AccountPairingAvailability capability = ResolveAccountPairingCapability(_family);

        Mutate(() =>
        {
            _selected = null;
            _host = typed;
            _accountPairingCapability = capability;
            EnterLink();
        });
    }

    /// <summary>
    /// Pick a console from the scan. Everything it told us about itself comes along — family, name, host id and
    /// firmware — rather than only its address, and its own family wins over the one the user guessed.
    /// </summary>
    public void SelectDiscovered(DiscoveredConsoleCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        CancelScan();

        // The card's own family, not _family: the console reports what it actually is, and that assignment
        // happens inside the closure below — asking about the family the user guessed would answer for the
        // wrong console.
        AccountPairingAvailability capability = ResolveAccountPairingCapability(card.Family);

        Mutate(() =>
        {
            _selected = card;
            _family = card.Family;
            _host = card.Address;
            _accountPairingCapability = capability;
            EnterLink();
        });
    }

    /// <summary>
    /// Update the link-code and account fields, which together decide whether Pair is offered.
    ///
    /// <para>
    /// The account id is ignored while it is coming from a signed-in account. The front end hides the field in
    /// that case, but it still raises change events as the panel is built and torn down, and honouring those
    /// would let an empty text box overwrite a perfectly good account id — which presents as Pair silently
    /// refusing to enable, with nothing on screen to explain it.
    /// </para>
    /// </summary>
    public void SetLinkInput(string passcode, string accountId) => Mutate(() =>
    {
        _passcode = (passcode ?? string.Empty).Trim();

        if (!AccountIdIsAutomatic)
        {
            _typedAccountId = (accountId ?? string.Empty).Trim();
        }
    });

    /// <summary>
    /// Attempt the pairing exchange with the code from the console's screen.
    ///
    /// <para>
    /// Availability is checked first, so a build that cannot pair reports on the step the user is still on
    /// instead of flashing a pairing panel they can do nothing about.
    /// </para>
    /// </summary>
    /// <summary>
    /// The route the user picked, or null while they have not — in which case the flow follows availability,
    /// so the step is set up for the account route the moment it becomes possible and for the code route when
    /// it does not. Sticky once chosen: a capability that resolves a second later must not move the selection
    /// out from under someone who has already started typing a code.
    /// </summary>
    private PairingRoute? _chosenRoute;

    /// <summary>The route the link step is set up for right now.</summary>
    public PairingRoute Route => _chosenRoute ?? DefaultRoute;

    private PairingRoute DefaultRoute =>
        _accountPairing is not null && AccountIdIsAutomatic
        && (_accountPairingCapability?.Available ?? false)
        && ResolveCloudDeviceId() is not null
            ? PairingRoute.Account
            : PairingRoute.Code;

    /// <summary>Pick a route. No-op mid-pairing, like every other input on this step.</summary>
    public void SelectRoute(PairingRoute route)
    {
        if (State.IsPairing)
        {
            return;
        }

        Mutate(() =>
        {
            _chosenRoute = route;

            // A code typed for one route is not an input to the other, and leaving it behind means a later
            // switch back silently commits digits the user may have abandoned.
            if (route == PairingRoute.Account)
            {
                _passcode = string.Empty;
            }

            _linkError = null;
        });
    }

    /// <summary>Commit the link step by whichever route it is set up for.</summary>
    public Task PairBySelectedRouteAsync()
        => Route == PairingRoute.Account ? PairWithAccountAsync() : PairAsync();

    public async Task PairAsync()
    {
        if (_step != AddConsoleStep.Link || !State.CanPair)
        {
            return;
        }

        RegistrarAvailability availability = _registrar.CheckAvailability(_family);
        if (!availability.Available)
        {
            Mutate(() => _linkError = string.Format(Strings.Pairing_CryptoUnavailable, availability.Detail));
            return;
        }

        var registration = new ConsoleRegistration(_host, EffectiveAccountId, _passcode, _family);

        await RunPairingAsync(
            token => _registrar.RegisterAsync(registration, token),
            _options.RegistrationTimeout,
            accountRoute: false,
            timeoutMessage: Strings.Pairing_ConsoleTimedOut).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempt the pairing exchange through the signed-in account, with no code.
    ///
    /// <para>
    /// The console's cloud id is resolved here and not by the seam, because it comes from the console list this
    /// flow already fetched alongside the scan — the same value that is stored on the paired console for a
    /// remote wake. Its absence is the one failure worth stating in full: a console that the account does not
    /// list cannot take this route at all, and no amount of retrying changes that.
    /// </para>
    /// </summary>
    public async Task PairWithAccountAsync()
    {
        if (_step != AddConsoleStep.Link || _accountPairing is null || !State.CanPairWithAccount)
        {
            return;
        }

        if (ResolveCloudDeviceId() is not { } cloudDeviceId)
        {
            Mutate(() => _linkError = NotInAccountListMessage);
            return;
        }

        var request = new AccountPairingRequest(_host, EffectiveAccountId, cloudDeviceId, _family);

        await RunPairingAsync(
            token => _accountPairing.PairAsync(request, token),
            _options.AccountPairingTimeout,
            accountRoute: true,
            timeoutMessage: Strings.Pairing_AccountConfirmTimedOut)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Run one pairing attempt, whichever route it takes.
    ///
    /// <para>
    /// Shared because everything except the request itself is identical, and every part of it was a bug once:
    /// cancelling a previous attempt, owning the timeout rather than borrowing one, checking on return that this
    /// attempt is still the current one, and reporting nothing when it is not. A second copy of that dance for
    /// the account route would be a second place for those to regress.
    /// </para>
    /// </summary>
    private async Task RunPairingAsync(
        Func<CancellationToken, Task<ConsoleRegistrationResult>> pair,
        TimeSpan timeout,
        bool accountRoute,
        string timeoutMessage)
    {
        CancelPairing();
        var cts = new CancellationTokenSource(timeout);
        _pairCts = cts;

        Mutate(() =>
        {
            _linkError = null;
            _accountRoute = accountRoute;
            _step = AddConsoleStep.Pairing;
        });

        try
        {
            ConsoleRegistrationResult result = await pair(cts.Token).ConfigureAwait(false);

            if (!ReferenceEquals(_pairCts, cts))
            {
                return; // superseded or disposed while in flight
            }

            if (!result.Succeeded || result.CredentialRecord is null)
            {
                FailBackToLink(result.FailureReason ?? Strings.Pairing_Failed);
                return;
            }

            PairedConsole paired = BuildRecord(result.CredentialRecord);
            Mutate(() =>
            {
                _paired = paired;
                _step = AddConsoleStep.Done;
            });
        }
        catch (OperationCanceledException)
        {
            // A timeout looks the same as a cancel from here; only report if this pairing is still the current
            // one, so leaving the flow does not push an error onto a state nobody is looking at.
            if (ReferenceEquals(_pairCts, cts))
            {
                FailBackToLink(timeoutMessage);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pairCts, cts))
            {
                FailBackToLink(string.Format(Strings.Pairing_Error, ex.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_pairCts, cts))
            {
                _pairCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Save the paired console, optionally starting a session. A nickname is stored only when it differs from
    /// what the console would be called anyway — otherwise a user who accepts the prefilled name silently gets it
    /// pinned, and it stops tracking the console if the console is ever renamed.
    /// </summary>
    public void Finish(string typedName, bool connect)
    {
        if (_paired is null)
        {
            return;
        }

        string typed = (typedName ?? string.Empty).Trim();
        PairedConsole toSave = typed.Length > 0 && typed != _paired.DisplayName
            ? _paired with { Nickname = typed }
            : _paired;

        _store.Upsert(toSave);
        Completed?.Invoke(new AddConsoleCompletion(toSave, connect));
    }

    /// <summary>
    /// Step back. Refused during the pairing exchange: the console is mid-registration and has consumed a link
    /// code, so walking out leaves it having done that for nothing. Returns false when the flow has nowhere left
    /// to go back to, which the front end reads as "leave the flow entirely".
    /// </summary>
    public async Task<bool> BackAsync()
    {
        switch (_step)
        {
            case AddConsoleStep.Find:
                CancelScan();
                Mutate(() =>
                {
                    _step = AddConsoleStep.Family;
                    _isScanning = false;
                });
                return true;

            case AddConsoleStep.Link:
                // Back to the results already found rather than a fresh scan. The page used to restart the
                // search here, discarding what the user had just waited for.
                Mutate(() => _step = AddConsoleStep.Find);
                return true;

            case AddConsoleStep.Pairing:
                return true; // handled, but deliberately does nothing

            default:
                await DisposeAsync().ConfigureAwait(false);
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelScan();
        CancelPairing();

        // The one thing that does end the cloud lookup other than a newer one: the flow itself going away.
        CancellationTokenSource? cloud = _cloudCts;
        _cloudCts = null;
        cloud?.Cancel();
        cloud?.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---- internals -----------------------------------------------------------------------------

    private Task EnterFindAsync()
    {
        Mutate(() =>
        {
            _step = AddConsoleStep.Find;
            _manualEntryOpen = false;
        });
        return StartScanAsync();
    }

    /// <summary>Called inside a Mutate, so it only sets fields.</summary>
    private void EnterLink()
    {
        _step = AddConsoleStep.Link;
        _linkError = null;

        // The route is decided by which button is pressed, and the last press must not colour this one's
        // progress text after a failure sent the user back here.
        _accountRoute = false;
    }

    /// <summary>
    /// Ask whether this build can pair through an account. Never throws: a backend that fails while answering
    /// leaves the code route perfectly usable, so the answer is "not this way", with the reason.
    /// </summary>
    private AccountPairingAvailability ResolveAccountPairingCapability(ConsoleFamily family)
    {
        if (_accountPairing is null)
        {
            return new AccountPairingAvailability(false, NoAccountPairingMessage);
        }

        try
        {
            return _accountPairing.CheckAvailability(family);
        }
        catch (Exception ex)
        {
            return new AccountPairingAvailability(false, string.Format(Strings.Pairing_AccountUnavailable, ex.Message));
        }
    }

    private void FailBackToLink(string message) => Mutate(() =>
    {
        _step = AddConsoleStep.Link;
        _linkError = message;
    });

    private async Task StartScanAsync()
    {
        CancelScan();

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        Mutate(() =>
        {
            Discovered.Clear();
            _isScanning = true;
            _findSubheading = ScanningMessage;
        });

        var completion = new TaskCompletionSource();
        IDisposable? subscription = null;

        // Runs alongside the scan rather than before it: it must never delay results appearing, and a cloud
        // that is slow or down must not stop a purely local pairing from working. Not awaited anywhere and not
        // tied to this scan's lifetime — see _cloudCts.
        StartCloudConsoleLookup();

        try
        {
            subscription = _scanner.Scan(_options.SearchWindow, cts.Token).Subscribe(
                new AnonymousObserver<DiscoveredConsole>(
                    // Freshness is judged when the result ARRIVES, not when the mutation drains. Same reason as
                    // the finally below: Mutate posts, so a check written inside the closure runs later — and by
                    // then the scan may have ended and cleared _scanCts, which would silently discard results
                    // that were perfectly valid when they came in.
                    onNext: console =>
                    {
                        if (IsCurrentScan(cts))
                        {
                            Mutate(() => Accept(console));
                        }
                    },
                    // A scanner that faults has still told us about whatever answered first; report it as an
                    // outcome rather than throwing out of a background subscription.
                    onError: _ => completion.TrySetResult(),
                    onCompleted: () => completion.TrySetResult()));

            // A cancelled subscription may raise neither OnCompleted nor OnError, which would leave this await
            // hanging forever.
            using CancellationTokenRegistration registration =
                cts.Token.Register(() => completion.TrySetResult());

            // The window is the authority on how long a scan lasts; a terminal signal from the scanner is only a
            // fast path. Waiting solely for the signal is what left the spinner up forever in a live run: the
            // discovery producer swallows OperationCanceledException and then raises neither OnCompleted nor
            // OnError, so a single family going quiet meant the scan never appeared to end. A backstop the flow
            // owns cannot be defeated by anything a transport does.
            await Task.WhenAny(completion.Task, DelayQuietly(_options.SearchWindow + ScanGrace, cts.Token))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Decided now, not inside the callback — see the note in the finally below.
            bool faultedScanIsCurrent = ReferenceEquals(_scanCts, cts);
            Mutate(() =>
            {
                if (faultedScanIsCurrent)
                {
                    _findSubheading = string.Format(Strings.Pairing_SearchFailed, ex.Message);
                }
            });
        }
        finally
        {
            subscription?.Dispose();

            // "Is this scan still the current one?" is answered HERE, synchronously, and the answer is captured.
            // Mutate only runs inline when the caller is already on the UI thread; off it — which is where this
            // continuation always lands — it POSTS, so anything the closure reads is read later. Releasing
            // ownership below would then have already nulled _scanCts, the closure's guard would fail, and
            // _isScanning would never be lowered. That is exactly the bug that kept the progress bar up: the
            // guard was correct, but it was being evaluated after the state it guarded against had changed.
            bool isCurrent = ReferenceEquals(_scanCts, cts);

            // Release ownership before disposing. Skipping this left _scanCts referencing a disposed source once a
            // scan had run to completion, and the next CancelScan() threw ObjectDisposedException.
            if (isCurrent)
            {
                _scanCts = null;
            }

            Mutate(() =>
            {
                if (!isCurrent)
                {
                    return;
                }

                _isScanning = false;
                DescribeScanOutcome();
            });

            cts.Dispose();
        }
    }

    /// <summary>
    /// Whether <paramref name="scan"/> is still the scan the flow cares about. The one place that question is
    /// asked, and it must always be asked synchronously with the event being judged — never from inside a posted
    /// closure, which would evaluate it against whatever the state has become by drain time.
    /// </summary>
    private bool IsCurrentScan(CancellationTokenSource scan)
        => ReferenceEquals(_scanCts, scan) && !scan.IsCancellationRequested;

    /// <summary>
    /// Accept one discovery result — the single place results enter the list, so deduplication and ordering have
    /// exactly one home. Freshness has already been decided by the caller.
    /// </summary>
    private void Accept(DiscoveredConsole console)
    {
        if (Discovered.Any(d => d.Console.IpAddress.Equals(console.IpAddress)))
        {
            return;
        }

        // The family the user picked first, then everything else — their console is almost certainly the one they
        // said it was, and it should not be listed below one they were not looking for.
        DiscoveredConsoleCard card = DiscoveredConsoleCard.From(console);
        int insertAt = card.Family == _family
            ? Discovered.Count(d => d.Family == _family)
            : Discovered.Count;
        Discovered.Insert(insertAt, card);
    }

    /// <summary>Called inside a Mutate, so it only sets fields.</summary>
    private void DescribeScanOutcome()
    {
        if (Discovered.Count == 0)
        {
            _findSubheading = Strings.Pairing_NothingAnswered;
            _manualEntryOpen = true;
            return;
        }

        _findSubheading = Discovered.Any(d => d.Family != _family)
            ? Strings.Pairing_PickConsoleMixedFamilies
            : Strings.Pairing_PickConsole;
    }

    /// <summary>
    /// Begin (or restart) the account console-list lookup. Fire-and-forget by design: nothing waits for it, and
    /// <see cref="FetchCloudConsolesAsync"/> cannot fault, so there is no task worth holding.
    /// </summary>
    private void StartCloudConsoleLookup()
    {
        if (_account?.Current is null)
        {
            return;
        }

        CancellationTokenSource? previous = _cloudCts;
        var cts = new CancellationTokenSource();
        _cloudCts = cts;

        // Superseded, not merely ignored: a search-again means the previous answer is no longer the one being
        // asked for, and the old request should stop rather than race the new one.
        previous?.Cancel();
        previous?.Dispose();

        _ = FetchCloudConsolesAsync(cts);
    }

    /// <summary>
    /// Fetch the account's console list, if signed in. Never throws and never reports: this is entirely
    /// optional enrichment, and a user pairing a console on their own sofa should not see a cloud error.
    /// </summary>
    private async Task FetchCloudConsolesAsync(CancellationTokenSource own)
    {
        try
        {
            IReadOnlyList<CloudConsole> consoles = await _account!
                .ListConsolesAsync(own.Token)
                .ConfigureAwait(false);

            // Freshness judged here, synchronously, as the result arrives — the same rule the scan follows, and
            // for the same reason: Mutate posts, so a check written inside the closure runs later. The question
            // is whether THIS lookup is still the current one, which is not the same as whether the scan that
            // started it is: the user picking a console ends the scan and must not discard an answer that is
            // still perfectly good.
            //
            // Applied THROUGH Mutate rather than assigned directly, which it was while nothing but BuildRecord
            // read it. Two reasons, and the first is the layer's rule: this continuation is not on the
            // dispatcher thread, and every field here is written on that thread alone. The second is visible —
            // whether the account already knows this console decides whether account pairing is offered, so a
            // list that lands while the user is on the link step has to recompose the state, or the button
            // stays greyed out until something unrelated moves.
            if (ReferenceEquals(_cloudCts, own))
            {
                Mutate(() => _cloudConsoles = consoles);
            }
        }
        catch (Exception)
        {
            // Signed out mid-scan, offline, cloud down, or a token that expired. All of them mean "we will not
            // learn the cloud id this time", which costs only the ability to wake this console remotely.
        }
    }

    /// <summary>
    /// The account service's id for the console being paired. Shared with the backfill that repairs consoles
    /// paired before sign-in existed — see <see cref="CloudConsoleMatch"/> for the matching rule and why an
    /// ambiguous match deliberately yields nothing.
    /// </summary>
    private string? ResolveCloudDeviceId()
        => CloudConsoleMatch.ResolveId(_cloudConsoles, _selected?.Console.DisplayName);

    /// <summary>The signed-in account's name in parentheses, or nothing when it has none to show.</summary>
    private string FormatAccountName()
    {
        string? name = _account?.Current?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? string.Empty : $" ({name})";
    }

    private PairedConsole BuildRecord(byte[] credentialRecord)
    {
        // The console's own host-id is a better identity than its current address, which a DHCP lease can move
        // out from under us. Falls back to the address for a console typed in by hand.
        string? hostId = _selected?.Console.Id;

        return new PairedConsole(
            Id: string.IsNullOrEmpty(hostId) ? _host : hostId,
            Name: _family.LongName,
            Host: _host,
            Platform: _family.Key,
            CredentialBlob: _store.EncodeBlob(credentialRecord))
        {
            HostId = hostId,
            ReportedName = _selected?.Console.DisplayName,
            SystemVersion = _selected?.Console.SystemVersion,
            CloudDeviceId = ResolveCloudDeviceId(),
        };
    }

    /// <summary>
    /// A delay that ends quietly when cancelled, so it can be raced with <c>Task.WhenAny</c> without leaving a
    /// faulted task nobody observes.
    /// </summary>
    private async Task DelayQuietly(TimeSpan span, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(span, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Abandon the current scan, if any.
    ///
    /// <para>
    /// Clears <c>_isScanning</c> here rather than leaving it to the scan's own completion path, because an
    /// abandoned scan never reaches that path: the flag is only ever lowered by the scan that owns it, and this
    /// one has just been disowned. Leaving it set meant the Find step came back with a spinner that never stopped
    /// and a disabled Search-again button — visible as soon as a user picked a console before the four-second
    /// window closed, which is most of the time.
    /// </para>
    /// </summary>
    private void CancelScan()
    {
        CancellationTokenSource? cts = _scanCts;
        _scanCts = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        Mutate(() => _isScanning = false);
    }

    private void CancelPairing()
    {
        CancellationTokenSource? cts = _pairCts;
        _pairCts = null;
        cts?.Cancel();
    }

    protected override AddConsoleFlowState Compose()
    {
        string name = _selected?.DisplayName ?? _host;

        // Offered whenever someone is signed in — and only then, because the route IS the account. A build with
        // no credential never has a signed-in account, so it never reaches this and the code route is all it
        // shows, exactly as before. Enabled is the stricter question: the constants have to be present and the
        // account has to already know this console.
        bool accountPairingOffered = _accountPairing is not null && AccountIdIsAutomatic;
        bool accountCapable = _accountPairingCapability?.Available ?? false;
        bool consoleKnownToAccount = ResolveCloudDeviceId() is not null;

        return new AddConsoleFlowState(
            Step: _step,

            // THREE dashes, not four, and they are the mark's own three.
            //
            // Pairing shares the link step's dash: it is the same step from the user's point of view, just
            // the part they are not doing anything during. Family shares FIND's, because discovery leads now
            // and the family question is the exception rather than a stage - it appears only on the manual
            // path and when a scan finds nothing, and counting it as its own step would make the common
            // journey look like it skipped one.
            //
            // Four equal dashes were a progress bar. Three uneven ones are the trail from the mark, which is
            // the same shape the connect sequence fills in and the celebration assembles.
            ReachedDash: _step switch
            {
                AddConsoleStep.Family or AddConsoleStep.Find => 1,
                AddConsoleStep.Link or AddConsoleStep.Pairing => 2,
                _ => 3,
            },
            Family: _family,
            FamilyNote: _familyNote,
            FindHeading: string.Format(Strings.Pairing_LookingFor, _family.ShortName),
            FindSubheading: _findSubheading,
            IsScanning: _isScanning,
            ManualEntryOpen: _manualEntryOpen,
            LinkHeading: string.Format(Strings.Pairing_LinkHeading, name),

            // Sending someone to a menu that does not exist on their console is the fastest way to lose them.
            ConsoleStepsText: _family == ConsoleFamily.Ps4
                ? Strings.Pairing_ConsoleStepsLegacy
                : Strings.Pairing_ConsoleSteps,

            // Enabled once both fields could plausibly be right. A disabled button that explains itself beats a
            // validation error after the fact.
            // Route-aware: the account route needs no code, so gating its button on eight digits nobody was
            // asked for is how a working action arrives disabled.
            CanPair: Route == PairingRoute.Account
                ? accountPairingOffered && accountCapable && consoleKnownToAccount
                : _passcode.Length >= _options.MinimumPasscodeLength && EffectiveAccountId.Length > 0,

            AccountPairingOffered: accountPairingOffered,
            CanPairWithAccount: accountPairingOffered && accountCapable && consoleKnownToAccount,

            Route: Route,

            // Only worth asking when both can actually work. Where the account route cannot, the step shows
            // the code route without putting a decision in front of someone who has none to make.
            RouteChoiceOffered: accountPairingOffered && accountCapable && consoleKnownToAccount,
            SwitchRouteLabel: Route == PairingRoute.Account
                ? Strings.Pairing_UseCodeInstead
                : Strings.Pairing_UseAccountInstead,
            CodeEntryShown: Route == PairingRoute.Code,
            // At Done the record is already on disk, so this is not a save button - it is the thing the
            // player came for, named as such. It was the literal string "Save & connect" in the page's
            // code-behind, past the catalogue entirely.
            PairActionLabel: _step == AddConsoleStep.Done
                ? Strings.Pairing_PlayNow
                : Route == PairingRoute.Account
                    ? Strings.Pairing_ActionWithAccount
                    : Strings.Pairing_ActionWithCode,

            // Three different things to say, and the difference matters: one is an invitation, one is fixable on
            // the console, and one is a property of the build the user cannot do anything about.
            // Suppressed on the code route: "no code needed" above a box asking for one is the contradiction
            // this step used to open with. The reasons the account route is *unavailable* still show, because
            // those explain why there is no choice rather than describing a route being taken.
            AccountPairingNote: !accountPairingOffered
                ? string.Empty
                : !accountCapable
                    ? _accountPairingCapability?.Detail ?? NoAccountPairingMessage
                    : !consoleKnownToAccount
                        ? NotInAccountListMessage
                        : Route == PairingRoute.Account
                            ? string.Format(Strings.Pairing_NoCodeNeeded, name)
                            : string.Empty,

            // When signed in, the account id stops being something the user has to find. This is the whole point
            // of the account tier for someone who only ever plays on their own network.
            AccountIdIsAutomatic: AccountIdIsAutomatic,
            AccountIdNote: AccountIdIsAutomatic
                ? string.Format(Strings.Pairing_UsingSignedInAccount, FormatAccountName())
                : Strings.Pairing_SignInToAutofill,
            LinkError: _linkError,
            PairingStatus: _accountRoute
                ? string.Format(Strings.Pairing_WaitingForAccountConfirm, name)
                : string.Format(Strings.Pairing_Registering, name),

            // The code route asks the user to leave something on screen; the account route asks nothing of them
            // and takes longer. Telling someone to keep a code visible when they never entered one is the kind
            // of leftover that makes people go and look for a code.
            PairingHint: _accountRoute
                ? Strings.Pairing_HintAccountRoute
                : Strings.Pairing_HintCodeRoute,

            // Live everywhere except during the exchange. On the first step Back means "leave the flow", which is
            // a perfectly good thing to want, so it stays enabled rather than being a dead button on arrival.
            // On Done the console is already paired and going back would mean re-pairing it.
            CanGoBack: _step is not (AddConsoleStep.Pairing or AddConsoleStep.Done),
            SuggestedName: _paired?.DisplayName ?? string.Empty,
            DoneSubtext: _paired is null
                ? string.Empty
                : string.Format(Strings.Pairing_DoneSubtext, _paired.DisplayName));
    }
}
