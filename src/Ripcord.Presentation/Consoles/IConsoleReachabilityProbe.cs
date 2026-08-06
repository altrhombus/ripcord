using Ripcord.Core.Consoles;

namespace Ripcord.Presentation.Consoles;

/// <summary>
/// Asks one console whether it is there, and whether it is awake.
///
/// <para>
/// Deliberately the dumbest possible seam: a nullable bool, where null means "no reply at all". The interesting
/// part — turning no-reply / awake / standby into a <see cref="ConsoleReachability"/>, and deciding what a
/// transient non-answer means during a rest transition — is judgement, and judgement belongs in the portable
/// layer where it can be tested. A seam that returned <c>ConsoleReachability</c> directly would have moved that
/// judgement down into the protocol adapter, where testing it means opening a UDP socket.
/// </para>
/// </summary>
public interface IConsoleReachabilityProbe
{
    /// <summary>
    /// True when the console answered and is awake, false when it answered from standby, null when it did not
    /// answer at all (powered off, off the network, or its stored address has moved).
    /// </summary>
    /// <remarks>
    /// Takes the whole record rather than an address because the probe has to speak the console's own family:
    /// a PS4 answers on a different port with a different protocol version, so probing it as a PS5 would report
    /// a perfectly healthy console as offline.
    /// </remarks>
    Task<bool?> ProbeAsync(PairedConsole console, CancellationToken cancellationToken);
}
