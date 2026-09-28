namespace Ripcord.Core.Reactive;

/// <summary>
/// Minimal cold <see cref="IObservable{T}"/> helpers so backends can expose discovery/streams without
/// taking a reactive-extensions dependency. Each subscription runs the producer with its own
/// cancellation, cancelled on dispose.
/// </summary>
public static class AsyncObservable
{
    public static IObservable<T> Create<T>(Func<IObserver<T>, CancellationToken, Task> producer)
        => new ProducerObservable<T>(producer);

    private sealed class ProducerObservable<T>(Func<IObserver<T>, CancellationToken, Task> producer) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var cts = new CancellationTokenSource();
            // Taken now: the subscription may dispose the source before the task reads it, and a disposed
            // source's Token throws where a token's IsCancellationRequested does not.
            CancellationToken token = cts.Token;
            _ = Task.Run(async () =>
            {
                // Exactly one terminal signal, and none after the subscription is disposed. Until 2026-09-27 an
                // OperationCanceledException always ended the sequence silently, whoever raised it, so a producer
                // that cancelled itself (an internal timeout, say) left an observer waiting for an end that never
                // came. And a producer that returned normally after a dispose was still told OnCompleted. Now only
                // our own cancellation, meaning a dispose, is silent; any other end is signalled.
                try
                {
                    await producer(observer, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        observer.OnCompleted();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // subscription disposed: no signal
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        observer.OnError(ex);
                    }
                }
            }, token);

            return new Subscription(cts);
        }
    }

    private sealed class Subscription(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
