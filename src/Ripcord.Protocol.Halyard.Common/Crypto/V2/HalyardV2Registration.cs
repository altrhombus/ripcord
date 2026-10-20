using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// v2 PIN registration (spec §1, K16-SOLVED). The rgst body is the v1 587-byte layout unchanged; v2 adds an
/// <c>RP-Hmac</c> header that authenticates the request with the bundled <c>K16_reg</c> and the registration
/// PIN: <c>RP-Hmac = HMAC-SHA256(K16_reg, PIN as uint32 little-endian)</c>. <c>K16_reg</c> is the family-selected
/// bundled constant (the PS5 value from the interop bundle). Registration clears the stored type-11 token, so
/// the first session bootstraps from <c>K16_reg</c> (<see cref="HalyardSessionToken"/>).
/// </summary>
public static class HalyardV2Registration
{
    /// <summary>
    /// The rgst <c>RP-Hmac</c>: <c>HMAC-SHA256(k16Reg, uint32-little-endian(pin))</c>. Byte-exact on captures
    /// 002/003/004/009.
    /// </summary>
    public static byte[] ComputeRegistrationHmac(ReadOnlySpan<byte> k16Reg, uint pin)
    {
        Span<byte> pinLittleEndian = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(pinLittleEndian, pin);
        return HMACSHA256.HashData(k16Reg, pinLittleEndian);
    }

    /// <summary>
    /// Parse the registration PIN — the decimal code read off the console (up to 8 digits, so it fits a
    /// uint32) — to the integer the HMAC consumes. Throws for anything that is not plain decimal digits in
    /// range (no sign, no whitespace).
    /// </summary>
    public static uint ParsePin(string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        if (!uint.TryParse(pin, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
        {
            throw new ArgumentException(
                "Registration PIN must be decimal digits fitting a 32-bit integer.", nameof(pin));
        }

        return value;
    }
}
