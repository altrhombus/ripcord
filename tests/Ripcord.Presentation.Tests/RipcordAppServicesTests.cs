using Ripcord.Core.Consoles;
using Ripcord.Core.Discovery;
using Ripcord.Core.Platform;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The composition root's own contract.
///
/// <para>
/// Small, but not ceremonial: the graph replaced a scheme in which each surface constructed its own
/// collaborators, and the thing that made that scheme survivable — a page could always fall back on
/// <c>new</c> — is exactly what is gone. What is left is one late-bound member (the shell) and a set of
/// factories, and both have failure modes worth pinning: a shell read too early, and a second shell attached
/// over the first.
/// </para>
/// </summary>
public class RipcordAppServicesTests
{
    private static RipcordAppServices Build()
    {
        // Pointed at a scratch directory, not the real one: SettingsStore reads its file in its constructor,
        // and a test that quietly loads (and could rewrite) the developer's own settings.json is a test that
        // behaves differently on their machine than in CI.
        var paths = new ScratchPaths();

        return new RipcordAppServices
        {
            Dispatcher = new ImmediateUiDispatcher(),
            Paths = paths,
            Settings = new SettingsStore(paths),
            Consoles = new InMemoryPairedConsoleStore(),
            Scanner = new StubScanner(),
            Registrar = new StubRegistrar(),
            ReachabilityProbe = new StubProbe(),
        };
    }

    [Fact]
    public void Shell_BeforeAnythingIsAttached_Throws()
    {
        // The whole argument for the composition root is that a missing dependency becomes a startup failure
        // with a stack trace rather than a button that quietly does nothing. A nullable shell that callers
        // null-check would reproduce the old `App.MainWindow as MainWindow` behaviour exactly.
        RipcordAppServices services = Build();

        Assert.Throws<InvalidOperationException>(() => services.Shell);
    }

    [Fact]
    public void AttachShell_ThenShell_ReturnsIt()
    {
        RipcordAppServices services = Build();
        var shell = new StubShell();

        services.AttachShell(shell);

        Assert.Same(shell, services.Shell);
    }

    [Fact]
    public void AttachShell_WithTheSameShellTwice_IsHarmless()
    {
        // Idempotent rather than strict: re-attaching the same object says nothing has changed, and failing on
        // it would make the graph fragile to a front end that wires defensively.
        RipcordAppServices services = Build();
        var shell = new StubShell();

        services.AttachShell(shell);
        services.AttachShell(shell);

        Assert.Same(shell, services.Shell);
    }

    [Fact]
    public void AttachShell_WithADifferentShell_Throws()
    {
        // A second window is a second graph. Silently rebinding would leave surfaces built against the first
        // shell driving the second one, which is a class of bug with no visible cause.
        RipcordAppServices services = Build();
        services.AttachShell(new StubShell());

        Assert.Throws<InvalidOperationException>(() => services.AttachShell(new StubShell()));
    }

    [Fact]
    public void CreateAddConsoleFlow_ReturnsAFreshFlowEachTime()
    {
        // Per-surface, not shared: two add-console pages must not share one state machine.
        RipcordAppServices services = Build();

        AddConsoleFlow first = services.CreateAddConsoleFlow();
        AddConsoleFlow second = services.CreateAddConsoleFlow();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void CreateConsoleCard_WiresTheGraphsDispatcher()
    {
        // The observable evidence that the card got the graph's dispatcher rather than one of its own: with the
        // immediate dispatcher, a mutation is visible on the very next line.
        RipcordAppServices services = Build();
        var console = new PairedConsole("id", "PS5", "10.0.0.5", "PS5", "blob");

        ConsoleCardViewModel card = services.CreateConsoleCard(console);
        card.IsHighlighted = true;

        Assert.True(card.State.IsHighlighted);
    }

    // ---- stubs ---------------------------------------------------------------------------------

    /// <summary>A throwaway directory, created on first use so nothing is written unless something asks.</summary>
    private sealed class ScratchPaths : IPlatformPaths
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "ripcord-tests", Guid.NewGuid().ToString("n"));

        public string ConfigDirectory => Ensure();

        public string DataDirectory => Ensure();

        public string StateDirectory => Ensure();

        private string Ensure()
        {
            Directory.CreateDirectory(_root);
            return _root;
        }
    }

    private sealed class StubShell : IShellNavigator
    {
        public bool IsStreaming => false;

        public bool IsFullScreen => false;

        public void ShowStream(PairedConsole console)
        {
        }

        public void CloseStream(string? closedConsoleHost = null, bool restRequested = false)
        {
        }

        public void SetFullScreen(bool fullScreen)
        {
        }
    }

    private sealed class StubScanner : IConsoleScanner
    {
        public IObservable<DiscoveredConsole> Scan(TimeSpan window, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubRegistrar : IConsoleRegistrar
    {
        public RegistrarAvailability CheckAvailability(ConsoleFamily family)
            => throw new NotSupportedException();

        public Task<ConsoleRegistrationResult> RegisterAsync(
            ConsoleRegistration registration, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubProbe : IConsoleReachabilityProbe
    {
        public Task<bool?> ProbeAsync(PairedConsole console, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
