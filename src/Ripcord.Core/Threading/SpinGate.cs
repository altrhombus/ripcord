namespace Ripcord.Core.Threading;

/// <summary>
/// Mutual exclusion for a few lines of code shared between the UI thread and a device or network thread, which
/// waits by spinning and never in a wait the UI thread can pump through.
///
/// <para>
/// <b>Why not <c>lock</c>.</b> A contended <c>lock</c> on a COM single-threaded apartment, which the WinUI UI thread
/// is, does not simply block: it waits in <c>CoWaitForMultipleHandles</c>, which keeps dispatching window messages.
/// XAML then runs a queued message re-entrantly, inside whatever callback took the lock, and fails fast on purpose
/// with a stowed <c>E_UNEXPECTED</c> - no managed exception, nothing in the crash log, the process gone. It was
/// caught in a dump on 2026-09-30: the UI thread in <c>MainWindow</c>'s Loaded handler, building the controller
/// composite, waited on its lock while a pad's thread held it, and XAML failed fast under
/// <c>CXcpDispatcher::OnReentrancyProtectedWindowMessage</c>. It happened only with pads connected, because only then
/// is a device thread there to hold the lock.
/// </para>
///
/// <para>
/// <b>What it is for, and what it is not.</b> Every section it guards must be short and must not call out: copy a
/// list, swap a field, merge two frames. It is not re-entrant, and a thread that enters twice spins forever. It is
/// not for waiting on work - the UI thread waits for nothing here, it only ever loses a race by a few instructions.
/// </para>
/// </summary>
public sealed class SpinGate
{
    private int _held;

    /// <summary>Enter, spinning until free. Dispose the result to leave: <c>using (gate.Enter()) { ... }</c>.</summary>
    public Scope Enter()
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            var spin = new SpinWait();
            do
            {
                // -1 keeps SpinWait away from Thread.Sleep(1), so the worst a contended entry costs is a few
                // yields. None of this waits in a way COM can pump through.
                spin.SpinOnce(sleep1Threshold: -1);
            }
            while (Interlocked.CompareExchange(ref _held, 1, 0) != 0);
        }

        return new Scope(this);
    }

    /// <summary>Held until disposed. A ref struct, so it cannot escape the block that entered.</summary>
    public readonly ref struct Scope
    {
        private readonly SpinGate _gate;

        internal Scope(SpinGate gate) => _gate = gate;

        public void Dispose() => Volatile.Write(ref _gate._held, 0);
    }
}
