namespace Ripcord.Core.Reactive;

/// <summary>
/// Minimal hot observable that backends push values into (video/audio frames, statistics). Avoids a
/// reactive-extensions dependency in Core. Thread-safe for concurrent publish/subscribe.
/// </summary>
public sealed class Subject<T> : IObservable<T>
{
    private readonly Lock _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private bool _completed;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _observers.Add(observer);
            }
        }

        return new Unsubscribe(this, observer);
    }

    public void OnNext(T value)
    {
        foreach (IObserver<T> observer in Snapshot())
        {
            observer.OnNext(value);
        }
    }

    public void OnCompleted()
    {
        lock (_gate)
        {
            _completed = true;
        }

        foreach (IObserver<T> observer in Snapshot())
        {
            observer.OnCompleted();
        }
    }

    private IObserver<T>[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _observers];
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Unsubscribe(Subject<T> subject, IObserver<T> observer) : IDisposable
    {
        public void Dispose() => subject.Remove(observer);
    }
}
