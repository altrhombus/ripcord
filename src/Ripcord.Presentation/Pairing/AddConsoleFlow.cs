using System.Collections.ObjectModel;
using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;

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

    private const string ScanningMessage =
        "Make sure the console is switched on and on the same network as this PC.";

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
        IAccountSession? account = null)
        : base(dispatcher)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new AddConsoleFlowOptions();
        _delay = delay ?? Task.Delay;
        _account = account;
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
        Mutate(() =>
        {
            _selected = null;
            _host = typed;
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
        Mutate(() =>
        {
            _selected = card;
            _family = card.Family;
            _host = card.Address;
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
    /// Attempt the pairing exchange.
    ///
    /// <para>
    /// Availability is checked first, so a build that cannot pair reports on the step the user is still on
    /// instead of flashing a pairing panel they can do nothing about.
    /// </para>
    /// </summary>
    public async Task PairAsync()
    {
        if (_step != AddConsoleStep.Link || !State.CanPair)
        {
            return;
        }

        RegistrarAvailability availability = _registrar.CheckAvailability(_family);
        if (!availability.Available)
        {
            Mutate(() => _linkError = $"Registration crypto unavailable: {availability.Detail}");
            return;
        }

        var registration = new ConsoleRegistration(_host, EffectiveAccountId, _passcode, _family);

        CancelPairing();
        var cts = new CancellationTokenSource(_options.RegistrationTimeout);
        _pairCts = cts;

        Mutate(() =>
        {
            _linkError = null;
            _step = AddConsoleStep.Pairing;
        });

        try
        {
            ConsoleRegistrationResult result = await _registrar
                .RegisterAsync(registration, cts.Token)
                .ConfigureAwait(false);

            if (!ReferenceEquals(_pairCts, cts))
            {
                return; // superseded or disposed while in flight
            }

            if (!result.Succeeded || result.CredentialRecord is null)
            {
                FailBackToLink(result.FailureReason ?? "Pairing failed.");
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
                FailBackToLink("The console didn't answer in time.");
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pairCts, cts))
            {
                FailBackToLink($"Pairing error: {ex.Message}");
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
        // that is slow or down must not stop a purely local pairing from working.
        Task cloudLookup = FetchCloudConsolesAsync(cts);

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
                    _findSubheading = $"Couldn't search the network: {ex.Message}";
                }
            });
        }
        finally
        {
            subscription?.Dispose();

            // Observed so a failure cannot surface as an unobserved task exception. Its result is optional by
            // design, so there is nothing to do with it beyond not letting it escape.
            await cloudLookup.ConfigureAwait(false);

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
            _findSubheading =
                "Nothing answered. The console needs to be switched on, on the same network, and reachable — "
                + "some networks block the broadcast this uses. You can enter its address instead.";
            _manualEntryOpen = true;
            return;
        }

        _findSubheading = Discovered.Any(d => d.Family != _family)
            ? "Pick your console. We also found consoles from another family — they're listed too, in case you "
              + "picked the wrong one."
            : "Pick your console.";
    }

    /// <summary>
    /// Fetch the account's console list, if signed in. Never throws and never reports: this is entirely
    /// optional enrichment, and a user pairing a console on their own sofa should not see a cloud error.
    /// </summary>
    private async Task FetchCloudConsolesAsync(CancellationTokenSource scan)
    {
        if (_account?.Current is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<CloudConsole> consoles = await _account
                .ListConsolesAsync(scan.Token)
                .ConfigureAwait(false);

            // Freshness judged here, synchronously, as the result arrives — the same rule the scan follows, and
            // for the same reason: Mutate posts, so a check written inside the closure runs later.
            if (IsCurrentScan(scan))
            {
                _cloudConsoles = consoles;
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

        return new AddConsoleFlowState(
            Step: _step,

            // Pairing shares the link step's dash: it is the same step from the user's point of view, just the
            // part they are not doing anything during.
            ReachedDash: _step switch
            {
                AddConsoleStep.Family => 1,
                AddConsoleStep.Find => 2,
                AddConsoleStep.Link or AddConsoleStep.Pairing => 3,
                _ => 4,
            },
            Family: _family,
            FamilyNote: _familyNote,
            FindHeading: $"Looking for your {_family.ShortName}",
            FindSubheading: _findSubheading,
            IsScanning: _isScanning,
            ManualEntryOpen: _manualEntryOpen,
            LinkHeading: $"Link this PC to {name}",

            // Sending someone to a menu that does not exist on their console is the fastest way to lose them.
            ConsoleStepsText: _family == ConsoleFamily.Ps4
                ? "Open Settings → Remote Play Connection Settings → Add Device. The console shows an 8-digit code."
                : "Open Settings → System → Remote Play → Link Device. The console shows an 8-digit code.",

            // Enabled once both fields could plausibly be right. A disabled button that explains itself beats a
            // validation error after the fact.
            CanPair: _passcode.Length >= _options.MinimumPasscodeLength && EffectiveAccountId.Length > 0,

            // When signed in, the account id stops being something the user has to find. This is the whole point
            // of the account tier for someone who only ever plays on their own network.
            AccountIdIsAutomatic: AccountIdIsAutomatic,
            AccountIdNote: AccountIdIsAutomatic
                ? $"Using the account you're signed in as{FormatAccountName()}."
                : "Sign in to your account and this fills itself in.",
            LinkError: _linkError,
            PairingStatus: $"Registering with {name}…",

            // Live everywhere except during the exchange. On the first step Back means "leave the flow", which is
            // a perfectly good thing to want, so it stays enabled rather than being a dead button on arrival.
            // On Done the console is already paired and going back would mean re-pairing it.
            CanGoBack: _step is not (AddConsoleStep.Pairing or AddConsoleStep.Done),
            SuggestedName: _paired?.DisplayName ?? string.Empty,
            DoneSubtext: _paired is null
                ? string.Empty
                : $"{_paired.DisplayName} is linked to this PC. You won't need the code again.");
    }
}
