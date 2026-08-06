using Microsoft.UI.Dispatching;
using Ripcord.Presentation.Threading;

namespace Ripcord_App.Threading;

/// <summary>
/// The WinUI implementation of the portable layer's marshalling seam.
///
/// <para>
/// Constructed once, at startup, from the UI thread — so <see cref="DispatcherQueue.GetForCurrentThread"/>
/// returns the real window dispatcher rather than null. Passing the queue in rather than resolving it per call
/// keeps that requirement at one call site instead of every one.
/// </para>
/// </summary>
internal sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue)
        => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public bool IsOnUiThread => _queue.HasThreadAccess;

    /// <summary>
    /// TryEnqueue rather than the throwing variants: it returns false once the queue has shut down, which is a
    /// normal thing to hit while a session is tearing down and the last background notification arrives. The
    /// seam's contract is that posting after the loop is gone must not throw.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.TryEnqueue(() => action());
    }
}
