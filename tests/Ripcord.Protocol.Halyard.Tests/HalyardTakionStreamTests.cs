using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Net.Udp;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Full-stack integration over loopback through <see cref="HalyardTakionStream"/>: Takion handshake →
/// SESSION_REQUEST/REPLY (ECDH) → the base-type demultiplexing loop → an A/V video packet the mock console
/// seals with the negotiated keys, routed to the demuxer, GMAC-verified, AES-CTR-decrypted, and reassembled
/// into an <see cref="EncodedVideoFrame"/>. This is the end-to-end "first picture" path without a console.
/// </summary>
public class HalyardTakionStreamTests
{
    /// <summary>
    /// 17 (0x11) — the protocol version our own capture negotiated, and the only range whose ECDH curve has
    /// been observed. This mock answered 9 until 2026-09-11, which put the full-stack decrypt test on the
    /// P-256 branch that <c>CurveForVersion</c> used to guess at for unknown versions. The most end-to-end
    /// test in the suite was therefore covering a curve no console has been seen to use.
    /// </summary>
    private const int LiveVersion = 17;

    private const uint ServerTag = 0x00002222;
    private static readonly byte[] HandshakeKey = Convert.FromHexString("7a8b9c0d1e2f3344556677889900aabb");
    private static readonly byte[] FramePlaintext = RandomNumberGenerator.GetBytes(200);

    [Fact]
    public async Task FullStack_HandshakeNegotiateAndDecryptVideoFrame()
    {
        using var serverSocket = new UdpChannel();
        using var clientSocket = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverSocket.LocalEndPoint.Port);

        using var crypto = new HalyardV1SessionCrypto(TestSecrets.SyntheticControlSecrets());
        var demuxer = new HalyardStreamDemuxer(crypto);
        var frameReady = new TaskCompletionSource<EncodedVideoFrame>();
        // Copied: the payload crosses to another thread via the TCS, well outside the callback's lifetime.
        demuxer.VideoFrameReady += f => frameReady.TrySetResult(f with { Payload = f.Payload.ToArray() });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = RunMockConsoleAsync(serverSocket, cts.Token);

        await using var stream = new HalyardTakionStream(clientSocket, serverEndpoint, crypto, demuxer);
        var request = new TakionSessionRequest(9, "skey", "launchspec", HandshakeKey);

        TakionSessionResult result = await stream.StartAsync(
            request, handshakeTimeout: TimeSpan.FromSeconds(1), handshakeAttempts: 5, cts.Token);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(stream.IsStreamEstablished);

