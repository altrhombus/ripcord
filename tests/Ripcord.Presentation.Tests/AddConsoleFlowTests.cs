using System.Net;
using Ripcord.Core;
using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The add-a-console state machine.
///
/// <para>
/// None of this could be tested while it lived in a page: exercising a single transition meant launching the app
/// and having a real console on the LAN, so the whole flow — including every failure path — was verified by hand
/// or not at all. These tests are the reason the extraction was worth doing.
/// </para>
/// </summary>
public class AddConsoleFlowTests
{
    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeScanner : IConsoleScanner
    {
        private readonly List<DiscoveredConsole> _results = [];

        public int ScanCount { get; private set; }

        public TimeSpan? LastWindow { get; private set; }

        /// <summary>When set, the scan emits results but never completes until <see cref="Complete"/>.</summary>
        public bool HoldOpen { get; set; }

        public Exception? Fault { get; set; }

        private IObserver<DiscoveredConsole>? _observer;

        public FakeScanner Yields(params DiscoveredConsole[] results)
        {
            _results.AddRange(results);
            return this;
        }

        /// <summary>Push one more result after the fact, as a console answering late would.</summary>
        public void Emit(DiscoveredConsole console) => _observer?.OnNext(console);

        public void Complete() => _observer?.OnCompleted();

        public IObservable<DiscoveredConsole> Scan(TimeSpan window, CancellationToken cancellationToken)
        {
            ScanCount++;
            LastWindow = window;
            return new Sequence(this, cancellationToken);
        }

        private sealed class Sequence(FakeScanner owner, CancellationToken cancellationToken)
            : IObservable<DiscoveredConsole>
        {
            public IDisposable Subscribe(IObserver<DiscoveredConsole> observer)
            {
                owner._observer = observer;

                foreach (DiscoveredConsole console in owner._results)
                {
                    observer.OnNext(console);
                }

                if (owner.Fault is { } fault)
                {
                    observer.OnError(fault);
                }
                else if (!owner.HoldOpen && !cancellationToken.IsCancellationRequested)
                {
                    observer.OnCompleted();
                }

                return new NoopSubscription();
            }
        }

        private sealed class NoopSubscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeRegistrar : IConsoleRegistrar
    {
        public bool Available { get; set; } = true;

        public string AvailabilityDetail { get; set; } = "bundled interop constants";

        public ConsoleRegistrationResult Result { get; set; } =
            new(true, null, [1, 2, 3, 4]);

        public Exception? Throws { get; set; }

        /// <summary>Never completes until released, standing in for a console that has stopped answering.</summary>
        public bool HangForever { get; set; }

        public int RegisterCalls { get; private set; }

        public ConsoleRegistration? LastRegistration { get; private set; }

        public ConsoleFamily? AvailabilityAskedFor { get; private set; }

        public RegistrarAvailability CheckAvailability(ConsoleFamily family)
        {
            AvailabilityAskedFor = family;
            return new RegistrarAvailability(Available, AvailabilityDetail);
        }

        public async Task<ConsoleRegistrationResult> RegisterAsync(
            ConsoleRegistration registration, CancellationToken cancellationToken)
        {
            RegisterCalls++;
            LastRegistration = registration;

            if (Throws is { } ex)
            {
                throw ex;
            }

            if (HangForever)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return Result;
        }
    }

    private sealed class FakeAccountPairing : IAccountConsolePairing
    {
        public bool Available { get; set; } = true;

        public string AvailabilityDetail { get; set; } = "bundled interop constants";

        public Exception? AvailabilityThrows { get; set; }

        public ConsoleRegistrationResult Result { get; set; } = new(true, null, [9, 8, 7, 6]);

        public Exception? Throws { get; set; }

        /// <summary>Never completes until released, standing in for a console that never confirms.</summary>
        public bool HangForever { get; set; }

        public int PairCalls { get; private set; }

        public AccountPairingRequest? LastRequest { get; private set; }

        public ConsoleFamily? AvailabilityAskedFor { get; private set; }

        public AccountPairingAvailability CheckAvailability(ConsoleFamily family)
        {
            AvailabilityAskedFor = family;

            if (AvailabilityThrows is { } ex)
            {
                throw ex;
            }

            return new AccountPairingAvailability(Available, AvailabilityDetail);
        }

        public async Task<ConsoleRegistrationResult> PairAsync(
            AccountPairingRequest request, CancellationToken cancellationToken)
        {
            PairCalls++;
            LastRequest = request;

            if (Throws is { } ex)
            {
                throw ex;
            }

            if (HangForever)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return Result;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DiscoveredConsole Console(
        string address,
        ConsolePlatform platform = ConsolePlatform.Halyard,
        string name = "PS5-8A2F",
        string id = "host-id",
        bool awake = true,
        string? systemVersion = null) =>
        new(id, name, platform, IPAddress.Parse(address), awake, DiscoveryTransport.LanBroadcast, systemVersion);

    private sealed class Harness
    {
        public FakeScanner Scanner { get; } = new();
        public FakeRegistrar Registrar { get; } = new();
        public FakeAccountPairing AccountPairing { get; } = new();
        public InMemoryPairedConsoleStore Store { get; } = new();
        public AddConsoleFlow Flow { get; }
        public List<AddConsoleCompletion> Completions { get; } = [];

        /// <param name="delay">
        /// Defaults to completing instantly, so the scan's window backstop does not make every test wait five
        /// real seconds. Tests that care about the timing pass their own recorder.
        /// </param>
        /// <param name="withAccountPairing">
        /// False builds the flow with no account-pairing seam at all, which is what a front end that composed
        /// none looks like. Defaulted on, because the seam being present but declining is the ordinary case.
        /// </param>
        public Harness(
            AddConsoleFlowOptions? options = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            IAccountSession? account = null,
            bool withAccountPairing = true)
        {
            Flow = new AddConsoleFlow(
                Scanner,
                Registrar,
                Store,
                new ImmediateUiDispatcher(),
                options,
                delay ?? ((_, _) => Task.CompletedTask),
                account,
                withAccountPairing ? AccountPairing : null);
            Flow.Completed += Completions.Add;
        }

        /// <summary>Seed one console for the scan to find, so StartAsync has something to report.</summary>
        public Harness WithConsole(DiscoveredConsole? console = null)
        {
            Scanner.Yields(console ?? Console("10.0.0.7"));
            return this;
        }

        /// <summary>Drive the flow to the Link step with a console picked from the scan.</summary>
        public async Task<DiscoveredConsoleCard> ToLinkViaScanAsync(DiscoveredConsole? console = null)
        {
            Scanner.Yields(console ?? Console("10.0.0.7"));
            await Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
            DiscoveredConsoleCard card = Flow.Discovered.Single();
            Flow.SelectDiscovered(card);
            return card;
        }

        public void EnterValidLinkInput() => Flow.SetLinkInput("12345678", "1234567890123456");
    }

    /// <summary>
    /// Wait for something a fire-and-forget continuation will make true. Polled rather than signalled because
    /// the thing under test deliberately hands back no task to await — the cloud lookup is nobody's to wait on.
    /// </summary>
    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>
    /// A backstop delay that never elapses by itself but still honours cancellation. For tests that need a scan
    /// to stay genuinely in progress, rather than being ended immediately by the window backstop.
    /// </summary>
    private static Task NeverElapses(TimeSpan span, CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken);

    /// <summary>
    /// A dispatcher that genuinely defers, like the real one.
    ///
    /// <para>
    /// <see cref="ImmediateUiDispatcher"/> applies every mutation inline, which is what makes the other tests
    /// synchronous — but it also means those tests cannot see a class of bug that only exists when a mutation is
    /// POSTED: a closure that reads a field later than the code which changed it. One such bug shipped and kept
    /// the discovery progress bar on screen, so the ordering deserves a test of its own.
    /// </para>
    /// </summary>
    private sealed class DeferringDispatcher : IUiDispatcher
    {
        private readonly List<Action> _pending = [];

        public bool IsOnUiThread => false;

        public void Post(Action action) => _pending.Add(action);

        public void Drain()
        {
            // By index: draining can queue more work, and that work must run too.
            for (int i = 0; i < _pending.Count; i++)
            {
                _pending[i]();
            }

            _pending.Clear();
        }
    }

    // ---- family step ---------------------------------------------------------------------------

    [Fact]
    public async Task StartsByLooking_NotByAsking()
    {
        // DISCOVERY LEADS. The flow used to open on "which console are you connecting to?" - a question the
        // scan answers by itself, since every console found reports its own platform and SelectDiscovered has
        // always taken the family from the console rather than the guess. The step existed to display a shape.
        var h = new Harness().WithConsole();

        await h.Flow.StartAsync();

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
        Assert.Equal(1, h.Scanner.ScanCount);
        Assert.Equal(1, h.Flow.State.ReachedDash);
    }

    [Fact]
    public async Task TheScanHeadingNamesNoFamilyUntilSomebodyPicksOne()
    {
        // It read "Looking for your PS5" on the way in, which is the app telling the user what they are
        // looking for on the strength of a default nobody chose.
        var h = new Harness().WithConsole();

        await h.Flow.StartAsync();
        Assert.DoesNotContain("PS5", h.Flow.State.FindHeading);

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        Assert.Contains("PS5", h.Flow.State.FindHeading);
    }

    [Fact]
    public async Task AConsoleFoundByScanningNeverAsksItsFamily()
    {
        // The whole point. The console reports what it is, so the question never reaches the player.
        var h = new Harness().WithConsole();

        await h.Flow.StartAsync();
        h.Flow.SelectDiscovered(h.Flow.Discovered[0]);

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Equal(ConsoleFamily.Ps5, h.Flow.State.Family);
    }

    [Fact]
    public async Task AnEmptyScanIsWhereTheFamilyQuestionBelongs()
    {
        // Nothing answered, so nothing has said what we are looking for - and an address typed by hand cannot
        // be paired without knowing its family. This is the one place the question is genuinely unanswerable.
        var h = new Harness();

        await h.Flow.StartAsync();

        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);
        Assert.NotNull(h.Flow.State.FamilyNote);
    }

    [Fact]
    public async Task PickingAFamilyAfterAnEmptyScanDoesNotScanAgain()
    {
        // The scanner is family-agnostic, so a second scan would look for the same consoles in the same place
        // and find the same nothing, having spent the search window doing it. Go where they were headed.
        var h = new Harness();

        await h.Flow.StartAsync();
        Assert.Equal(1, h.Scanner.ScanCount);

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Equal(1, h.Scanner.ScanCount);
        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
        Assert.True(h.Flow.State.ManualEntryOpen);
    }

    [Fact]
    public async Task BackFromTheFirstScanLeavesTheFlow()
    {
        // Find is the first step now. For anybody whose scan found something there is nothing behind it, and
        // Back means leaving - inventing a step to go back to would be worse.
        var h = new Harness().WithConsole();

        await h.Flow.StartAsync();

        Assert.False(await h.Flow.BackAsync());
    }

    [Fact]
    public async Task UnsupportedFamily_ShowsTheCaveatAndGoesNoFurther()
    {
        // Reached the way a player now reaches it: a scan found nothing, so the family is asked. Picking one
        // the app cannot stream must say so and stay put.
        var h = new Harness();
        await h.Flow.StartAsync();
        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Xbox);

        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);
        Assert.NotNull(h.Flow.State.FamilyNote);
        Assert.Equal(1, h.Scanner.ScanCount);
    }

