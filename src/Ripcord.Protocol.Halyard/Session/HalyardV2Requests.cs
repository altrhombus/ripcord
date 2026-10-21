using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// The v2 (GCM) control-plane HTTP requests (spec §4, K16-SOLVED init state machine). Pure builders so the header
/// sets are testable without a live console. This covers the v2 <c>/sess/init</c> (the vendor's "type 1"), which
/// adds <c>RP-Pubkey</c> + <c>RP-Hmac</c> to the v1 header set; init2 and the GCM-enveloped <c>/sess/ctrl</c>
/// land with the <c>ConnectAsync</c> flow (their exact HTTP shape has open <c>[X]</c>s pinned by the hardware
/// capture).
/// </summary>
internal static class HalyardV2Requests
{
    /// <summary>
    /// The v2 <c>/sess/init</c> request: the v1 registkey/version/support headers plus the control ECDH
    /// <c>RP-Pubkey</c> (133-byte P-521 point, base64) and <c>RP-Hmac</c> (<c>HMAC-SHA256(K16, pubkey[:133])</c>,
    /// base64), and <c>RP-Feature</c> — the header set of <c>FUN_180198a50</c>. <c>Rp-Version</c> keeps the
    /// lowercase-p spelling the captured client sends on init.
    /// </summary>
    internal static SessRequest BuildInitRequest(
        HalyardConsolePlatform platform, string hostHeader, byte[]? registrationKey,
        ReadOnlySpan<byte> controlPublicKey, ReadOnlySpan<byte> rpHmac)
    {
        string registKey = registrationKey is null
            ? string.Empty
            : Convert.ToHexString(registrationKey).ToLowerInvariant();

        // v2 /sess/init is HTTP/1.2 on the wire (confirmed, cap 004); ctrl stays HTTP/1.1.
        return new SessRequest(SessHttpMethod.Get, SessProtocol.PathFor(platform, "init"), "HTTP/1.2")
            .Header("Host", hostHeader)
            .Header("User-Agent", "remoteplay Windows")
            .Header("Connection", "close")
            .Header(SessProtocol.HeaderRegistKey, registKey)
            .Header("Rp-Version", SessProtocol.VersionFor(platform))
            .Header(SessProtocol.HeaderSupportCmd, SessProtocol.V2SupportCmd)
            .Header(SessProtocol.HeaderPubKey, Convert.ToBase64String(controlPublicKey))
            .Header(SessProtocol.HeaderHmac, Convert.ToBase64String(rpHmac))
            .Header(SessProtocol.HeaderFeature, "1");
    }

    /// <summary>
    /// The v2 <c>/sess/init2</c> request (spec §4): a GET on HTTP/1.2 carrying the freshly minted
    /// <c>RP-VerifyKey</c>, GCM-sealed (purpose 0x09, counter 0) into <c>RP-Data</c>/<c>RP-Tag</c>. The AAD is
    /// this request's own header lines minus RP-Data/RP-Tag (CRLF-joined, trailing CRLF) — the console strips the
    /// same two from what we send and computes the identical AAD, so it is self-consistent and need not match any
    /// vendor header set. <paramref name="crypto"/> must already be established.
    /// </summary>
    internal static SessRequest BuildInit2Request(
        IHalyardV2ControlCrypto crypto, ReadOnlySpan<byte> verifyKey, HalyardConsolePlatform platform, string hostHeader)
    {
        var request = new SessRequest(SessHttpMethod.Get, SessProtocol.PathFor(platform, "init2"), "HTTP/1.2")
            .Header("Host", hostHeader)
            .Header("User-Agent", "remoteplay Windows")
            .Header("Connection", "close");

        // AAD = the serialized header lines as they go on the wire, before RP-Data/RP-Tag are appended.
        byte[] aad = HeaderBlock(request.Serialize());

        // init2 plaintext: the verify key as a header line. [X] the exact trailing CRLF (the console parses it as
        // a header either way; verifiable from the captured init2 ciphertext length).
        byte[] plaintext = Encoding.ASCII.GetBytes(
            $"{SessProtocol.HeaderVerifyKey}: {Convert.ToBase64String(verifyKey)}\r\n");

        byte[] sealed_ = crypto.SealClientToConsole(plaintext, aad, counter: 0);
        request.Header(SessProtocol.HeaderData, Convert.ToBase64String(sealed_[..^16]));
        request.Header(SessProtocol.HeaderTag, Convert.ToBase64String(sealed_[^16..]));
        return request;
    }

    /// <summary>The request's header-lines block (each line + CRLF), i.e. the serialization minus the request
    /// line and the terminating blank line — the AAD basis for the init2 envelope.</summary>
    internal static byte[] HeaderBlock(byte[] serialized)
    {
        int firstCrlf = Array.IndexOf(serialized, (byte)'\r');
        int start = firstCrlf + 2;            // past the request line's CRLF
        int end = serialized.Length - 2;      // drop the terminating blank-line CRLF
        return serialized[start..end];
    }
}
