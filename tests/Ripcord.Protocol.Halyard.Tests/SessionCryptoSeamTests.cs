using System.Security.Cryptography;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Integration tests for the reshaped crypto seam driven through the real <see cref="HalyardV1SessionCrypto"/>:
/// control-field round-trips, the ECDH stream key agreement between two endpoints, and the whole-packet
/// seal/open path the demuxer and input writer use. These lock the wiring end to end (a step above the
/// primitive-level <see cref="StreamCryptoTests"/>).
/// </summary>
public class SessionCryptoSeamTests
{
    // Synthetic extracted-constant container: the stream path never touches these, and the control path
    // only needs the KDF to be deterministic (encrypt/decrypt round-trips regardless of whether the key
    // matches a real console). Real-console values live in the gitignored dirty-room fixture.
    private static HalyardControlSecrets SyntheticSecrets()
    {
        var t1 = new byte[HalyardControlSecrets.KdfTableLength];
        var t2 = new byte[HalyardControlSecrets.KdfTableLength];
        RandomNumberGenerator.Fill(t1);
        RandomNumberGenerator.Fill(t2);
        var keys = new HalyardFieldContextKeys(
            new byte[16], new byte[16], new byte[16], new byte[16]);
        return new HalyardControlSecrets(t1, t2, keys);
    }

    [Fact]
    public void Control_EstablishThenEncryptDecrypt_RoundTrips()
    {
        using var crypto = new HalyardV1SessionCrypto(SyntheticSecrets());
        Assert.False(crypto.IsControlEstablished);

        crypto.EstablishControl(new HalyardControlKeyMaterial(
            Nonce: Hex.Bytes("1a2b3c4d5e6f708192a3b4c5d6e7f809"),
            Companion: Hex.Bytes("2a3b4c5d6e7f8091a2b3c4d5e6f70819"),
            CodecSelector: 2, VersionSelector: 1));
        Assert.True(crypto.IsControlEstablished);

        var plaintext = Hex.Bytes("61626364313233340000000000000000");
        var ct = crypto.EncryptControlField(0, plaintext);
        Assert.NotEqual(Hex.String(plaintext), Hex.String(ct));
        Assert.Equal(Hex.String(plaintext), Hex.String(crypto.DecryptControlField(0, ct)));

        // Streaminfo (OFB) is symmetric through the seam.
        var cfg = System.Text.Encoding.ASCII.GetBytes("{\"sessionId\":\"x\"}");
        var enc = crypto.CryptStreaminfo(0, cfg);
        Assert.Equal(Hex.String(cfg), Hex.String(crypto.CryptStreaminfo(0, enc)));
    }

    /// <summary>
    /// The protocol version our own capture negotiated, and the only one whose curve has been observed.
    /// Passed explicitly because <c>CurveForVersion</c> no longer has a fallback to be defaulted into.
    /// </summary>
    private const int LiveVersion = 17;

