using System.Net;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>The outcome of the v2 control-plane handshake.</summary>
internal readonly record struct V2ControlHandshakeResult(
    bool Success, string? Failure, bool TokenDesync, byte[]? CtrlResponsePlaintext)
{
    public static V2ControlHandshakeResult Ok(byte[] ctrlResponsePlaintext) => new(true, null, false, ctrlResponsePlaintext);
    public static V2ControlHandshakeResult Fail(string reason) => new(false, reason, false, null);
    public static V2ControlHandshakeResult Desync(string reason) => new(false, reason, true, null);
}

/// <summary>
/// Drives the v2 control-plane handshake over a control channel (spec §4): <c>/sess/init</c> (RP-Pubkey/RP-Hmac)
/// → establish the GCM control plane + the launchSpec M → <c>/sess/init2</c> (RP-VerifyKey) → GCM
/// <c>/sess/ctrl</c> → decrypt the response. Each message is a fresh connection (the console closes between
/// them), so the per-connection GCM counter is 0 throughout. Extracted from the session so it is testable
/// against a mock console without the stream (Phase 2) plane.
/// </summary>
internal static class HalyardV2ControlHandshake
{
    internal static async Task<V2ControlHandshakeResult> RunAsync(
        IHalyardControlChannel control, HalyardV2SessionCrypto crypto, IPEndPoint endpoint, string hostHeader,
        HalyardConsolePlatform platform, byte[]? registrationKey, byte[] companion, byte[] k16, byte[] deviceId,
        int osMajor, int osMinor, int startBitrate, int streamingType, int connectionPath, byte[] verifyKey,
        CancellationToken cancellationToken)
    {
        // --- /sess/init: offer our control pubkey, authenticated by RP-Hmac(K16, pubkey). ---
        await control.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        byte[] publicKey = crypto.GenerateControlPublicKey();
        byte[] rpHmac = crypto.ComputeRpHmac(k16, publicKey);
        SessResponse initResponse = await control.SendRequestAsync(
            HalyardV2Requests.BuildInitRequest(platform, hostHeader, registrationKey, publicKey, rpHmac),
            cancellationToken).ConfigureAwait(false);

        if (!initResponse.IsSuccess)
        {
            string? reason = initResponse.Header(SessProtocol.HeaderApplicationReason);
            if (reason is not null && reason.Contains("80108d6c", StringComparison.OrdinalIgnoreCase))
            {
                return V2ControlHandshakeResult.Desync(
                    "The console rejected the v2 session token (0x80108d6c) — the pairing's token is out of sync; re-pair the console.");
            }

            return V2ControlHandshakeResult.Fail(
                $"v2 /sess/init rejected (HTTP {initResponse.StatusCode}{(reason is null ? "" : $", RP-Application-Reason {reason}")}).");
        }

        if (!HalyardV2Control.TryEstablish(initResponse, k16, crypto, out string? establishProblem))
            return V2ControlHandshakeResult.Fail(establishProblem!);

        // The launchSpec cipher (M) is the v1 control KDF over (RP-Nonce, companion); set up CryptStreaminfo.
        HalyardControlKeyMaterial? material = HalyardStreamingSession.ControlKeyMaterial(
            initResponse.Header(SessProtocol.HeaderNonce), companion, platform, out string? materialProblem);
        if (material is null)
            return V2ControlHandshakeResult.Fail(materialProblem!);
        crypto.EstablishControl(material.Value);

        // --- /sess/init2 on a fresh connection: the freshly minted RP-VerifyKey. ---
        await control.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        SessResponse init2Response = await control.SendRequestAsync(
            HalyardV2Requests.BuildInit2Request(crypto, verifyKey, platform, hostHeader), cancellationToken)
            .ConfigureAwait(false);
        if (!init2Response.IsSuccess)
            return V2ControlHandshakeResult.Fail($"v2 /sess/init2 rejected (HTTP {init2Response.StatusCode}).");

        // --- /sess/ctrl on a fresh connection: the GCM-enveloped field block. ---
        await control.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        SessResponse ctrlResponse = await control.SendRequestAsync(
            HalyardV2Requests.BuildCtrlRequest(
                crypto, platform, hostHeader, registrationKey ?? [], deviceId, osMajor, osMinor,
                startBitrate, streamingType, connectionPath),
            cancellationToken).ConfigureAwait(false);
        if (!ctrlResponse.IsSuccess)
            return V2ControlHandshakeResult.Fail($"v2 /sess/ctrl rejected (HTTP {ctrlResponse.StatusCode}).");

        // Decrypt the console's ctrl response (console->client, purpose 0x08, counter 0, AAD empty).
        string? data = ctrlResponse.Header(SessProtocol.HeaderData);
        string? tag = ctrlResponse.Header(SessProtocol.HeaderTag);
        if (data is null || tag is null)
            return V2ControlHandshakeResult.Fail("v2 /sess/ctrl response carried no RP-Data/RP-Tag.");

        byte[] ciphertextAndTag = [.. Convert.FromBase64String(data), .. Convert.FromBase64String(tag)];
        byte[]? plaintext = crypto.OpenConsoleToClient(ciphertextAndTag, ReadOnlySpan<byte>.Empty, counter: 0);
        if (plaintext is null)
            return V2ControlHandshakeResult.Fail("v2 /sess/ctrl response did not authenticate (wrong key/IV).");

        return V2ControlHandshakeResult.Ok(plaintext);
    }
}
