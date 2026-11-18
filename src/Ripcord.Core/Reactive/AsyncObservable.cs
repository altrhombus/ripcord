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
                // Exactly one terminal signal, and none after the subscription is disposed. Only our own
                // cancellation, meaning a dispose, is silent. A producer that cancels itself (an internal timeout,
                // say) raises an OperationCanceledException that is not ours, and it reaches OnError: ending it
                // silently would leave an observer waiting for an end that never comes. A producer that returns
                // normally after a dispose is not told OnCompleted.
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