        EncodedVideoFrame frame = await frameReady.Task.WaitAsync(cts.Token);
        // The demuxer strips the 2-byte per-unit prefix before the Annex-B NAL, so the emitted frame is the
        // decrypted payload minus those 2 bytes.
        Assert.Equal(Convert.ToHexString(FramePlaintext.AsSpan(2).ToArray()), Convert.ToHexString(frame.Payload.ToArray()));

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    private static async Task RunMockConsoleAsync(UdpChannel server, CancellationToken ct)
    {
        // P-521: the curve version 17 selects, i.e. the one a real console brings to this exchange.
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using (serverKp)
        {
            uint clientTag = 0;
            var reassembler = new TakionMessageReassembler();
            uint outboundSeq = ServerTag;

            while (!ct.IsCancellationRequested)
            {
                var received = await server.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] packet = received.Buffer;
                var client = received.RemoteEndPoint;
                if (packet.Length < TakionMessageHeader.Length + 1)
                {
                    continue;
                }

                var chunkType = (SctpChunkType)packet[TakionMessageHeader.Length];
                if (chunkType == SctpChunkType.Init)
                {
                    clientTag = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(TakionMessageHeader.Length + 4));
                    await server.SendAsync(BuildInitAck(clientTag), client, ct).ConfigureAwait(false);
                    continue;
                }
                if (chunkType == SctpChunkType.CookieEcho)
                {
                    await server.SendAsync(BuildBareChunk(clientTag, SctpChunkType.CookieAck), client, ct).ConfigureAwait(false);
                    continue;
                }

                if (!TakionDataChunk.TryParse(packet, out var data))
                {
                    continue; // ignore client SACKs
                }

                await server.SendAsync(TakionSackChunk.Build(clientTag, data.Seq), client, ct).ConfigureAwait(false);
                byte[]? complete = reassembler.Accept(data);
                if (complete is null)
                {
                    continue;
                }

                var message = ControlMessage.Parser.ParseFrom(complete);

                // A version is agreed before a session is asked for. This console answers 9, a P-256 version,
                // matching the curve its key pair is on.
                if (message.Type == ControlMessage.Types.MessageType.ProtocolVersionRequest)
                {
                    var versionAck = new ControlMessage
                    {
                        Type = ControlMessage.Types.MessageType.ProtocolVersionAck,
                        ProtocolVersionAck = new ProtocolVersionAckPayload { ProtocolVersion = LiveVersion },
                    };
                    await server.SendAsync(
                        TakionDataChunk.Build(clientTag, outboundSeq++, 0, versionAck.ToByteArray()),
                        client, ct).ConfigureAwait(false);
                    continue;
                }

                if (message.Type != ControlMessage.Types.MessageType.SessionRequest)
                {
                    continue;
                }

                byte[] clientPub = message.SessionRequestPayload.EcdhPublicKey.ToByteArray();
                byte[] shared = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
                var s2c = HalyardStreamKeySchedule.DeriveDirection(shared, HandshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
                var packetCrypto = new HalyardPacketCrypto(s2c);

                byte[] serverSig = HalyardStreamKeySchedule.ComputeEcdhSignature(HandshakeKey, serverPub);
                var reply = new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.SessionReply,
                    SessionReplyPayload = new SessionReplyPayload
                    {
                        ServerVersion = LiveVersion, Token = 1, EncryptedKeyAccepted = true, VersionAccepted = true,
                        SessionKey = "skey",
                        EcdhPublicKey = ByteString.CopyFrom(serverPub),
                        EcdhSignature = ByteString.CopyFrom(serverSig),
                    },
                };
                await server.SendAsync(TakionDataChunk.Build(clientTag, outboundSeq++, 0, reply.ToByteArray()), client, ct).ConfigureAwait(false);

                // After the key agreement the console sends STREAM_INFO and waits for the client's
                // STREAM_INFO_ACK before it streams A/V (mirrors the real flow, wire-confirmed).
                var streamInfo = new ControlMessage { Type = ControlMessage.Types.MessageType.StreamInfo };
                await server.SendAsync(TakionDataChunk.Build(clientTag, outboundSeq++, 0, streamInfo.ToByteArray()), client, ct).ConfigureAwait(false);

                // Give the client a moment to establish the stream keys, then send two video frames so the
                // demuxer flushes frame 0 (it emits a frame when the next frame index arrives).
                await Task.Delay(200, ct).ConfigureAwait(false);
                await server.SendAsync(BuildSealedVideo(packetCrypto, packetIndex: 1, frameIndex: 0, keyPos: 0x00013060, FramePlaintext), client, ct).ConfigureAwait(false);
                await server.SendAsync(BuildSealedVideo(packetCrypto, packetIndex: 2, frameIndex: 1, keyPos: 0x00013460, RandomNumberGenerator.GetBytes(64)), client, ct).ConfigureAwait(false);
            }
        }
    }

    private static byte[] BuildSealedVideo(HalyardPacketCrypto crypto, ushort packetIndex, ushort frameIndex, uint keyPos, byte[] plaintext)
    {
        const int payloadOffset = 21; // v1 video: base 18 + 3-byte video prefix
        var packet = new byte[payloadOffset + plaintext.Length];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), packetIndex);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);
        // off5..8 unit fields = 0 (unit 0, 1 unit total, 0 fec); off9 codec = 0
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(14), keyPos);
        plaintext.CopyTo(packet, payloadOffset);

        byte[] ct = crypto.CryptPayload(keyPos, packet.AsSpan(payloadOffset));
        ct.CopyTo(packet.AsSpan(payloadOffset));
        return crypto.SealPacket(keyPos, packet, HalyardPacketCrypto.AvTagOffset);
    }

    private static byte[] BuildInitAck(uint clientTag)
    {
        var cookie = new byte[TakionHandshake.CookieLength];
        int chunkLength = 4 + 16 + cookie.Length;
        var packet = new byte[TakionMessageHeader.Length + chunkLength];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, clientTag).Write(span);
        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.InitAck;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)chunkLength);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], ServerTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], TakionHandshake.DefaultReceiveWindow);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[12..], TakionHandshake.DefaultOutStreams);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[14..], TakionHandshake.DefaultInStreams);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[16..], ServerTag);
        cookie.CopyTo(chunk[20..]);
        return packet;
    }

    private static byte[] BuildBareChunk(uint clientTag, SctpChunkType type)
    {
        var packet = new byte[TakionMessageHeader.Length + 4];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, clientTag).Write(span);
        span[TakionMessageHeader.Length] = (byte)type;
        return packet;
    }
}
