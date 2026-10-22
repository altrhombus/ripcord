using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// The v2 session-token roll (spec §2). Each v2 session mints a fresh 16-byte <c>RP-VerifyKey</c>, sends it
/// encrypted in init2, and — <b>once the console accepts the session</b> — stores it as the type-11 token, which
/// becomes the <b>next</b> session's K16. The first session after pairing holds no token and bootstraps from the
/// bundled <c>K16_reg</c> instead.
///
/// <para>Rolling is self-healing: an abrupt disconnect leaves both ends on the same token, so a from-scratch
/// client persists its own token and reconnects without re-pairing (hardware-confirmed, <c>rp_token_roll_2</c>).
/// This type holds only the roll <em>rules</em> — the token itself is persisted on
/// <see cref="HalyardPairingRecord.RolledToken"/>, and the <c>K16_reg</c> bootstrap comes from the interop
/// bundle. Committing the minted token (<c>record with { RolledToken = … }</c>) is the caller's to do only after
/// acceptance, so a rejected session never advances the token.</para>
/// </summary>
public static class HalyardSessionToken
{
    /// <summary>The token / K16 length in bytes.</summary>
    public const int Length = 16;

    /// <summary>Mint a fresh <c>RP-VerifyKey</c>: 16 CSPRNG bytes, drawn independently of every other session
    /// value (notably <b>not</b> the stream handshake key X — a separate draw; spec §7).</summary>
    public static byte[] Mint() => RandomNumberGenerator.GetBytes(Length);

    /// <summary>Whether a usable rolled token is held — i.e. this is not the first, bootstrap session.</summary>
    public static bool HasRolledToken(ReadOnlySpan<byte> rolledToken) => rolledToken.Length == Length;

    /// <summary>
    /// This session's K16: the stored <paramref name="rolledToken"/> when one is held, otherwise the bootstrap
    /// <paramref name="bootstrapK16Reg"/> (the first session after pairing). Throws if neither is a usable
    /// 16-byte value — a client with no token and no bundled K16_reg cannot open a v2 session.
    /// </summary>
    public static byte[] ResolveK16(ReadOnlySpan<byte> rolledToken, ReadOnlySpan<byte> bootstrapK16Reg)
    {
        if (rolledToken.Length == Length)
            return rolledToken.ToArray();
        if (bootstrapK16Reg.Length == Length)
            return bootstrapK16Reg.ToArray();
        throw new InvalidOperationException(
            "No usable K16: neither a rolled token nor a 16-byte bootstrap K16_reg is available.");
    }
}
