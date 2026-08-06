namespace Ripcord.Presentation.Threading;

/// <summary>
/// The single marshalling seam between this layer and a front end's main loop.
///
/// <para>
/// THE RULE, stated here and nowhere else: a view-model in this layer never marshals at its call sites. It
/// mutates through one central helper, which is the only place a post happens. Consequently every field a
/// view-model owns is read and written on the dispatcher thread alone, and <em>nothing in this layer takes a
/// lock</em>. That is a deliberate difference from <c>SessionController</c>, which genuinely is multi-threaded
/// and pays for it with a gate and volatile reads throughout.
/// </para>
///
/// <para>
/// This is the counterpart to <c>SessionController</c>'s threading contract, not a contradiction of it. That
/// class documents that its observers are notified from whichever thread caused the change and that "a UI
/// consumer must marshal to its own dispatcher". A view-model here <em>is</em> that consumer, and it discharges
/// the obligation once, on behalf of the whole layer. Every other arbitrary-thread source — discovery
/// callbacks, reachability probes, settings-changed events — arrives the same way.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// True when the caller is already on the front end's main/UI thread.
    ///
    /// <para>
    /// This is a correctness requirement rather than an optimisation. A handler that calls a view-model method
    /// and then reads the resulting state on the next line must not observe the pre-change value; posting
    /// unconditionally would break that, while posting only when off-thread preserves the synchronous
    /// reasoning event handlers rely on.
    /// </para>
    /// </summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Queue <paramref name="action"/> onto the main loop. Must not throw when the loop is already gone —
    /// work in flight during shutdown is expected and is not an error.
    /// </summary>
    void Post(Action action);
}
