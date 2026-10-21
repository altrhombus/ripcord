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
            .Header(SessProtocol.HeaderSupportCmd, SessProtocol.DefaultSupportCmd)
            .Header(SessProtocol.HeaderPubKey, Convert.ToBase64String(controlPublicKey))
            .Header(SessProtocol.HeaderHmac, Convert.ToBase64String(rpHmac))
            .Header(SessProtocol.HeaderFeature, "1");
    }
}
