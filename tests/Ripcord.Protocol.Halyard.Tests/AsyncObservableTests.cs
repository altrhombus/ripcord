using Ripcord.Core.Reactive;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// One terminal signal for every end but a dispose. Until 2026-09-27 an OperationCanceledException ended the
/// sequence silently whoever raised it, so a producer that cancelled itself left its observer waiting for good;
/// and a producer that returned normally after a dispose still told its observer OnCompleted.
/// </summary>
public class AsyncObservableTests
{
    private sealed class Recorder : IObserver<int>
    {
        public List<int> Values { get; } = [];
        public TaskCompletionSource<string> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Terminals;

        public void OnNext(int value) => Values.Add(value);

        public void OnCompleted()
        {
            Interlocked.Increment(ref Terminals);
            Ended.TrySetResult("completed");
        }

        public void OnError(Exception error)
        {
            Interlocked.Increment(ref Terminals);
            Ended.TrySetResult(error.GetType().Name);
        }
    }

    [Fact]
    public async Task AProducerThatReturns_Completes()
    {
        var recorder = new Recorder();
        using var _ = AsyncObservable.Create<int>((o, _) => { o.OnNext(1); return Task.CompletedTask; }).Subscribe(recorder);
        Assert.Equal("completed", await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([1], recorder.Values);
    }

    /// <summary>The case that ended silently: a producer's own cancellation, not ours.</summary>
    [Fact]
    public async Task AProducerThatCancelsItself_EndsWithAnError()
    {
        var recorder = new Recorder();
        using var _ = AsyncObservable.Create<int>(async (_, _) =>
        {
            using var own = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            await Task.Delay(Timeout.Infinite, own.Token);
        }).Subscribe(recorder);
        Assert.Equal(nameof(TaskCanceledException), await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AProducerThatThrows_EndsWithThatError()
    {
        var recorder = new Recorder();
        using var _ = AsyncObservable.Create<int>((_, _) => throw new InvalidDataException()).Subscribe(recorder);
        Assert.Equal(nameof(InvalidDataException), await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A dispose is silent, whether the producer then throws or returns.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADisposedSubscription_GetsNoSignal(bool producerReturnsNormally)
    {
        var recorder = new Recorder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable subscription = AsyncObservable.Create<int>(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException) when (producerReturnsNormally)
            {
                // a producer that treats its token as "stop" and returns, as the LAN search does
            }
            finally
            {
                finished.SetResult();
            }
        }).Subscribe(recorder);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        subscription.Dispose();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(0, recorder.Terminals);
    }
}
