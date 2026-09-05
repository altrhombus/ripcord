using Avstream;
using Google.Protobuf;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>Inputs to the SESSION_REQUEST (spec §4.2, §5). The launchSpec is already built + encrypted by the caller.</summary>
/// <param name="ClientVersion">Protocol/client version reported in the request.</param>
/// <param name="SessionKey">The session key string carried in the request.</param>
/// <param name="LaunchSpecJson">The streaminfo/launchSpec field value (out1-encrypted + base64 by the caller).</param>
/// <param name="HandshakeKey">The 16 random bytes that authenticate the ECDH exchange (also placed in the launchSpec).</param>
public readonly record struct TakionSessionRequest(
    uint ClientVersion,
    string SessionKey,
    string LaunchSpecJson,
    byte[] HandshakeKey);

/// <summary>Outcome of the session negotiation.</summary>
public readonly record struct TakionSessionResult(bool Success, string? FailureReason, ControlMessage? Reply)
{
    public static TakionSessionResult Ok(ControlMessage reply) => new(true, null, reply);
    public static TakionSessionResult Fail(string reason) => new(false, reason, null);
}

/// <summary>
/// Runs the v1 stream key agreement over an established Takion connection (spec §4.1, §5.1-5.4): send
/// SESSION_REQUEST with our ephemeral ECDH public key + <c>ecdhSignature</c> and the launchSpec (which
/// carries the handshakeKey), receive SESSION_REPLY, verify the server's <c>ecdhSignature</c>, and derive
/// the per-direction stream keys via the crypto seam. On success the stream crypto is established and A/V
/// packets can be authenticated and decrypted.
/// </summary>
public sealed class TakionSessionNegotiator
{
    private readonly TakionReliableChannel _channel;
    private readonly IHalyardSessionCrypto _crypto;

    public TakionSessionNegotiator(TakionReliableChannel channel, IHalyardSessionCrypto crypto)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    /// <summary>
    /// The protocol versions the stream association offers, in the order and with the gap the captured client
    /// uses — 12 really is absent (cap64/cap65, [W]). The console answers with the single one it picked.
    ///
    /// <para>
    /// Sending this at all is the point. The stream association used to skip straight to SESSION_REQUEST, and
    /// a console that has agreed no version defaults to the lowest it knows: it answered ours at version 9 and
    /// returned a SESSION_REPLY with the ECDH fields simply absent, which arrives downstream as
    /// "ecdhSignature verification failed" and reads like a crypto bug. The senkusha association is the
    /// exception and offers only 9, because a probe needs no key agreement.
    /// </para>
    /// </summary>
    private static readonly uint[] SupportedVersions = [9, 10, 11, 13, 14, 15, 16, 17];

    public async Task<TakionSessionResult> NegotiateAsync(TakionSessionRequest request, CancellationToken cancellationToken)
    {
        // Agree a version before asking for a session (channel 0x15).
        var versionRequest = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.ProtocolVersionRequest,
            ProtocolVersionRequest = new ProtocolVersionRequestPayload { SupportedVersions = { SupportedVersions } },
        };
        await _channel.SendMessageAsync(
            TakionDataChunk.ChannelProtocolVersion, versionRequest, cancellationToken).ConfigureAwait(false);

        ControlMessage? versionAck = await ReceiveUntilAsync(
            ControlMessage.Types.MessageType.ProtocolVersionAck, cancellationToken).ConfigureAwait(false);
        if (versionAck is null)
        {
            return TakionSessionResult.Fail("no PROTOCOL_VERSION_ACK received");
        }

        // Speak the version the console chose, not the one we hoped for. It picks from our list, so this is
        // normally what we asked for; deferring to it costs nothing and keeps the curve choice below honest if
        // it ever picks lower.
        uint version = versionAck.ProtocolVersionAck?.ProtocolVersion is uint agreed and not 0
            ? agreed
            : request.ClientVersion;

        // Our ephemeral ECDH public key, on the curve for the negotiated version (§5.2), authenticated by the
        // handshakeKey.
        byte[] publicKey = _crypto.GenerateEphemeralPublicKey((int)version);
        byte[] signature = _crypto.ComputeEcdhSignature(request.HandshakeKey, publicKey);

        var message = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload
            {
                ClientVersion = version,
                SessionKey = request.SessionKey,
                LaunchSpecJson = request.LaunchSpecJson,
                // encryptedKey is a required proto field; the console drops the whole SESSION_REQUEST without it.
                // The vendor client sends 4 zero bytes (the key rides in the launchSpec, not here).
                EncryptedKey = ByteString.CopyFrom(new byte[4]),
                EcdhPublicKey = ByteString.CopyFrom(publicKey),
                EcdhSignature = ByteString.CopyFrom(signature),
            },
        };

        await _channel.SendMessageAsync(TakionDataChunk.ChannelSession, message, cancellationToken).ConfigureAwait(false);

        ControlMessage? reply = await ReceiveUntilAsync(ControlMessage.Types.MessageType.SessionReply, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return TakionSessionResult.Fail("no SESSION_REPLY received");
        }

        SessionReplyPayload payload = reply.SessionReplyPayload;
        if (payload is null || payload.EcdhPublicKey is null || payload.EcdhSignature is null)
        {
            return TakionSessionResult.Fail("SESSION_REPLY missing ECDH material");
        }

        byte[] serverPublicKey = payload.EcdhPublicKey.ToByteArray();
        byte[] serverSignature = payload.EcdhSignature.ToByteArray();

        if (!_crypto.VerifyEcdhSignature(request.HandshakeKey, serverPublicKey, serverSignature))
        {
            return TakionSessionResult.Fail("server ecdhSignature verification failed (possible MITM)");
        }

        if (!_crypto.TryEstablishStream(serverPublicKey, request.HandshakeKey))
        {
            return TakionSessionResult.Fail("stream key establishment failed");
        }

        return TakionSessionResult.Ok(reply);
    }

    private async Task<ControlMessage?> ReceiveUntilAsync(ControlMessage.Types.MessageType type, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ControlMessage message;
            try
            {
                message = await _channel.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (message.Type == type)
            {
                return message;
            }
            // Ignore other control messages (heartbeats, bandwidth, etc.) while waiting for the reply.
        }

        return null;
    }
}
