using System.Net;
using System.Security.Cryptography;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Net.Udp;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// End-to-end stream key agreement over loopback: the real <see cref="TakionSessionNegotiator"/> drives
/// SESSION_REQUEST/SESSION_REPLY against a mock console that mirrors the ECDH. The decisive assertion is
/// that the keys the client establishes actually match the server's - proven by having the mock seal an A/V
/// packet with the server->client keys and confirming the client decrypts it through the crypto seam. This
/// is the transport-meets-crypto "first picture" path exercised without a real console.
/// </summary>
public class TakionSessionNegotiatorTests
{
    private const uint ClientTag = 0x00001111;
    private const uint ServerTag = 0x00002222;
    private static readonly byte[] HandshakeKey = Convert.FromHexString("7a8b9c0d1e2f3344556677889900aabb");

    [Fact]
    public async Task Negotiate_EstablishesStreamKeysThatDecryptServerPacket()
    {
        using var serverSocket = new UdpChannel();
        using var clientSocket = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverSocket.LocalEndPoint.Port);

        using var crypto = new HalyardV1SessionCrypto(TestSecrets.SyntheticControlSecrets());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var sealedByServer = new TaskCompletionSource<(byte[] packet, byte[] plaintext, ulong keyPos, int payloadOffset)>();
        var serverTask = RunMockConsoleAsync(serverSocket, sealedByServer, cts.Token);

        await using var channel = new TakionReliableChannel(
            clientSocket, serverEndpoint, ClientTag, ServerTag, retransmitInterval: TimeSpan.FromMilliseconds(150));
        channel.Start();

        var negotiator = new TakionSessionNegotiator(channel, crypto);
        var request = new TakionSessionRequest(
            ClientVersion: 9, SessionKey: "skey", LaunchSpecJson: "launchspec", HandshakeKey: HandshakeKey);

        TakionSessionResult result = await negotiator.NegotiateAsync(request, cts.Token);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(crypto.IsStreamEstablished);

        // A version is agreed before a session is asked for, and the console's answer is what gets spoken --
        // not the version the caller requested. Skipping this exchange is how a console ends up agreeing its
        // own lowest version and returning a SESSION_REPLY with no ECDH material in it.
        Assert.Contains(MockConsoleVersion, await offeredVersions.Task.WaitAsync(cts.Token));
        Assert.Equal(MockConsoleVersion, await clientVersions.Task.WaitAsync(cts.Token));

        // Decisive: the client opens the A/V packet the server sealed with the negotiated s->c keys.
        var (avPacket, plaintext, keyPos, payloadOffset) = await sealedByServer.Task.WaitAsync(cts.Token);
        Assert.True(crypto.TryOpenPacket(avPacket, HalyardPacketLayout.Av(keyPos, payloadOffset)));
        Assert.Equal(Convert.ToHexString(plaintext), Convert.ToHexString(avPacket.AsSpan(payloadOffset).ToArray()));

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    /// <summary>The version the mock console picks — see the ack it builds for why this one.</summary>
    private const uint MockConsoleVersion = 9;

