namespace Ripcord.Presentation.Threading;

/// <summary>
/// Marshals onto a captured <see cref="SynchronizationContext"/> — the generic .NET front end's dispatcher
/// (Avalonia, GTK#, Uno, or a plain host with a message loop).
///
/// <para>
/// Ships now, with no consumer, because it is the cheap proof that <see cref="IUiDispatcher"/> is genuinely an
/// abstraction rather than a rename of one platform's API. If the seam could only ever be implemented by
/// WinUI's DispatcherQueue, the extraction would not have bought anything.
/// </para>
/// </summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context;

    /// <param name="context">
    /// The UI context. Defaults to whatever is current at construction, which is why this must be constructed on
    /// the UI thread; a null current context means there is no message loop to marshal onto and is an error rather
    /// than something to paper over with a thread-pool fallback.
    /// </param>
    public SynchronizationContextUiDispatcher(SynchronizationContext? context = null)
        => _context = context
            ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "No SynchronizationContext. Construct this on the UI thread, or pass the context explicitly.");

    public bool IsOnUiThread => ReferenceEquals(SynchronizationContext.Current, _context);

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _context.Post(static state => ((Action)state!)(), action);
    }
}