    [Fact]
    public void Stream_DownDirection_ServerSealsClientOpens()
    {
        var handshakeKey = RandomBytes(16);
        using var client = new HalyardV1SessionCrypto(SyntheticSecrets());

        // Client generates its ephemeral key; the "server" completes ECDH against the client's pubkey.
        byte[] clientPub = client.GenerateEphemeralPublicKey(LiveVersion);
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using (serverKp)
        {
            Assert.True(client.TryEstablishStream(serverPub, handshakeKey));
            Assert.True(client.IsStreamEstablished);

            // Server-side down-direction (server->client) packet crypto, independently derived.
            var serverShared = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
            var s2c = HalyardStreamKeySchedule.DeriveDirection(serverShared, handshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
            var serverDown = new HalyardPacketCrypto(s2c);

            const ulong keyPos = 0x00013060;
            const int payloadOffset = 21; // video: base 18 + 3-byte prefix
            var plaintext = System.Text.Encoding.ASCII.GetBytes(new string('V', 160));

            // Server: build packet, encrypt payload, MAC the whole packet.
            var packet = new byte[payloadOffset + plaintext.Length];
            packet[0] = 0x02;
            plaintext.CopyTo(packet, payloadOffset);
            var ct = serverDown.CryptPayload(keyPos, packet.AsSpan(payloadOffset));
            ct.CopyTo(packet.AsSpan(payloadOffset));
            WriteKeyPos(packet, keyPos);
            var sealed_ = serverDown.SealPacket(keyPos, packet, HalyardPacketCrypto.AvTagOffset);

            // Client opens through the seam: verify + decrypt in place.
            Assert.True(client.TryOpenPacket(sealed_, HalyardPacketLayout.Av(keyPos, payloadOffset)));
            Assert.Equal(Hex.String(plaintext), Hex.String(sealed_.AsSpan(payloadOffset)));

            // Tampering with the payload fails the GMAC.
            var tampered = (byte[])sealed_.Clone();
            tampered[payloadOffset] ^= 0x01;
            Assert.False(client.TryOpenPacket(tampered, HalyardPacketLayout.Av(keyPos, payloadOffset)));
        }
    }

    [Fact]
    public void Stream_P521_ForVersion17_EstablishesAndOpens()
    {
        var handshakeKey = RandomBytes(16);
        using var client = new HalyardV1SessionCrypto(SyntheticSecrets());

        // Version 17 (0x11) selects P-521: the seam yields a 133-byte pubkey.
        byte[] clientPub = client.GenerateEphemeralPublicKey(protocolVersion: 17);
        Assert.Equal(133, clientPub.Length);

        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using (serverKp)
        {
            Assert.True(client.TryEstablishStream(serverPub, handshakeKey));

            var shared = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
            var s2c = HalyardStreamKeySchedule.DeriveDirection(shared, handshakeKey, HalyardStreamKeySchedule.DirectionServerToClient);
            var serverDown = new HalyardPacketCrypto(s2c);

            const ulong keyPos = 0x00013060;
            const int payloadOffset = 21;
            var plaintext = System.Text.Encoding.ASCII.GetBytes(new string('P', 128));
            var packet = new byte[payloadOffset + plaintext.Length];
            packet[0] = 0x02;
            plaintext.CopyTo(packet, payloadOffset);
            serverDown.CryptPayload(keyPos, packet.AsSpan(payloadOffset)).CopyTo(packet.AsSpan(payloadOffset));
            WriteKeyPos(packet, keyPos);
            var sealed_ = serverDown.SealPacket(keyPos, packet, HalyardPacketCrypto.AvTagOffset);

            Assert.True(client.TryOpenPacket(sealed_, HalyardPacketLayout.Av(keyPos, payloadOffset)));
            Assert.Equal(Hex.String(plaintext), Hex.String(sealed_.AsSpan(payloadOffset)));
        }
    }

    [Fact]
    public void Stream_UpDirection_ClientSealsServerOpens()
    {
        var handshakeKey = RandomBytes(16);
        using var client = new HalyardV1SessionCrypto(SyntheticSecrets());
        byte[] clientPub = client.GenerateEphemeralPublicKey(LiveVersion);
        var (serverKp, serverPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using (serverKp)
        {
            Assert.True(client.TryEstablishStream(serverPub, handshakeKey));

            var serverShared = HalyardStreamKeySchedule.DeriveSharedSecret(serverKp, clientPub);
            var c2s = HalyardStreamKeySchedule.DeriveDirection(serverShared, handshakeKey, HalyardStreamKeySchedule.DirectionClientToServer);
            var serverUp = new HalyardPacketCrypto(c2s);

            const ulong keyPos = 0;
            const int payloadOffset = 12; // feedback: tag@8, payload@12
            var plaintext = System.Text.Encoding.ASCII.GetBytes(new string('I', 45));

            // Client seals an input packet through the seam.
            var packet = new byte[payloadOffset + plaintext.Length];
            packet[0] = 0x0e;
            plaintext.CopyTo(packet, payloadOffset);
            client.SealPacket(packet, HalyardPacketLayout.Feedback(keyPos, payloadOffset));

            // Payload must now be ciphertext, and the server verifies + decrypts it.
            Assert.NotEqual(Hex.String(plaintext), Hex.String(packet.AsSpan(payloadOffset)));
            Assert.True(serverUp.VerifyPacket(keyPos, packet, HalyardPacketCrypto.FeedbackTagOffset));
            var recovered = serverUp.CryptPayload(keyPos, packet.AsSpan(payloadOffset));
            Assert.Equal(Hex.String(plaintext), Hex.String(recovered));
        }
    }

    [Fact]
    public void Passthrough_OpensWithoutDecrypting_AndSealIsNoOp()
    {
        var crypto = new PassthroughHalyardSessionCrypto();
        var packet = System.Text.Encoding.ASCII.GetBytes("....plaintext-payload-stays-put....");
        var original = (byte[])packet.Clone();

        Assert.True(crypto.TryOpenPacket(packet, HalyardPacketLayout.Av(100, 18)));
        Assert.Equal(Hex.String(original), Hex.String(packet)); // unchanged

        crypto.SealPacket(packet, HalyardPacketLayout.Feedback(0, 12));
        Assert.Equal(Hex.String(original), Hex.String(packet)); // unchanged
    }

    private static void WriteKeyPos(byte[] packet, ulong keyPos)
    {
        packet[14] = (byte)(keyPos >> 24);
        packet[15] = (byte)(keyPos >> 16);
        packet[16] = (byte)(keyPos >> 8);
        packet[17] = (byte)keyPos;
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
