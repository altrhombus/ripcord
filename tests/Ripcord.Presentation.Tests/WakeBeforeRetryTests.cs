using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The reconnect that never asked: a console going into rest refused every retry, while a fresh connect found it
/// resting and woke it (2026-10-02).
/// </summary>
public class WakeBeforeRetryTests
{
    private static readonly PairedConsole Console = new("id", "Console", "192.0.2.10", "PS5", "blob");

    private sealed class CountingWake(ConsoleWakeOutcome outcome) : IConsoleWakeCoordinator
    {
        public int Calls { get; private set; }

        public Task<ConsoleWakeOutcome> EnsureAwakeAsync(
            PairedConsole console, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            progress?.Report("Waking your console…");
            return Task.FromResult(outcome);
        }
    }

    private static Func<CancellationToken, Task<IStreamingSession>> Opener(List<string> log)
        => _ =>
        {
            log.Add("open");
            return Task.FromResult<IStreamingSession>(null!);
        };

    [Fact]
    public async Task TheFirstOpen_DoesNotAskAgain()
    {
        // ConnectFlow has just asked.
        var wake = new CountingWake(ConsoleWakeOutcome.AlreadyAwake);
        List<string> log = [];

        await WakeBeforeRetry.Wrap(Opener(log), wake, Console)(CancellationToken.None);

        Assert.Equal(0, wake.Calls);
        Assert.Equal(["open"], log);
    }

    [Fact]
    public async Task EveryRetry_AsksTheConsoleToWakeFirst()
    {
        var wake = new CountingWake(ConsoleWakeOutcome.Woken);
        List<string> log = [];
        List<string> lines = [];
        Func<CancellationToken, Task<IStreamingSession>> open =
            WakeBeforeRetry.Wrap(Opener(log), wake, Console, new SynchronousLines(lines));

        await open(CancellationToken.None);
        await open(CancellationToken.None);
        await open(CancellationToken.None);

        Assert.Equal(2, wake.Calls);
        Assert.Equal(3, log.Count);
        Assert.Equal(["Waking your console…", "Waking your console…"], lines);
    }

    [Fact]
    public async Task AConsoleThatNeverWoke_FailsTheRetryInsteadOfBeingRefused()
    {
        var wake = new CountingWake(ConsoleWakeOutcome.TimedOut);
        List<string> log = [];
        Func<CancellationToken, Task<IStreamingSession>> open = WakeBeforeRetry.Wrap(Opener(log), wake, Console);

        await open(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => open(CancellationToken.None));

        Assert.Equal(["open"], log);
    }

    [Theory]
    [InlineData(ConsoleWakeOutcome.AlreadyAwake)]
    [InlineData(ConsoleWakeOutcome.Unknown)]
    public async Task AnAwakeOrUnknownConsole_IsOpenedAsBefore(ConsoleWakeOutcome outcome)
    {
        var wake = new CountingWake(outcome);
        List<string> log = [];
        Func<CancellationToken, Task<IStreamingSession>> open = WakeBeforeRetry.Wrap(Opener(log), wake, Console);

        await open(CancellationToken.None);
        await open(CancellationToken.None);

        Assert.Equal(2, log.Count);
    }

    private sealed class SynchronousLines(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
