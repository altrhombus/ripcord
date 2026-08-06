namespace Ripcord.Core.Reactive;

/// <summary>
/// Minimal IObserver adapter so callers can subscribe with a plain delegate instead of
/// implementing IObserver themselves, since Ripcord.Core's IObservable-based contracts don't
/// pull in a System.Reactive dependency.
///
/// <para>
/// Lives in Core beside <see cref="Subject{T}"/> rather than in Ripcord.Input, where it started: it is the
/// counterpart to Core's own IObservable contracts and had acquired consumers in the app, the input stack and
/// the protocol layer. Ripcord.Input targets net10.0-windows, so leaving it there meant anything portable that
/// wanted to subscribe with a lambda had to write its own copy.
/// </para>
/// </summary>
/// <param name="onNext">Required — the whole point of the adapter.</param>
/// <param name="onError">
/// Optional. Omitting it swallows the error, which is fine for a fire-and-forget subscription but wrong for
/// anything that waits on the sequence: a producer that throws would otherwise never be heard from again.
/// </param>
/// <param name="onCompleted">Optional. Needed by callers that wait for a sequence to finish.</param>
public sealed class AnonymousObserver<T>(
    Action<T> onNext,
    Action<Exception>? onError = null,
    Action? onCompleted = null) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);

    public void OnError(Exception error) => onError?.Invoke(error);

    public void OnCompleted() => onCompleted?.Invoke();
}