    private static readonly TaskCompletionSource<uint[]> offeredVersions =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly TaskCompletionSource<uint> clientVersions =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task RunMockConsoleAsync(
        UdpChannel server,
        TaskCompletionSource<(byte[], byte[], ulong, int)> sealedByServer,
        CancellationToken ct)
    {
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair();
        using (serverKp)
        {
            var reassembler = new TakionMessageReassembler();

            // Each message this console sends needs its own sequence. It only ever sent one before, so a
            // constant was harmless; now that a version ack precedes the reply, reusing it makes the client's
            // reliable channel discard the second as a duplicate -- which surfaces as "no SESSION_REPLY".
            uint outboundSeq = ServerTag;
            while (!ct.IsCancellationRequested)
            {
                var received = await server.ReceiveAsync(ct).ConfigureAwait(false);
                if (!TakionDataChunk.TryParse(received.Buffer, out var data))
                {
                    continue; // ignore SACKs from the client
                }

                var client = received.RemoteEndPoint;
                await server.SendAsync(TakionSackChunk.Build(ClientTag, data.Seq), client, ct).ConfigureAwait(false);

                byte[]? complete = reassembler.Accept(data);
                if (complete is null)
                {
                    continue;
                }

                var message = ControlMessage.Parser.ParseFrom(complete);

                // Agree a version first, the way a console does: it picks one of the offered versions and the
                // client speaks that. Without this the negotiator never gets as far as SESSION_REQUEST.
                if (message.Type == ControlMessage.Types.MessageType.ProtocolVersionRequest)
                {
                    offeredVersions.TrySetResult([.. message.ProtocolVersionRequest.SupportedVersions]);

                    // This console answers 9 — the lowest on offer, and a P-256 version, which is the curve its
                    // key pair is on. A client that ignored the answer and used its own highest offer would put
                    // a P-521 key on the wire and the shared secret below would not derive at all.
                    var versionAck = new ControlMessage
                    {
                        Type = ControlMessage.Types.MessageType.ProtocolVersionAck,
                        ProtocolVersionAck = new ProtocolVersionAckPayload { ProtocolVersion = MockConsoleVersion },
                    };
                    await server.SendAsync(
                        TakionDataChunk.Build(ClientTag, seq: outboundSeq++, channel: 0, versionAck.ToByteArray()),
                        client, ct).ConfigureAwait(false);
                    continue;
                }

                if (message.Type != ControlMessage.Types.MessageType.SessionRequest)
                {
                    continue;
                }

                clientVersions.TrySetResult(message.SessionRequestPayload.ClientVersion);
                byte[] clientPub = message.SessionRequestPayload.EcdhPublicKey.ToByteArray();

                // Mirror the ECDH: derive the shared secret and the server->client packet keys.
                byte[] shared = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
                var s2c = HalyardStreamKeySchedule.DeriveDirection(shared, HandshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
                var packetCrypto = new HalyardPacketCrypto(s2c);

                // Seal an A/V packet with those keys for the client to open.
                const ulong keyPos = 0x00013060;
                const int payloadOffset = 21;
                byte[] plaintext = RandomNumberGenerator.GetBytes(160);
                var avPacket = new byte[payloadOffset + plaintext.Length];
                avPacket[0] = 0x02;
                plaintext.CopyTo(avPacket, payloadOffset);
                var ct2 = packetCrypto.CryptPayload(keyPos, avPacket.AsSpan(payloadOffset));
                ct2.CopyTo(avPacket.AsSpan(payloadOffset));
                WriteKeyPos(avPacket, keyPos);
                byte[] sealedPacket = packetCrypto.SealPacket(keyPos, avPacket, HalyardPacketCrypto.AvTagOffset);
                sealedByServer.TrySetResult((sealedPacket, plaintext, keyPos, payloadOffset));

                // Reply with the server pubkey + signature so the client can verify + establish.
                byte[] serverSig = HalyardStreamKeySchedule.ComputeEcdhSignature(HandshakeKey, serverPub);
                var reply = new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.SessionReply,
                    SessionReplyPayload = new SessionReplyPayload
                    {
                        ServerVersion = 9,
                        Token = 1,
                        EncryptedKeyAccepted = true,
                        VersionAccepted = true,
                        SessionKey = "skey",
                        EcdhPublicKey = ByteString.CopyFrom(serverPub),
                        EcdhSignature = ByteString.CopyFrom(serverSig),
                    },
                };
                byte[] replyPacket = TakionDataChunk.Build(ClientTag, seq: outboundSeq++, channel: 0, reply.ToByteArray());
                await server.SendAsync(replyPacket, client, ct).ConfigureAwait(false);
            }
        }
    }

    private static void WriteKeyPos(byte[] packet, ulong keyPos)
    {
        packet[14] = (byte)(keyPos >> 24);
        packet[15] = (byte)(keyPos >> 16);
        packet[16] = (byte)(keyPos >> 8);
        packet[17] = (byte)keyPos;
    }
}
