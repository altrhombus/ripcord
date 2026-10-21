using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Establishing the v2 GCM control plane from the <c>/sess/init</c> response (spec §4): verify the console's
/// <c>RP-Hmac</c> over its <c>RP-Pubkey</c> (the console proving it holds the same K16), then ECDH against that
/// pubkey under the session K16. Kept out of the big session file so it is testable against a synthetic console.
/// </summary>
internal static class HalyardV2Control
{
    /// <summary>The uncompressed P-521 point length. The <c>RP-Pubkey</c> base64 decodes to 135 bytes; both the
    /// RP-Hmac and the ECDH use the first 133 (the point).</summary>
    private const int PointLength = HalyardStreamKeySchedule.PublicKeyLengthP521;

    /// <summary>
    /// Verify the console's <c>RP-Hmac</c> and establish the control plane from the init response. Returns false
    /// with a diagnosable <paramref name="problem"/> if a header is missing/malformed, the console's RP-Hmac does
    /// not verify under <paramref name="k16"/>, or its pubkey is not a usable point.
    /// </summary>
    internal static bool TryEstablish(
        SessResponse initResponse, ReadOnlySpan<byte> k16, IHalyardV2ControlCrypto crypto, out string? problem)
    {
        if (!TryDecodeHeader(initResponse, SessProtocol.HeaderNonce, out byte[] nonce, out problem) ||
            !TryDecodeHeader(initResponse, SessProtocol.HeaderPubKey, out byte[] consolePublicKey, out problem) ||
            !TryDecodeHeader(initResponse, SessProtocol.HeaderHmac, out byte[] consoleHmac, out problem))
        {
            return false;
        }

        if (nonce.Length != 16)
        {
            problem = $"/sess/init RP-Nonce is {nonce.Length} bytes, expected 16.";
            return false;
        }

        if (consolePublicKey.Length < PointLength)
        {
            problem = $"/sess/init RP-Pubkey is {consolePublicKey.Length} bytes, expected at least {PointLength}.";
            return false;
        }

        // The console authenticates its pubkey with the same K16 we hold; verify it constant-time before using it.
        byte[] expected = crypto.ComputeRpHmac(k16, consolePublicKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, consoleHmac))
        {
            problem = "The console's RP-Hmac did not verify — wrong K16/token, or a tampered response.";
            return false;
        }

        if (!crypto.EstablishControl(consolePublicKey.AsSpan(0, PointLength), nonce, k16))
        {
            problem = "The console's RP-Pubkey did not yield a control key (bad P-521 point).";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryDecodeHeader(SessResponse response, string header, out byte[] value, out string? problem)
    {
        value = [];
        problem = null;
        string? b64 = response.Header(header);
        if (string.IsNullOrEmpty(b64))
        {
            problem = $"/sess/init response is missing {header}.";
            return false;
        }

        try
        {
            value = Convert.FromBase64String(b64);
            return true;
        }
        catch (FormatException)
        {
            problem = $"/sess/init response {header} is not valid base64.";
            return false;
        }
    }
}