    [Fact]
    public async Task SupportedFamily_AdvancesAndScans()
    {
        var h = new Harness();

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);

        // Still the first dash. Family and Find share one: discovery leads, and the family question is the
        // exception rather than a stage - counting it separately made the common journey look like it had
        // skipped a step.
        Assert.Equal(1, h.Flow.State.ReachedDash);
        Assert.Equal(1, h.Scanner.ScanCount);
        Assert.Contains("PS5", h.Flow.State.FindHeading);
    }

    [Fact]
    public async Task SearchWindowIsGenerous_BecauseRestingConsolesAnswerSlowly()
    {
        var h = new Harness();
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        Assert.Equal(TimeSpan.FromSeconds(4), h.Scanner.LastWindow);
    }

    // ---- find step -----------------------------------------------------------------------------

    [Fact]
    public async Task Scan_PublishesEachConsoleAsItAnswers()
    {
        var h = new Harness(delay: NeverElapses);
        h.Scanner.HoldOpen = true;
        h.Scanner.Yields(Console("10.0.0.7"));

        Task select = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        // Already visible while the scan is still running, rather than appearing only when the window closes.
        Assert.Single(h.Flow.Discovered);
        Assert.True(h.Flow.State.IsScanning);

        h.Scanner.Complete();
        await select;

        Assert.False(h.Flow.State.IsScanning);
    }

    [Fact]
    public async Task Scan_DeduplicatesByAddress()
    {
        var h = new Harness();
        h.Scanner.Yields(Console("10.0.0.7"), Console("10.0.0.7", name: "same box again"));

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Single(h.Flow.Discovered);
    }

    [Fact]
    public async Task Scan_FloatsTheChosenFamilyAboveTheOthers()
    {
        // Their console is almost certainly the family they said it was, and should not be listed under one they
        // were not looking for.
        var h = new Harness();
        h.Scanner.Yields(
            Console("10.0.0.8", ConsolePlatform.HalyardLegacy, name: "a-ps4"),
            Console("10.0.0.7", ConsolePlatform.Halyard, name: "a-ps5"));

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Equal("a-ps5", h.Flow.Discovered[0].DisplayName);
        Assert.Equal("a-ps4", h.Flow.Discovered[1].DisplayName);
    }

    [Fact]
    public async Task Scan_FindingNothing_ExplainsWhyAndOpensManualEntry()
    {
        var h = new Harness();

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Empty(h.Flow.Discovered);
        Assert.True(h.Flow.State.ManualEntryOpen);
        Assert.Contains("Nothing answered", h.Flow.State.FindSubheading);
    }

    [Fact]
    public async Task Scan_FindingAnotherFamilyToo_SaysSo()
    {
        var h = new Harness();
        h.Scanner.Yields(Console("10.0.0.8", ConsolePlatform.HalyardLegacy));

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Contains("another family", h.Flow.State.FindSubheading);
    }

    [Fact]
    public async Task Scan_ThatFaults_StopsScanningRatherThanHanging()
    {
        // A scanner error must resolve the scan, not leave the spinner up forever.
        var h = new Harness();
        h.Scanner.Fault = new InvalidOperationException("socket in use");

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.False(h.Flow.State.IsScanning);
    }

    [Fact]
    public async Task Rescan_ClearsWhatThePreviousScanFound()
    {
        var h = new Harness();
        h.Scanner.Yields(Console("10.0.0.7"));
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        Assert.Single(h.Flow.Discovered);

        await h.Flow.RescanAsync();

        Assert.Equal(2, h.Scanner.ScanCount);
        Assert.Single(h.Flow.Discovered);   // re-found, not duplicated
    }

    [Fact]
    public async Task Scan_ResultArrivingAfterDispose_IsDropped()
    {
        // The post-navigation mutation bug. The page marshalled each result onto the dispatcher with no way to
        // ask whether the scan was still current, so a console answering late mutated a dead page's collection.
        var h = new Harness();
        h.Scanner.HoldOpen = true;
        Task select = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        await h.Flow.DisposeAsync();
        h.Scanner.Emit(Console("10.0.0.99"));

        Assert.Empty(h.Flow.Discovered);

        h.Scanner.Complete();
        await select;
    }

    [Fact]
    public async Task Scan_ResultFromASupersededScan_IsDropped()
    {
        var h = new Harness();
        h.Scanner.HoldOpen = true;
        Task first = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        // Start a second scan. The first is now stale, and its cancellation token is cancelled.
        h.Scanner.HoldOpen = false;
        await h.Flow.RescanAsync();

        int before = h.Flow.Discovered.Count;
        h.Scanner.Emit(Console("10.0.0.99"));

        Assert.Equal(before, h.Flow.Discovered.Count);

        h.Scanner.Complete();
        await first;
    }

    [Fact]
    public async Task Scan_EndingOnADeferringDispatcher_StillLowersTheSpinner()
    {
        // The bug this pins shipped twice, and neither of the other ~50 tests could see it: with mutations applied
        // inline, "is this scan still current?" is evaluated before ownership is released, so the guard passes. On
        // the real dispatcher the check is POSTED and runs afterwards, the guard fails, and _isScanning is never
        // lowered — a progress bar that stays up forever while the user watches the list.
        var dispatcher = new DeferringDispatcher();
        var scanner = new FakeScanner();
        scanner.Yields(Console("10.0.0.7"));

        var flow = new AddConsoleFlow(
            scanner,
            new FakeRegistrar(),
            new InMemoryPairedConsoleStore(),
            dispatcher,
            options: null,
            delay: (_, _) => Task.CompletedTask);

        await flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        dispatcher.Drain();

        Assert.False(flow.State.IsScanning);
        Assert.Single(flow.Discovered);
    }

    [Fact]
    public async Task Scan_ThatNeverSignalsCompletion_StillEndsAfterTheWindow()
    {
        // The live bug this backstop exists for. Ripcord.Core's AsyncObservable swallows
        // OperationCanceledException and then raises NEITHER OnCompleted NOR OnError, so one discovery family
        // going quiet meant the merged scan never appeared to finish and the progress bar stayed up forever while
        // the user watched the list. The window has to be the authority; a terminal signal is only a fast path.
        var h = new Harness();
        h.Scanner.HoldOpen = true;                 // never completes, never errors
        h.Scanner.Yields(Console("10.0.0.7"));

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.False(h.Flow.State.IsScanning);
        Assert.Single(h.Flow.Discovered);           // and whatever answered is still listed
        Assert.Equal("Pick your console.", h.Flow.State.FindSubheading);
    }

    [Fact]
    public async Task Scan_WaitsPastTheWindowBeforeGivingUpOnTheSignal()
    {
        // The backstop must not cut a well-behaved scan short, so it waits the window plus a grace.
        var recorded = new List<TimeSpan>();
        var h = new Harness(delay: (span, _) =>
        {
            recorded.Add(span);
            return Task.CompletedTask;
        });
        h.Scanner.HoldOpen = true;

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Single(recorded);
        Assert.True(recorded[0] > TimeSpan.FromSeconds(4), $"backstop was {recorded[0]}, expected > the 4s window");
    }

    [Fact]
    public async Task SelectDiscovered_MidScan_StopsShowingTheScanAsRunning()
    {
        // The scan is abandoned, not finished, so its own completion path never runs — but the user has left the
        // Find step and nothing is scanning any more. A stale IsScanning leaves the progress bar spinning and
        // Search-again disabled the moment they step back to Find.
        var h = new Harness(delay: NeverElapses);
        h.Scanner.HoldOpen = true;
        h.Scanner.Yields(Console("10.0.0.7"));

        Task select = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        Assert.True(h.Flow.State.IsScanning);

        h.Flow.SelectDiscovered(h.Flow.Discovered.Single());

        Assert.False(h.Flow.State.IsScanning);

        h.Scanner.Complete();
        await select;
        Assert.False(h.Flow.State.IsScanning);
    }

    [Fact]
    public async Task Back_FromLink_LeavesTheFindStepUsable()
    {
        // Back no longer rescans, so whatever IsScanning was left as is what the user sees. It must be false, or
        // Find comes back with a spinner that never stops and a disabled Search-again button.
        var h = new Harness();
        h.Scanner.HoldOpen = true;
        h.Scanner.Yields(Console("10.0.0.7"));
        Task select = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        h.Flow.SelectDiscovered(h.Flow.Discovered.Single());

        Assert.True(await h.Flow.BackAsync());

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
        Assert.False(h.Flow.State.IsScanning);

        h.Scanner.Complete();
        await select;
    }

    [Fact]
    public async Task UseTypedAddress_MidScan_AlsoStopsShowingTheScanAsRunning()
    {
        var h = new Harness();
        h.Scanner.HoldOpen = true;
        Task select = h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        h.Flow.UseTypedAddress("10.0.0.50");

        Assert.False(h.Flow.State.IsScanning);

        h.Scanner.Complete();
        await select;
    }

    [Fact]
    public async Task UseTypedAddress_Blank_IsIgnored()
    {
        var h = new Harness();
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        h.Flow.UseTypedAddress("   ");

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
    }

    [Fact]
    public async Task UseTypedAddress_KeepsTheChosenFamily()
    {
        // Nothing has told us otherwise for a hand-typed address, unlike a console picked from the scan.
        var h = new Harness();
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps4);

        h.Flow.UseTypedAddress(" 10.0.0.50 ");

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Equal(ConsoleFamily.Ps4, h.Flow.State.Family);
        Assert.Contains("10.0.0.50", h.Flow.State.LinkHeading);
    }

    [Fact]
    public async Task SelectDiscovered_AdoptsTheConsolesOwnFamily_NotTheGuess()
    {
        // A PS4 found while PS5 was selected must be treated as a PS4 — the console reports what it is.
        var h = new Harness();
        h.Scanner.Yields(Console("10.0.0.8", ConsolePlatform.HalyardLegacy, name: "the-ps4"));
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        h.Flow.SelectDiscovered(h.Flow.Discovered.Single());

        Assert.Equal(ConsoleFamily.Ps4, h.Flow.State.Family);
    }

    [Fact]
    public async Task LinkInstructions_NameTheRightMenuForTheFamily()
    {
        // Sending someone to a menu that does not exist on their console is the fastest way to lose them.
        var ps5 = new Harness();
        await ps5.ToLinkViaScanAsync();
        Assert.Contains("System → Remote Play", ps5.Flow.State.ConsoleStepsText);

        var ps4 = new Harness();
        await ps4.Flow.SelectFamilyAsync(ConsoleFamily.Ps4);
        ps4.Flow.UseTypedAddress("10.0.0.50");
        Assert.Contains("Remote Play Connection Settings", ps4.Flow.State.ConsoleStepsText);
    }

    // ---- link step -----------------------------------------------------------------------------

    [Theory]
    [InlineData("", "", false)]
    [InlineData("1234567", "account", false)]     // one digit short
    [InlineData("12345678", "", false)]           // no account
    [InlineData("12345678", "account", true)]
    [InlineData("  12345678  ", "  account  ", true)]   // trimmed
    public async Task CanPair_RequiresBothFields(string passcode, string account, bool expected)
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();

        h.Flow.SetLinkInput(passcode, account);

        Assert.Equal(expected, h.Flow.State.CanPair);
    }

    // ---- account id, typed or automatic ---------------------------------------------------------

    [Fact]
    public async Task NotSignedIn_SignInLeadsAndTheCodeFormWaits()
    {
        // The code route LOOKS like the low-friction one and is not. It wants somebody at the console,
        // through its menus, reading an 8-digit code, and holding a numeric account id almost nobody knows -
        // this app's own caption sends them to a third-party lookup tool for it, and the vendor's client
        // never asks for that number at all. Signing in wants a password they already have.
        var h = new Harness(account: new FakeAccountSession());

        await h.ToLinkViaScanAsync();

        Assert.True(h.Flow.State.SignInLeads);
        Assert.False(h.Flow.State.CodeEntryShown);
        Assert.NotEmpty(h.Flow.State.SignInLeadText);
        Assert.NotEmpty(h.Flow.State.CodeRouteLabel);
    }

    [Fact]
    public async Task TheCodeRouteIsAlwaysOnePressAway()
    {
        // Local pairing is a legitimate choice, not a fallback being grudgingly allowed. Somebody who does
        // not want an account connected reaches the form in one press and without being argued with.
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        h.Flow.RevealCodeRoute();

        Assert.False(h.Flow.State.SignInLeads);
        Assert.True(h.Flow.State.CodeEntryShown);
    }

    [Fact]
    public async Task OnceTheCodeFormIsUp_TheOfferIsTheQuietOneBesideTheField()
    {
        // Two offers, never both, because they say different things: the lead one promises no code to fetch,
        // which would be a contradiction sitting above a box asking for a code.
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        Assert.Empty(h.Flow.State.SignInInvitation);

        h.Flow.RevealCodeRoute();

        Assert.NotEmpty(h.Flow.State.SignInInvitation);
        Assert.False(h.Flow.State.SignInLeads);
    }

    [Fact]
    public async Task SignedIn_TheFormIsNotWithheld()
    {
        // Nothing to defer: the account supplies the id, so the form is only ever the code box and there is
        // no work to spare anybody by hiding it.
        var account = new FakeAccountSession { Current = new AccountIdentity("4200000000000000042", "somebody", "GB") };
        var h = new Harness(account: account);

        await h.ToLinkViaScanAsync();

        Assert.False(h.Flow.State.SignInLeads);
        Assert.True(h.Flow.State.CodeEntryShown);
    }

    [Fact]
    public async Task AlreadySignedIn_NothingInvitesThemToSignInAgain()
    {
        var account = new FakeAccountSession { Current = new AccountIdentity("4200000000000000042", "somebody", "GB") };
        var h = new Harness(account: account);

        await h.ToLinkViaScanAsync();

        Assert.Empty(h.Flow.State.SignInInvitation);
    }

    // ---- signing in without leaving the flow -----------------------------------------------------
    //
    // The invitation used to navigate to the settings page, because that is where the sign-in sequence was
    // written. Reported from hardware: sign-in completes and you are standing on a settings page with the
    // pairing flow abandoned behind you, and the way back is to start pairing over. Sign-in is now a modal over
    // this step, so what is tested is that the step re-aims itself when told the account arrived.

    [Fact]
    public async Task SigningInMidFlow_TurnsTheStepOverWithoutLeavingIt()
    {
        var account = new FakeAccountSession();
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync();

        Assert.True(h.Flow.State.SignInLeads);

        // What the dialog does, as far as this flow can see: the session now has an identity.
        account.Current = new AccountIdentity("4200000000000000042", "somebody", "GB");
        h.Flow.AccountSignInFinished();

        // The offer is spent and the form appears with the id already answered. Nothing navigated.
        Assert.False(h.Flow.State.SignInLeads);
        Assert.True(h.Flow.State.CodeEntryShown);
        Assert.True(h.Flow.State.AccountIdIsAutomatic);
        Assert.Empty(h.Flow.State.SignInInvitation);
        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
    }

    [Fact]
    public async Task SigningInMidFlow_WhenTheAccountKnowsTheConsole_DropsTheCodeEntirely()
    {
        // The whole point of signing in here. The console-list lookup is skipped while there is no account to
        // ask, so being told about the sign-in has to start it — otherwise the account route stays unavailable
        // until something unrelated moves, and the user types a code they did not need.
        var account = new FakeAccountSession();
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.Equal(PairingRoute.Code, h.Flow.State.Route);

        account.Current = new AccountIdentity("4200000000000000042", "somebody", "GB");
        account.Consoles = [new CloudConsole("duid-living-room", "PS5-8A2F", true, true)];
        h.Flow.AccountSignInFinished();

        Assert.True(await WaitUntil(() => h.Flow.State.Route == PairingRoute.Account));
        Assert.True(h.Flow.State.CanPairWithAccount);
    }

    [Fact]
    public async Task ASignInThatCouldNotRun_SaysSoOnTheStepThatAskedForIt()
    {
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        h.Flow.AccountSignInFinished("Couldn't open the sign-in page.");

        Assert.Equal("Couldn't open the sign-in page.", h.Flow.State.SignInError);

        // Still offered, because the failure was ours and retrying is reasonable.
        Assert.True(h.Flow.State.SignInLeads);
    }

    [Fact]
    public async Task ACancelledSignIn_LeavesNothingBehind()
    {
        // Closing a window you opened is not an error. Reporting it would leave a red bar on the step for
        // somebody who simply changed their mind.
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        h.Flow.AccountSignInFinished("Couldn't open the sign-in page.");
        h.Flow.AccountSignInFinished();

        Assert.Empty(h.Flow.State.SignInError);
        Assert.True(h.Flow.State.SignInLeads);
    }

    [Fact]
    public async Task ABuildWithNoAccountTier_InvitesNobodyToSignIn()
    {
        // Offering a sign-in this build cannot perform would be worse than saying nothing: it names a way out
        // that does not exist, on the one screen somebody is already stuck on.
        var h = new Harness(withAccountPairing: false);

        await h.ToLinkViaScanAsync();

        Assert.Empty(h.Flow.State.SignInInvitation);
        Assert.False(h.Flow.State.SignInLeads);

        // And the form is there immediately: with no account to offer, withholding it would be a step that
        // shows nothing and offers nothing.
        Assert.True(h.Flow.State.CodeEntryShown);
    }

    [Fact]
    public async Task WhenSignedIn_TheAccountIdIsSuppliedRatherThanTyped()
    {
        // The point of the whole account tier for a LAN-only user: the account id was previously something you
        // had to go and look up somewhere else before you could pair at all.
        var account = new FakeAccountSession { Current = new AccountIdentity("4200000000000000042", "somebody", "GB") };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync();

        h.Flow.SetLinkInput("12345678", string.Empty);

        Assert.True(h.Flow.State.CanPair);
        Assert.True(h.Flow.State.AccountIdIsAutomatic);
        Assert.Contains("somebody", h.Flow.State.AccountIdNote);

        await h.Flow.PairAsync();

        Assert.Equal("4200000000000000042", h.Registrar.LastRegistration?.AccountId);
    }

    [Fact]
    public async Task WhenSignedIn_AnEmptyAccountBoxDoesNotClobberTheSignedInId()
    {
        // The front end hides the field when signed in, but it still raises change events as the panel is built.
        // Honouring those would leave Pair disabled with nothing on screen explaining why.
        var account = new FakeAccountSession { Current = new AccountIdentity("99", "somebody", "GB") };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync();

        h.Flow.SetLinkInput("12345678", "1234567890123456");
        h.Flow.SetLinkInput("12345678", string.Empty);

        Assert.True(h.Flow.State.CanPair);
        await h.Flow.PairAsync();
        Assert.Equal("99", h.Registrar.LastRegistration?.AccountId);
    }

    [Fact]
    public async Task WhenSignedOut_TheTypedAccountIdIsStillUsed()
    {
        // Pairing by hand stays first-class: a build with no credential has no other path.
        var h = new Harness(account: new FakeAccountSession { CanSignIn = false });
        await h.ToLinkViaScanAsync();

        h.Flow.SetLinkInput("12345678", "1234567890123456");

        Assert.False(h.Flow.State.AccountIdIsAutomatic);
        await h.Flow.PairAsync();
        Assert.Equal("1234567890123456", h.Registrar.LastRegistration?.AccountId);
    }

    [Fact]
    public async Task WithNoAccountSeamAtAll_BehavesExactlyAsBefore()
    {
        // The seam is optional, and its absence must change nothing.
        var h = new Harness();
        await h.ToLinkViaScanAsync();

        h.Flow.SetLinkInput("12345678", "1234567890123456");

        Assert.False(h.Flow.State.AccountIdIsAutomatic);
        await h.Flow.PairAsync();
        Assert.Equal("1234567890123456", h.Registrar.LastRegistration?.AccountId);
    }

    [Fact]
    public async Task SigningInWhileTheFlowIsOpen_TakesEffectWithoutResubscribing()
    {
        // The effective id is derived at read time rather than captured on entry, so a sign-in that happens on
        // another surface while this flow sits on the link step is picked up without the flow observing anything.
        var account = new FakeAccountSession();
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync();
        h.Flow.SetLinkInput("12345678", string.Empty);
        Assert.False(h.Flow.State.CanPair);

        account.Current = new AccountIdentity("55", "somebody", "GB");
        h.Flow.SetLinkInput("12345678", string.Empty);

        Assert.True(h.Flow.State.CanPair);
        Assert.True(h.Flow.State.AccountIdIsAutomatic);
    }

    [Fact]
    public async Task PairingWhileSignedIn_LearnsTheCloudIdSoTheConsoleCanBeWokenRemotelyLater()
    {
        // The one moment we have the console's local name and the account's list side by side.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("42", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-living-room", "PS5-8A2F", true, true)],
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SetLinkInput("12345678", string.Empty);
        await h.Flow.PairAsync();
        h.Flow.Finish("Living room", connect: false);

        Assert.Equal("duid-living-room", h.Completions.Single().Console.CloudDeviceId);
    }

    [Fact]
    public async Task PairingWithAnAmbiguousCloudName_StoresNoCloudIdRatherThanGuessing()
    {
        // Two consoles genuinely called the same thing is exactly why nicknames exist. A wrong id here would
        // send a wake to the other console.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("42", "somebody", "GB"),
            Consoles =
            [
                new CloudConsole("duid-a", "PS5-8A2F", true, true),
                new CloudConsole("duid-b", "PS5-8A2F", true, true),
            ],
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SetLinkInput("12345678", string.Empty);
        await h.Flow.PairAsync();
        h.Flow.Finish("Living room", connect: false);

        Assert.Null(h.Completions.Single().Console.CloudDeviceId);
    }

    [Fact]
    public async Task PairingWhenTheCloudLookupFails_StillPairs()
    {
        // Enrichment is optional. A user pairing a console on their own sofa must not see a cloud error.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("42", "somebody", "GB"),
            ListThrows = new InvalidOperationException("cloud down"),
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SetLinkInput("12345678", string.Empty);
        await h.Flow.PairAsync();
        h.Flow.Finish("Living room", connect: false);

        AddConsoleCompletion completion = h.Completions.Single();
        Assert.Null(completion.Console.CloudDeviceId);
        Assert.Equal("Living room", completion.Console.Nickname);
    }

    // ---- pairing through the account ------------------------------------------------------------
    //
    // The second route to a paired console: the console delivers the registration seed over the account service
    // instead of showing an eight-digit code. What is tested here is the decision — when it is offered, when it
    // is refused, and what the user is told — because the exchange itself belongs to the seam.

    /// <summary>Signed in, with the console present in the account's list — the case the route exists for.</summary>
    private static FakeAccountSession SignedInKnowing(string name = "PS5-8A2F", string duid = "duid-living-room")
        => new()
        {
            Current = new AccountIdentity("4200000000000000042", "somebody", "GB"),
            Consoles = [new CloudConsole(duid, name, true, true)],
        };

    // ---- the account's own flags on the console ---------------------------------------------------
    //
    // The record carries two, and the step used to ignore both: it offered the account route because the
    // console appeared in the list, which says only that the account has seen it. Getting this wrong is
    // expensive and silent - sixty seconds of waiting, then a message about a registration seed for a console
    // that was never going to answer. The two are not the same KIND of fact, which is the thing these pin.

    [Fact]
    public async Task AccountPairing_WhenRemotePlayIsOffOnTheConsole_IsRefusedAndSaysWhere()
    {
        // Fatal, and fixable on the console. Offering the route here would be offering something that cannot
        // work, so the step does not aim at it either.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("4200000000000000042", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-living-room", "PS5-8A2F", RemotePlayEnabled: false, CanWakeRemotely: true)],
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.Equal(PairingRoute.Code, h.Flow.State.Route);
        Assert.Contains("Remote Play is switched off", h.Flow.State.AccountPairingNote);

        // Caution, not an error tone: a setting being off is not a fault. See StatusTone.Neutral's note.
        Assert.Equal(StatusTone.Caution, h.Flow.State.AccountPairingNoteTone);
    }

    [Fact]
    public async Task AccountPairing_WhenTheConsoleCannotBeWoken_IsStillOfferedWithTheCondition()
    {
        // A condition, not a refusal: an awake console pairs normally, so refusing would take away a route
        // that works. What the user needs is to know it before pressing Pair rather than after two timeouts.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("4200000000000000042", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-living-room", "PS5-8A2F", RemotePlayEnabled: true, CanWakeRemotely: false)],
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.CanPairWithAccount);
        Assert.Equal(PairingRoute.Account, h.Flow.State.Route);
        Assert.Contains("Turn the console on first", h.Flow.State.AccountPairingNote);

        // And still says the code is unnecessary, because that is also true and is why they chose this route.
        Assert.Contains("No code needed", h.Flow.State.AccountPairingNote);

        // NOT Positive. A green bar carrying an instruction reads as the opposite of its own sentence.
        Assert.Equal(StatusTone.Caution, h.Flow.State.AccountPairingNoteTone);
    }

    [Fact]
    public async Task AccountPairing_WithBothFlagsSet_CarriesNoCondition()
    {
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.CanPairWithAccount);
        Assert.Contains("No code needed", h.Flow.State.AccountPairingNote);
        Assert.DoesNotContain("Turn the console on first", h.Flow.State.AccountPairingNote);
        Assert.Equal(StatusTone.Positive, h.Flow.State.AccountPairingNoteTone);
    }

    [Fact]
    public async Task AccountPairing_WhenTheAccountDoesNotKnowTheConsole_TheFlagsClaimNothing()
    {
        // The flags default to true for an unknown console, so that the note reports the thing that is
        // actually true - the console is not in the list - rather than inventing a fact about a record that
        // does not exist.
        var account = new FakeAccountSession
        {
            Current = new AccountIdentity("4200000000000000042", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-other", "Some other console", true, true)],
        };
        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.DoesNotContain("Remote Play is switched off", h.Flow.State.AccountPairingNote);
        Assert.Contains("isn't in your account's console list", h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task AccountPairing_WhenSignedOut_IsNotOfferedAtAll()
    {
        // The route IS the account. With nobody signed in there is nothing to offer, and an affordance that
        // explains itself by being disabled is worse here than one that is simply absent — the link step already
        // says signing in fills the account id in.
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        Assert.False(h.Flow.State.AccountPairingOffered);
        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.Equal(string.Empty, h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task AccountPairing_WithNoSeamAtAll_IsNotOffered()
    {
        // A front end that composed no account pairing must behave exactly as the flow did before it existed,
        // even for a signed-in user.
        var h = new Harness(account: SignedInKnowing(), withAccountPairing: false);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.False(h.Flow.State.AccountPairingOffered);
        Assert.False(h.Flow.State.CanPairWithAccount);
    }

    // ---- the route choice -------------------------------------------------------------------------
    //
    // The link step used to show both routes at once: "no code needed", then how to find a code on the
    // console, then a box asking for one, with two commit buttons and nothing to say which belonged to what.

    [Fact]
    public async Task WhenBothRoutesWork_TheChoiceIsOfferedAndDefaultsToTheAccount()
    {
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.RouteChoiceOffered);
        Assert.Equal(PairingRoute.Account, h.Flow.State.Route);

        // And the code half of the step is not shown at all, which is the contradiction being removed.
        Assert.False(h.Flow.State.CodeEntryShown);
        Assert.Contains("No code needed", h.Flow.State.AccountPairingNote);
        Assert.Equal("Pair with my account", h.Flow.State.PairActionLabel);

        // Committable with no code typed -- the account route never asked for one.
        Assert.True(h.Flow.State.CanPair);
    }

    [Fact]
    public async Task ChoosingTheCodeRoute_ShowsTheCodeHalfAndDropsTheNoCodePromise()
    {
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SelectRoute(PairingRoute.Code);

        Assert.True(h.Flow.State.CodeEntryShown);
        Assert.Equal("Pair with a code", h.Flow.State.PairActionLabel);

        // "No code needed" must not survive next to a box asking for a code.
        Assert.Equal(string.Empty, h.Flow.State.AccountPairingNote);

        // And it is not committable until a code is actually entered.
        Assert.False(h.Flow.State.CanPair);
    }

    [Fact]
    public async Task WhenOnlyTheCodeRouteWorks_NoChoiceIsPutInFrontOfTheUser()
    {
        // A console the account has never seen. Asking a question with one answer is worse than not asking.
        var h = new Harness(account: SignedInKnowing(name: "PS5-SOMEWHERE-ELSE"));
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.False(h.Flow.State.RouteChoiceOffered);
        Assert.Equal(PairingRoute.Code, h.Flow.State.Route);
        Assert.True(h.Flow.State.CodeEntryShown);

        // The reason the account route is unavailable still shows -- that explains the absent choice.
        Assert.NotEqual(string.Empty, h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task SwitchingToTheAccountRoute_ForgetsAnAbandonedCode()
    {
        // Otherwise switching back would silently commit digits the user had walked away from.
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SelectRoute(PairingRoute.Code);
        h.Flow.SetLinkInput("12345678", "");
        Assert.True(h.Flow.State.CanPair);

        h.Flow.SelectRoute(PairingRoute.Account);
        h.Flow.SelectRoute(PairingRoute.Code);

        Assert.False(h.Flow.State.CanPair);
    }

    [Fact]
    public async Task AChosenRouteIsNotOverriddenByTheDefault()
    {
        // The account capability resolves asynchronously; a user who has started on the code route must not
        // have the step change under them when it lands.
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        h.Flow.SelectRoute(PairingRoute.Code);
        h.Flow.SetLinkInput("1234", "");

        Assert.Equal(PairingRoute.Code, h.Flow.State.Route);
        Assert.True(h.Flow.State.CodeEntryShown);
    }

    [Fact]
    public async Task AccountPairing_SignedInAndConsoleKnown_IsOfferedAndInvited()
    {
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.AccountPairingOffered);
        Assert.True(h.Flow.State.CanPairWithAccount);
        Assert.Contains("No code needed", h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task AccountPairing_ConsoleTheAccountDoesNotList_IsOfferedButRefusedWithTheFix()
    {
        // Offered rather than hidden, because the user IS signed in and would otherwise be left wondering why
        // the thing they read about is missing. The note is the whole value of the state: the fix is on the
        // console, not in this app.
        var h = new Harness(account: SignedInKnowing(name: "PS5-SOMEWHERE-ELSE"));
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.AccountPairingOffered);
        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.Contains("isn't in your account's console list", h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task AccountPairing_WhenTheBuildCannot_SaysWhyRatherThanOffering()
    {
        var h = new Harness(account: SignedInKnowing());
        h.AccountPairing.Available = false;
        h.AccountPairing.AvailabilityDetail = "registration constants not found (set RIPCORD_REGIST_FIXTURE)";

        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Assert.True(h.Flow.State.AccountPairingOffered);
        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.Equal("registration constants not found (set RIPCORD_REGIST_FIXTURE)", h.Flow.State.AccountPairingNote);
    }

    [Fact]
    public async Task AccountPairing_AvailabilityIsAskedForTheConsolesOwnFamily()
    {
        // The console reports what it actually is, and its family decides which constants are needed. Asking
        // about the family the user guessed on the first step would answer for the wrong console — and that
        // assignment happens inside the same mutation, so this is easy to get wrong.
        var h = new Harness(account: SignedInKnowing());
        h.Scanner.Yields(Console("10.0.0.7", ConsolePlatform.HalyardLegacy, name: "PS4-8A2F"));
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        h.Flow.SelectDiscovered(h.Flow.Discovered.Single());

        Assert.Equal(ConsoleFamily.Ps4, h.AccountPairing.AvailabilityAskedFor);
    }

    [Fact]
    public async Task AccountPairing_AvailabilityThrowing_LeavesTheCodeRouteWorking()
    {
        // A backend that fails while answering must not take the local route down with it.
        var h = new Harness(account: SignedInKnowing());
        h.AccountPairing.AvailabilityThrows = new InvalidOperationException("constants store on fire");

        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));
        h.Flow.SetLinkInput("12345678", string.Empty);

        Assert.False(h.Flow.State.CanPairWithAccount);
        Assert.Contains("constants store on fire", h.Flow.State.AccountPairingNote);

        Assert.True(h.Flow.State.CanPair);
        await h.Flow.PairAsync();
        Assert.Equal(AddConsoleStep.Done, h.Flow.State.Step);
    }

    [Fact]
    public async Task PairWithAccount_NeedsNoCode_AndCarriesWhatTheSeamAsksFor()
    {
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        // Nothing typed, and committable anyway: CanPair is route-aware now, and the account route never asks
        // for a code. It used to mean "the code route is ready", which is why this asserted the opposite --
        // gating the account button on eight digits nobody was asked for is how a working action arrives
        // disabled. What says no code is wanted is that the code half of the step is not shown.
        Assert.True(h.Flow.State.CanPair);
        Assert.False(h.Flow.State.CodeEntryShown);

        await h.Flow.PairWithAccountAsync();

        Assert.Equal(AddConsoleStep.Done, h.Flow.State.Step);
        Assert.Equal(0, h.Registrar.RegisterCalls);

        AccountPairingRequest request = Assert.IsType<AccountPairingRequest>(h.AccountPairing.LastRequest);
        Assert.Equal("10.0.0.7", request.Host);
        Assert.Equal("4200000000000000042", request.AccountId);
        Assert.Equal("duid-living-room", request.CloudDeviceId);
        Assert.Equal(ConsoleFamily.Ps5, request.Family);
    }

    [Fact]
    public async Task PairWithAccount_SavesTheSameShapeOfConsoleAsTheCodeRoute()
    {
        // Where the pairing record came from is the seam's business. What is stored — identity, reported name,
        // cloud id — must not depend on the route.
        var h = new Harness(account: SignedInKnowing());
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        await h.Flow.PairWithAccountAsync();
        h.Flow.Finish("Living room", connect: false);

        PairedConsole saved = h.Completions.Single().Console;
        Assert.Equal("duid-living-room", saved.CloudDeviceId);
        Assert.Equal("PS5-8A2F", saved.ReportedName);
        Assert.Equal("10.0.0.7", saved.Host);
        Assert.Equal("Living room", saved.Nickname);
    }

    [Fact]
    public async Task PairWithAccount_SaysWhatItIsWaitingFor()
    {
        // The progress text is the only thing on the pairing panel, and the two routes wait on different things
        // — one on the user's code reaching the console, one on the console answering the account service.
        var h = new Harness(account: SignedInKnowing());
        h.AccountPairing.HangForever = true;
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        Task pairing = h.Flow.PairWithAccountAsync();

        Assert.Equal(AddConsoleStep.Pairing, h.Flow.State.Step);
        Assert.Contains("through your account", h.Flow.State.PairingStatus);

        await h.Flow.DisposeAsync();
        await pairing;
    }

    [Fact]
    public async Task PairWithAccount_AfterAFailure_TheNextCodeRouteAttemptSaysTheOrdinaryThing()
    {
        // The route flag is state, and stale state on a progress panel is how a user ends up reading that the
        // app is waiting on their account when it is waiting on their eight digits.
        var h = new Harness(account: SignedInKnowing());
        h.AccountPairing.Result = new ConsoleRegistrationResult(false, "the console said no", null);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        await h.Flow.PairWithAccountAsync();
        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Equal("the console said no", h.Flow.State.LinkError);

        h.Registrar.HangForever = true;
        h.EnterValidLinkInput();
        Task pairing = h.Flow.PairAsync();

        Assert.Contains("Registering with", h.Flow.State.PairingStatus);

        await h.Flow.DisposeAsync();
        await pairing;
    }

    [Fact]
    public async Task PairWithAccount_Timeout_ReportsTheAccountRoutesOwnMessage()
    {
        var h = new Harness(
            options: new AddConsoleFlowOptions { AccountPairingTimeout = TimeSpan.FromMilliseconds(20) },
            account: SignedInKnowing());
        h.AccountPairing.HangForever = true;
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        await h.Flow.PairWithAccountAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Contains("through your account", h.Flow.State.LinkError);
    }

    [Fact]
    public async Task PairWithAccount_ThrowingSeam_IsReportedRatherThanEscaping()
    {
        var h = new Harness(account: SignedInKnowing());
        h.AccountPairing.Throws = new InvalidOperationException("push upgrade rejected");
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));

        await h.Flow.PairWithAccountAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Contains("push upgrade rejected", h.Flow.State.LinkError);
    }

    [Fact]
    public async Task PairWithAccount_WhenNotOffered_DoesNothing()
    {
        var h = new Harness(account: new FakeAccountSession());
        await h.ToLinkViaScanAsync();

        await h.Flow.PairWithAccountAsync();

        Assert.Equal(0, h.AccountPairing.PairCalls);
        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
    }

    [Fact]
    public async Task AccountPairing_CloudListArrivingAfterTheUserHasMovedOn_StillEnablesIt()
    {
        // The list is fetched alongside the scan, so it can land while the user is already on the link step.
        // That write has to go through the state machine, or the button stays greyed out until something
        // unrelated recomposes — which is what happens when a field is assigned from a continuation instead.
        var account = SignedInKnowing();
        var gate = new TaskCompletionSource();
        account.ListGate = gate;

        var h = new Harness(account: account);
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "PS5-8A2F"));
        Assert.False(h.Flow.State.CanPairWithAccount);

        gate.SetResult();

        Assert.True(await WaitUntil(() => h.Flow.State.CanPairWithAccount));
    }

    [Fact]
    public async Task Pair_WithoutValidInput_DoesNothing()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();

        await h.Flow.PairAsync();

        Assert.Equal(0, h.Registrar.RegisterCalls);
        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
    }

    [Fact]
    public async Task Pair_CipherUnavailable_StaysOnLink_AndNeverShowsThePairingStep()
    {
        // Flashing a progress panel the user cannot act on is worse than telling them on the step they are on.
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.Available = false;
        h.Registrar.AvailabilityDetail = "fixture lacks PS4 registration constants";

        await h.Flow.PairAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Contains("fixture lacks PS4", h.Flow.State.LinkError);
        Assert.Equal(0, h.Registrar.RegisterCalls);
    }

    [Fact]
    public async Task Pair_ChecksAvailabilityForTheConsolesOwnFamily()
    {
        var h = new Harness();
        h.Scanner.Yields(Console("10.0.0.8", ConsolePlatform.HalyardLegacy));
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        h.Flow.SelectDiscovered(h.Flow.Discovered.Single());
        h.EnterValidLinkInput();

        await h.Flow.PairAsync();

        Assert.Equal(ConsoleFamily.Ps4, h.Registrar.AvailabilityAskedFor);
    }

    [Fact]
    public async Task Pair_Rejected_ReturnsToLinkWithTheConsolesReason()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.Result = new ConsoleRegistrationResult(false, "403 rejected", null);

        await h.Flow.PairAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Equal("403 rejected", h.Flow.State.LinkError);
    }

    [Fact]
    public async Task Pair_Throwing_ReturnsToLinkRatherThanStranding()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.Throws = new InvalidOperationException("connection reset");

        await h.Flow.PairAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Contains("connection reset", h.Flow.State.LinkError);
    }

    [Fact]
    public async Task Pair_TimingOut_ReportsItOnLink()
    {
        var h = new Harness(new AddConsoleFlowOptions { RegistrationTimeout = TimeSpan.FromMilliseconds(20) });
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.HangForever = true;

        await h.Flow.PairAsync();

        Assert.Equal(AddConsoleStep.Link, h.Flow.State.Step);
        Assert.Contains("didn't answer in time", h.Flow.State.LinkError);
    }

    [Fact]
    public async Task Pair_SendsWhatTheUserTyped()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.Flow.SetLinkInput(" 87654321 ", " 9876543210 ");

        await h.Flow.PairAsync();

        Assert.Equal("87654321", h.Registrar.LastRegistration!.Passcode);
        Assert.Equal("9876543210", h.Registrar.LastRegistration.AccountId);
        Assert.Equal("10.0.0.7", h.Registrar.LastRegistration.Host);
    }

    [Fact]
    public async Task Back_DuringPairing_IsRefused()
    {
        // The console is mid-registration and has consumed a link code; walking out wastes it.
        var h = new Harness(new AddConsoleFlowOptions { RegistrationTimeout = TimeSpan.FromSeconds(30) });
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.HangForever = true;

        Task pair = h.Flow.PairAsync();
        Assert.Equal(AddConsoleStep.Pairing, h.Flow.State.Step);
        Assert.False(h.Flow.State.CanGoBack);

        Assert.True(await h.Flow.BackAsync());
        Assert.Equal(AddConsoleStep.Pairing, h.Flow.State.Step);

        await h.Flow.DisposeAsync();
        await pair;
    }

    [Fact]
    public async Task DisposeAsync_MidPairing_CancelsTheExchange()
    {
        // The leak: the page's cancellation source was a timeout, not an ownership handle.
        var h = new Harness(new AddConsoleFlowOptions { RegistrationTimeout = TimeSpan.FromMinutes(5) });
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        h.Registrar.HangForever = true;

        Task pair = h.Flow.PairAsync();
        await h.Flow.DisposeAsync();
        await pair;   // completes promptly rather than hanging for five minutes

        // And it does not push an error onto a state nobody is looking at.
        Assert.Null(h.Flow.State.LinkError);
    }

    // ---- done step -----------------------------------------------------------------------------

    [Fact]
    public async Task Pair_Success_ReachesDoneWithASuggestedName()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();

        await h.Flow.PairAsync();

        Assert.Equal(AddConsoleStep.Done, h.Flow.State.Step);

        // Three dashes now, not four - the mark's own trail rather than a progress bar.
        Assert.Equal(3, h.Flow.State.ReachedDash);
        Assert.Equal("PS5-8A2F", h.Flow.State.SuggestedName);
        Assert.Contains("won't need the code again", h.Flow.State.DoneSubtext);

        // Stored ALREADY, which is the point of the step being allowed to say "Paired."
        Assert.Equal("PS5-8A2F", h.Store.Load().Single().DisplayName);
    }

    [Fact]
    public async Task Pair_Success_SurvivesWalkingAwayFromTheCelebration()
    {
        // The step says the console is linked to this PC. It used to be saying that about something only in
        // memory - Finish did the storing - so closing the app at the celebration lost it. And the loss is not
        // symmetric: the console HAS registered, so it believes the pairing exists while we no longer hold the
        // credential, and recovering means fetching a fresh code off its screen.
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();

        await h.Flow.PairAsync();
        await h.Flow.DisposeAsync();          // the user closed the app, or navigated away

        Assert.Equal("PS5-8A2F", h.Store.Load().Single().DisplayName);
    }

    [Fact]
    public async Task Finish_WithoutARename_DoesNotStoreASecondTime()
    {
        // Finish is a way out and a rename, not the save. Accepting the prefilled name must not pin it as a
        // nickname - a pinned name stops tracking the console if the console is ever renamed.
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("PS5-8A2F", connect: false);

        PairedConsole stored = h.Store.Load().Single();
        Assert.True(string.IsNullOrEmpty(stored.Nickname), $"nickname was pinned to '{stored.Nickname}'");
        Assert.Equal("PS5-8A2F", stored.DisplayName);
    }

    [Fact]
    public async Task Finish_WithARename_UpdatesTheStoredRecord()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("Living room", connect: false);

        Assert.Equal("Living room", h.Store.Load().Single().Nickname);
        Assert.Single(h.Store.Load());
    }

    [Fact]
    public async Task Pair_Success_PrefersTheConsolesOwnIdOverItsAddress()
    {
        // A DHCP lease can move the address out from under us; the host-id cannot.
        var h = new Harness();
        await h.ToLinkViaScanAsync(Console("10.0.0.7", id: "stable-host-id"));
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish(h.Flow.State.SuggestedName, connect: false);

        PairedConsole saved = h.Store.Load().Single();
        Assert.Equal("stable-host-id", saved.Id);
        Assert.Equal("stable-host-id", saved.HostId);
        Assert.Equal("10.0.0.7", saved.Host);
    }

    [Fact]
    public async Task Pair_Success_FallsBackToTheAddressForAHandTypedHost()
    {
        var h = new Harness();
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);
        h.Flow.UseTypedAddress("10.0.0.50");
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("", connect: false);

        PairedConsole saved = h.Store.Load().Single();
        Assert.Equal("10.0.0.50", saved.Id);
        Assert.Null(saved.HostId);
    }

    [Fact]
    public async Task Pair_Success_CarriesWhatTheConsoleReported()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync(Console("10.0.0.7", name: "Front room PS5", systemVersion: "10.01"));
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish(h.Flow.State.SuggestedName, connect: false);

        PairedConsole saved = h.Store.Load().Single();
        Assert.Equal("Front room PS5", saved.ReportedName);
        Assert.Equal("10.01", saved.SystemVersion);
        Assert.Equal("Ps5", saved.Platform);
    }

    [Fact]
    public async Task Pair_Success_StoresTheCredentialWrapped_NotPlaintext()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("", connect: false);

        PairedConsole saved = h.Store.Load().Single();
        Assert.StartsWith(PairedConsoleBlob.ProtectedPrefix, saved.CredentialBlob);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, h.Store.DecodeBlob(saved.CredentialBlob));
    }

    [Fact]
    public async Task Finish_AcceptingThePrefilledName_DoesNotPinANickname()
    {
        // Otherwise the name stops tracking the console if the console is ever renamed.
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish(h.Flow.State.SuggestedName, connect: false);

        Assert.Null(h.Store.Load().Single().Nickname);
    }

    [Fact]
    public async Task Finish_ADifferentName_IsPinnedAsANickname()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("Front room", connect: false);

        Assert.Equal("Front room", h.Store.Load().Single().Nickname);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finish_RaisesCompletedWithTheConnectIntent(bool connect)
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        h.EnterValidLinkInput();
        await h.Flow.PairAsync();

        h.Flow.Finish("", connect);

        AddConsoleCompletion completion = Assert.Single(h.Completions);
        Assert.Equal(connect, completion.ConnectNow);
        Assert.Equal("10.0.0.7", completion.Console.Host);
    }

    [Fact]
    public async Task Finish_BeforePairing_DoesNothing()
    {
        var h = new Harness();
        await h.ToLinkViaScanAsync();

        h.Flow.Finish("whatever", connect: true);

        Assert.Empty(h.Completions);
        Assert.Empty(h.Store.Load());
    }

    // ---- back ----------------------------------------------------------------------------------

    [Fact]
    public async Task Back_FromFind_ReturnsToFamilyAndStopsScanning()
    {
        var h = new Harness();
        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.True(await h.Flow.BackAsync());

        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);
        Assert.False(h.Flow.State.IsScanning);
    }

    [Fact]
    public async Task Back_FromLink_KeepsTheResultsAlreadyFound()
    {
        // The page restarted the search here, throwing away what the user had just waited four seconds for.
        var h = new Harness();
        await h.ToLinkViaScanAsync();
        int scansBefore = h.Scanner.ScanCount;

        Assert.True(await h.Flow.BackAsync());

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
        Assert.Single(h.Flow.Discovered);
        Assert.Equal(scansBefore, h.Scanner.ScanCount);
    }

    [Fact]
    public async Task Back_FromTheFirstStep_ReportsThatTheFlowShouldBeLeft()
    {
        var h = new Harness();
        Assert.False(await h.Flow.BackAsync());
    }

    // ---- construction --------------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var scanner = new FakeScanner();
        var registrar = new FakeRegistrar();
        var store = new InMemoryPairedConsoleStore();
        var dispatcher = new ImmediateUiDispatcher();

        Assert.Throws<ArgumentNullException>(() => new AddConsoleFlow(null!, registrar, store, dispatcher));
        Assert.Throws<ArgumentNullException>(() => new AddConsoleFlow(scanner, null!, store, dispatcher));
        Assert.Throws<ArgumentNullException>(() => new AddConsoleFlow(scanner, registrar, null!, dispatcher));
    }
}
