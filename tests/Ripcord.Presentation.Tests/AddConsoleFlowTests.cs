using System.Net;
using Ripcord.Core;
using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
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
        public InMemoryPairedConsoleStore Store { get; } = new();
        public AddConsoleFlow Flow { get; }
        public List<AddConsoleCompletion> Completions { get; } = [];

        public Harness(AddConsoleFlowOptions? options = null)
        {
            Flow = new AddConsoleFlow(Scanner, Registrar, Store, new ImmediateUiDispatcher(), options);
            Flow.Completed += Completions.Add;
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

    // ---- family step ---------------------------------------------------------------------------

    [Fact]
    public async Task StartsOnTheFamilyStep()
    {
        var h = new Harness();
        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);
        Assert.Equal(1, h.Flow.State.ReachedDash);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnsupportedFamily_ShowsTheCaveatAndGoesNoFurther()
    {
        var h = new Harness();

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Xbox);

        Assert.Equal(AddConsoleStep.Family, h.Flow.State.Step);
        Assert.NotNull(h.Flow.State.FamilyNote);
        Assert.Equal(0, h.Scanner.ScanCount);
    }

    [Fact]
    public async Task SupportedFamily_AdvancesAndScans()
    {
        var h = new Harness();

        await h.Flow.SelectFamilyAsync(ConsoleFamily.Ps5);

        Assert.Equal(AddConsoleStep.Find, h.Flow.State.Step);
        Assert.Equal(2, h.Flow.State.ReachedDash);
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
        var h = new Harness();
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
    public async Task SelectDiscovered_MidScan_StopsShowingTheScanAsRunning()
    {
        // The scan is abandoned, not finished, so its own completion path never runs — but the user has left the
        // Find step and nothing is scanning any more. A stale IsScanning leaves the progress bar spinning and
        // Search-again disabled the moment they step back to Find.
        var h = new Harness();
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
        Assert.Equal(4, h.Flow.State.ReachedDash);
        Assert.Equal("PS5-8A2F", h.Flow.State.SuggestedName);
        Assert.Contains("won't need the code again", h.Flow.State.DoneSubtext);

        // Nothing is stored until the user confirms.
        Assert.Empty(h.Store.Load());
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
