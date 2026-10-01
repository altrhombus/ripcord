using Ripcord.Core.Threading;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The gate that replaced <c>lock</c> wherever the UI thread meets a device thread. What it must never do, wait in
/// a way COM can pump through, cannot be observed from a test host; that it still excludes can.
/// </summary>
public class SpinGateTests
{
    [Fact]
    public void ContendedEntriesStillExclude()
    {
        var gate = new SpinGate();
        long counter = 0;
        const int Threads = 8;
        const int Iterations = 20_000;

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                using (gate.Enter())
                {
                    // A read, a pause and a write, so an entry that was not exclusive loses an increment.
                    long read = counter;
                    Thread.SpinWait(5);
                    counter = read + 1;
                }
            }
        });

        Assert.Equal((long)Threads * Iterations, counter);
    }

    [Fact]
    public void LeavingFreesItForTheNextEntry()
    {
        var gate = new SpinGate();

        using (gate.Enter())
        {
        }

        var entered = Task.Run(() =>
        {
            using (gate.Enter())
            {
                return true;
            }
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
    }
}
