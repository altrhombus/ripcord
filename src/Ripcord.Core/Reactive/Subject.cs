using Ripcord.Core.Threading;

namespace Ripcord.Core.Reactive;

/// <summary>
/// Minimal hot observable that backends push values into (video/audio frames, statistics). Avoids a
/// reactive-extensions dependency in Core. Thread-safe for concurrent publish/subscribe.
/// </summary>
public sealed class Subject<T> : IObservable<T>
{
    // A SpinGate, not a lock: the UI thread enters it, and a contended lock there lets XAML re-enter and fail
    // fast. See SpinGate.
    private readonly SpinGate _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private bool _completed;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        using (_gate.Enter())
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
        using (_gate.Enter())
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
        using (_gate.Enter())
        {
            return [.. _observers];
        }
    }

    private void Remove(IObserver<T> observer)
    {
        using (_gate.Enter())
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Unsubscribe(Subject<T> subject, IObserver<T> observer) : IDisposable
    {
        public void Dispose() => subject.Remove(observer);
    }
}
