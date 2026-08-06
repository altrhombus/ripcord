using Ripcord.Presentation.Consoles;

namespace Ripcord.Presentation.Pairing;

/// <summary>Whether pairing can even be attempted, and if not, what to tell the user.</summary>
/// <param name="Detail">
/// Shown to the user verbatim when unavailable. It names which source of the interop constants was tried and why
/// it lost, which is the difference between an actionable message and "pairing is broken".
/// </param>
public sealed record RegistrarAvailability(bool Available, string Detail);

/// <summary>What the user supplied, plus which console it is for.</summary>
public sealed record ConsoleRegistration(
    string Host,
    string AccountId,
    string Passcode,
    ConsoleFamily Family);

/// <param name="CredentialRecord">
/// The serialized pairing record, PLAINTEXT. The store encrypts it on the way to disk — this type deliberately
/// does not, so there is exactly one place that decides how a credential is protected at rest.
/// </param>
/// <param name="ConsoleId">The console's own stable id, when it reported one. Null for a hand-typed address.</param>
public sealed record ConsoleRegistrationResult(
    bool Succeeded,
    string? FailureReason,
    byte[]? CredentialRecord);

/// <summary>
/// Performs the pairing exchange with a console.
///
/// <para>
/// Two members rather than one because the two failures are different in kind and want different treatment.
/// "This build cannot pair at all" is knowable before the user commits, and reporting it on the step where they
/// are still typing is much better than flashing a progress panel they cannot act on. "The console rejected the
/// code" is only knowable after asking.
/// </para>
/// </summary>
public interface IConsoleRegistrar
{
    /// <summary>
    /// Whether the constants needed to pair <paramref name="family"/> are present. Synchronous because it is a
    /// file and bundle lookup, and checked <em>before</em> the flow enters its pairing step.
    /// </summary>
    RegistrarAvailability CheckAvailability(ConsoleFamily family);

    /// <summary>
    /// Attempt the pairing exchange. Should not throw for an ordinary rejection — that is a result with
    /// <c>Succeeded: false</c> and a reason — but the flow guards against exceptions anyway, since a transport
    /// can always surprise it.
    /// </summary>
    Task<ConsoleRegistrationResult> RegisterAsync(ConsoleRegistration registration, CancellationToken cancellationToken);
}
