namespace Ripcord.Input;

/// <summary>
/// Minimal IObserver adapter so callers can subscribe with a plain delegate instead of
/// implementing IObserver themselves, since Ripcord.Core's IObservable-based contracts don't
/// pull in a System.Reactive dependency.
/// </summary>
public sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }
}
