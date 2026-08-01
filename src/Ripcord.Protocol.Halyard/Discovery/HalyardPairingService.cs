using Ripcord.Core.Discovery;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>Pairing payload: the account context + on-console passcode needed to register this client.</summary>
/// <param name="AccountId">The PSN account id that will own the pairing.</param>
/// <param name="Passcode">The 8-digit passcode the user reads off the console (spec §2.0).</param>
/// <param name="ClientDeviceId">This client's 16-byte device id (RP-Did material).</param>
/// <param name="Platform">Which console family is being paired (selects the registration endpoint).</param>
public sealed record HalyardPairingCredentials(
    string AccountId,
    string Passcode,
    ReadOnlyMemory<byte> ClientDeviceId,
    HalyardConsolePlatform Platform = HalyardConsolePlatform.Ps5) : IPairingCredentials;

/// <summary>
/// Registration service. Delegates the wire flow (obtaining a registration key from a PIN) to
/// <see cref="IHalyardRegistration"/>, which is implemented for real by
/// <c>HalyardRegistrationClient</c> and has paired against a live console from scratch. The indirection
/// stays because it is also the seam the tests substitute.
/// </summary>
public sealed class HalyardPairingService(IHalyardRegistration registration) : IConsolePairingService
{
    private readonly IHalyardRegistration _registration = registration;

    public async Task<PairingResult> RegisterAsync(
        DiscoveredConsole console,
        IPairingCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (credentials is not HalyardPairingCredentials halyard)
        {
            return new PairingResult(false, "Unexpected credential type for this backend.", null);
        }

        HalyardRegistrationResult result = await _registration.RegisterAsync(
            new HalyardRegistrationRequest(
                console.Id,
                console.IpAddress.ToString(),
                halyard.AccountId,
                halyard.Passcode,
                halyard.ClientDeviceId,
                halyard.Platform),
            cancellationToken).ConfigureAwait(false);

        // The persisted credential blob is the full serialized pairing record (registkey + companion + keytype).
        byte[]? credential = result.Record?.Serialize();
        return new PairingResult(result.Succeeded, result.FailureReason, credential);
    }
}
