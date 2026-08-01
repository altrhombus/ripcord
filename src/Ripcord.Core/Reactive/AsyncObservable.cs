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
            _ = Task.Run(async () =>
            {
                try
                {
                    await producer(observer, cts.Token).ConfigureAwait(false);
                    observer.OnCompleted();
                }
                catch (OperationCanceledException)
                {
                    // subscription disposed
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                }
            }, cts.Token);

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
