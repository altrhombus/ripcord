using Ripcord.Core.Threading;

namespace Ripcord.Input;

/// <summary>
/// Minimal push-based IObservable, avoiding a System.Reactive dependency for what Phase 0 needs.
/// </summary>
/// <param name="replayLast">
/// When true, a new subscriber immediately receives the most recently published value.
///
/// <para>
/// Needed for STATE streams, as opposed to event streams. Controller connection events are edge-triggered and the
/// sources begin polling in their constructors, so a pad already attached at start-up was announced before anything
/// had subscribed — and never announced again. The visible result was a working controller reported as "none
/// attached", which only reproduced on a machine whose pad is present from boot rather than plugged in afterwards.
/// Frame streams deliberately do NOT replay: a stale input frame is worse than no frame.
/// </para>
/// </param>
internal sealed class SimpleObservable<T>(bool replayLast = false) : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = [];
    // A SpinGate, not a lock: the UI thread enters it, and a contended lock there lets XAML re-enter and fail
    // fast. See SpinGate.
    private readonly SpinGate _lock = new();

    private bool _hasLast;
    private T? _last;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        bool replay;
        T? last;
        using (_lock.Enter())
        {
            _observers.Add(observer);
            replay = replayLast && _hasLast;
            last = _last;
        }

        // Outside the lock: an observer that publishes or subscribes in response must not deadlock.
        if (replay)
        {
            observer.OnNext(last!);
        }

        return new Unsubscriber(this, observer);
    }

    public void Publish(T value)
    {
        IObserver<T>[] snapshot;
        using (_lock.Enter())
        {
            _last = value;
            _hasLast = true;
            snapshot = [.. _observers];
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(value);
        }
    }

    private sealed class Unsubscriber(SimpleObservable<T> owner, IObserver<T> observer) : IDisposable
    {
        public void Dispose()
        {
            using (owner._lock.Enter())
            {
                owner._observers.Remove(observer);
            }
        }
    }
}
