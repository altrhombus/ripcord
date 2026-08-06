using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Exercises the non-WinUI dispatcher. The point is not that anything ships on it yet — it is that
/// <see cref="IUiDispatcher"/> can be satisfied by something other than WinUI's DispatcherQueue. If it could
/// not, the seam would be a rename of one platform's API rather than an abstraction, and the whole extraction
/// would have bought nothing.
/// </summary>
public class SynchronizationContextUiDispatcherTests
{
    /// <summary>A context that queues rather than running inline, standing in for a real message loop.</summary>
    private sealed class QueueingContext : SynchronizationContext
    {
        public List<(SendOrPostCallback Callback, object? State)> Pending { get; } = [];

        public override void Post(SendOrPostCallback d, object? state) => Pending.Add((d, state));

        public void Drain()
        {
            var work = Pending.ToList();
            Pending.Clear();
            foreach ((SendOrPostCallback callback, object? state) in work)
            {
                callback(state);
            }
        }
    }

    [Fact]
    public void Post_MarshalsOntoTheContext_RatherThanRunningInline()
    {
        var context = new QueueingContext();
        var dispatcher = new SynchronizationContextUiDispatcher(context);
        bool ran = false;

        dispatcher.Post(() => ran = true);

        Assert.False(ran);
        Assert.Single(context.Pending);

        context.Drain();

        Assert.True(ran);
    }

    [Fact]
    public void IsOnUiThread_TracksWhetherTheContextIsCurrent()
    {
        var context = new QueueingContext();
        var dispatcher = new SynchronizationContextUiDispatcher(context);

        // Not current here: this test's thread has whatever context xUnit gave it.
        Assert.False(dispatcher.IsOnUiThread);

        SynchronizationContext? saved = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.True(dispatcher.IsOnUiThread);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(saved);
        }
    }

    [Fact]
    public void Constructor_WithNoContextAvailable_FailsLoudly()
    {
        // A thread-pool fallback would look like it worked and then mutate view-model state off the UI thread,
        // which is precisely the bug the single-threaded rule exists to make impossible. Better to refuse.
        SynchronizationContext? saved = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Assert.Throws<InvalidOperationException>(() => new SynchronizationContextUiDispatcher());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(saved);
        }
    }

    [Fact]
    public void Post_RejectsANullAction()
        => Assert.Throws<ArgumentNullException>(
            () => new SynchronizationContextUiDispatcher(new QueueingContext()).Post(null!));
}
