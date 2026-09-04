using System.Security.Cryptography;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Halyard.Consoles;
using Ripcord.Presentation.Pairing;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Discovery;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// Pairing against a PlayStation console: resolve the registration cipher for the family, build the request, and
/// run the exchange.
/// </summary>
public sealed class HalyardConsoleRegistrar : IConsoleRegistrar
{
    private readonly IHalyardRegistrationCipherResolver _cipherResolver;

    public HalyardConsoleRegistrar(IHalyardRegistrationCipherResolver? cipherResolver = null)
        => _cipherResolver = cipherResolver ?? new HalyardRegistrationCipherResolver();

    public RegistrarAvailability CheckAvailability(ConsoleFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);

        HalyardRegistrationCipherResolution resolved = _cipherResolver.Resolve(family.ToHalyardPlatform());
        return new RegistrarAvailability(resolved.Cipher.IsAvailable, resolved.Source);
    }

    public async Task<ConsoleRegistrationResult> RegisterAsync(
        ConsoleRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);

        HalyardConsolePlatform platform = registration.Family.ToHalyardPlatform();
        HalyardRegistrationCipherResolution resolved = _cipherResolver.Resolve(platform);
        if (!resolved.Cipher.IsAvailable)
        {
            return new ConsoleRegistrationResult(false, resolved.Source, null);
        }

        var request = new HalyardRegistrationRequest(
            ConsoleId: registration.Host,
            ConsoleHost: registration.Host,
            AccountId: registration.AccountId,
            Passcode: registration.Passcode,

            // 32 bytes, carried over verbatim from the flow this replaced even though
            // HalyardRegistrationRequest's own doc comment says "16-byte device id (RP-Did material)". The
            // discrepancy is real and wire-affecting, and pairing demonstrably works at 32 — so changing it
            // during an extraction would land as a refactor and behave as a protocol change. Tracked as an open
            // question in ROADMAP; do not "tidy" this without deriving the answer first.
            ClientDeviceId: RandomNumberGenerator.GetBytes(32),
            Platform: platform);

        var client = new HalyardRegistrationClient(resolved.Cipher);
        HalyardRegistrationResult result = await client
            .RegisterAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded && result.Record is not null
            ? new ConsoleRegistrationResult(true, null, result.Record.Serialize())
            : new ConsoleRegistrationResult(false, result.FailureReason ?? "Pairing failed.", null);
    }
}
