#if DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Pairing;

namespace Ripcord_App.Services;

/// <summary>
/// A pairing that always succeeds and talks to nothing, so the "Paired." celebration can be seen and checked without a
/// console to pair (showcase review). Debug builds only, and only with <c>RIPCORD_REHEARSE_PAIRING=1</c>; use it with
/// a throwaway <c>RIPCORD_DATA_DIR</c>, since what it saves holds no working credential and will never connect.
/// </summary>
internal sealed class RehearsalRegistrar : IConsoleRegistrar
{
    /// <summary>The rehearsal when asked for, otherwise null, which leaves the real registrar in place.</summary>
    public static IConsoleRegistrar? IfRequested()
        => Environment.GetEnvironmentVariable("RIPCORD_REHEARSE_PAIRING") == "1" ? new RehearsalRegistrar() : null;

    public RegistrarAvailability CheckAvailability(ConsoleFamily family) => new(true, string.Empty);

    public async Task<ConsoleRegistrationResult> RegisterAsync(
        ConsoleRegistration registration, CancellationToken cancellationToken)
    {
        // Long enough to see the pairing step, as a real exchange is.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return new ConsoleRegistrationResult(true, null, new byte[] { 0 });
    }
}
#endif
