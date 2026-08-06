namespace Ripcord.Presentation.Threading;

/// <summary>
/// Runs everything inline, on the calling thread. For tests, and for any host that has no separate UI thread.
///
/// <para>
/// Shipped here rather than in the test project on purpose: it makes every view-model assertion synchronous, so
/// a test never needs a delay, a completion signal, or a "wait for the dispatcher to drain" helper. Those are
/// the three things that make view-model tests flaky, and this is how they are avoided rather than managed.
/// </para>
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
