namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// THE v2 CONTROL-PLANE SEAM ("Path A"). The v2 (PS5) control plane replaces v1's per-field
/// AES-128-CFB (<see cref="IHalyardSessionCrypto.EncryptControlField"/> /
/// <see cref="IHalyardSessionCrypto.DecryptControlField"/>) with a single AES-128-GCM <em>envelope</em> over
/// the whole <c>/sess/init2</c> and <c>/sess/ctrl</c> body. It is a different mechanism, so it lives on its own
/// interface; <see cref="V2.HalyardV2SessionCrypto"/> implements both this and
/// <see cref="IHalyardSessionCrypto"/> (whose v1 field methods it does not support, because v2 does not
/// field-encrypt). The v2 stream plane is the v1 schedule with the client-minted <c>X</c> in the
/// <c>handshakeKey</c> role, and the launchSpec is still AES-128-OFB under the v1 control KDF — so those stay on
/// <see cref="IHalyardSessionCrypto"/>.
///
/// <para><b>The scheme</b> (spec §4, confirmed against our own captures 003/004/009):</para>
/// <list type="bullet">
///   <item><description>The client offers a P-521 control ECDH public key on <c>/sess/init</c>, authenticated
///   by <c>RP-Hmac = HMAC-SHA256(K16, publicKey[:133])</c>. The base64 <c>RP-Pubkey</c> decodes to 135 bytes;
///   the HMAC is over the first 133 (the uncompressed P-521 point).</description></item>
///   <item><description>The console answers with its own public key (its <c>RP-Nonce</c> is for the launchSpec,
///   not here). <c>Z</c> is the ECDH-P-521 shared X-coordinate (66 bytes).</description></item>
///   <item><description><c>sk_p = HMAC-SHA256(Z, 01 ‖ p ‖ 00 ‖ K16 ‖ 01 ‖ 00)</c>, with <c>p = 0x09</c> for
///   client→console and <c>p = 0x08</c> for console→client.</description></item>
///   <item><description>Each direction is AES-128-GCM, key <c>sk_p[0:16]</c>, with a 16-byte IV
///   <c>HMAC-SHA256(N16, sk_p[16:32] ‖ be64(ctr))[0:16]</c>, where <b>N16 is the fixed <c>selectorOne</c> context
///   key from the interop bundle</b> (confirmed against captures 004/009; NOT the per-session RP-Nonce).</description></item>
///   <item><description><b>The per-direction counter restarts at 0 per TCP connection</b>, so init2 and the
///   ctrl request on one connection reuse key+IV — a client MUST reproduce this to interoperate. The RPCS
///   channel on the same connection then continues the counter (1, 2, …), so the counter is the caller's to
///   track and is passed in explicitly here.</description></item>
///   <item><description>AAD: init2 = the request header lines minus <c>RP-Data</c>/<c>RP-Tag</c>, CRLF-joined
///   with a trailing CRLF; ctrl request/response = empty.</description></item>
/// </list>
///
/// <para><b>K16</b> is the per-session token (the stored type-11 token), or the bundled <c>K16_reg</c> on the
/// first session after pairing. No account or per-console material is held here.</para>
/// </summary>
public interface IHalyardV2ControlCrypto
{
    /// <summary>
    /// Generate the client's P-521 control ECDH public key — a 133-byte uncompressed point for <c>RP-Pubkey</c>
    /// — retaining the private key internally for <see cref="EstablishControl"/>. Separate from the stream
    /// plane's ephemeral key.
    /// </summary>
    byte[] GenerateControlPublicKey();

    /// <summary>
    /// <c>RP-Hmac</c> for a public key: <c>HMAC-SHA256(k16, publicKey[:133])</c>. Used both to authenticate our
    /// own <c>RP-Pubkey</c> on the request and to verify the console's on the response.
    /// </summary>
    byte[] ComputeRpHmac(ReadOnlySpan<byte> k16, ReadOnlySpan<byte> publicKey);

    /// <summary>
    /// Complete Path A: derive <c>Z</c> by ECDH against the console's control public key, then the two
    /// per-direction GCM keys from <c>Z</c> and the session <paramref name="k16"/>. (The IV's N16 is the fixed
    /// bundle constant, not a session value, so no nonce is needed here.) Returns <see langword="false"/> if the
    /// inputs are unusable (the instance is left not-established).
    /// </summary>
    bool EstablishControl(ReadOnlySpan<byte> consolePublicKey, ReadOnlySpan<byte> k16);

    /// <summary>Whether <see cref="EstablishControl"/> has succeeded.</summary>
    bool IsControlEstablished { get; }

    /// <summary>
    /// Seal (AES-128-GCM encrypt, returning ciphertext with the 16-byte tag appended) an outgoing
    /// client→console control message at connection counter <paramref name="counter"/> over
    /// <paramref name="aad"/>. The caller supplies the counter (0 for init2 and the ctrl request on a fresh
    /// connection; 1, 2, … for subsequent RPCS messages).
    /// </summary>
    byte[] SealClientToConsole(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, ulong counter);

    /// <summary>
    /// Open (verify the 16-byte tag, then AES-128-GCM decrypt) an incoming console→client control message at
    /// connection counter <paramref name="counter"/> over <paramref name="aad"/>; <see langword="null"/> if
    /// authentication fails (the input is left untouched).
    /// </summary>
    byte[]? OpenConsoleToClient(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, ulong counter);
}
