using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 (PS5) c2s send-authentication: <see cref="HalyardV2SessionCrypto.SealControlMessage"/>,
/// <see cref="HalyardV2SessionCrypto.SealCongestionPacket"/> and
/// <see cref="HalyardV2SessionCrypto.SealFeedbackPacket"/>. Each is a v1 layout under the v1 client->console
/// (dir-2) key with the v2 per-format header offsets and the v2 GMAC window rule; the offsets were confirmed
/// byte-exact against our own captures 003/004 (V2-SPEC §; Fork H 004 482/482, 003 287/287), whose fixtures live
/// in the captures tree.
///
/// <para>These tests do not KAT the console (no captured material); they prove the seal is <b>internally
/// byte-exact</b>: a peer that derives the same dir-2 key (one real ECDH side owned by the test) re-verifies the
/// GMAC at each format's offsets and decrypts the feedback payload, and the three formats share one advancing
/// key position. The console-byte-exactness of the offsets themselves is the captures-tree KAT; the underlying
/// GMAC/CTR primitives are KAT'd s2c in <see cref="V2StreamPacketVectorTests"/>.</para>
/// </summary>
public class V2C2sSendAuthTests
{
    // v2 c2s header offsets (mirror of the constants in HalyardV2SessionCrypto).
    private const int ControlTagOffset = 5, ControlKeyPosOffset = 9;
    private const int CongestionTagOffset = 15, CongestionKeyPosOffset = 19, CongestionLength = 23;
    private const int FeedbackKeyPosOffset = 12, FeedbackTagOffset = 16, FeedbackPayloadOffset = 20;

    private static readonly byte[] X = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"); // synthetic X

    /// <summary>
    /// Establish a client's stream crypto and return it together with a verifier keyed from the same dir-2
    /// material — derived by owning the peer's ECDH side, exactly as a console would.
    /// </summary>
    private static (HalyardV2SessionCrypto client, HalyardPacketCrypto verifier) EstablishPair()
    {
        var (peerEcdh, peerPub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        using var _peer = peerEcdh;

        var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] clientPub = client.GenerateEphemeralPublicKey(0x11); // P-521
        Assert.True(client.TryEstablishStream(peerPub, X));

        // ECDH is symmetric: Z(peer_priv, clientPub) == Z(client_priv, peerPub) the client used for _send.
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(peerEcdh, clientPub);
        var dir2 = HalyardStreamKeySchedule.DeriveDirection(z, X, HalyardStreamKeySchedule.DirectionClientToServer);
        var verifier = new HalyardPacketCrypto(dir2, HalyardGmacWindowRule.FromKeyPosMinusOne);
        return (client, verifier);
    }

    private static bool TagMatches(HalyardPacketCrypto verifier, ulong keyPos, byte[] packet, int tagOffset, bool zeroKeyPos)
    {
        byte[] expected = verifier.ComputeTag(keyPos, packet, tagOffset, zeroKeyPos);
        return expected.AsSpan().SequenceEqual(packet.AsSpan(tagOffset, HalyardPacketCrypto.TagLength));
    }

    [Fact]
    public void SealControlMessage_WritesKeyPosAndAGmacThatThePeerVerifies()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[32];
            Random.Shared.NextBytes(packet);
            client.SealControlMessage(packet);

            ulong keyPos = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(ControlKeyPosOffset, 4));
            Assert.Equal(0u, keyPos); // first seal on a fresh counter
            // Control AAD zeroes tag + key_pos.
            Assert.True(TagMatches(verifier, keyPos, packet, ControlTagOffset, zeroKeyPos: true));
            Assert.NotEqual("00000000", Convert.ToHexString(packet.AsSpan(ControlTagOffset, 4)));
        }
    }

    [Fact]
    public void SealCongestionPacket_UsesThe23ByteOffsets_AndVerifies()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[CongestionLength];
            Random.Shared.NextBytes(packet);
            client.SealCongestionPacket(packet);

            ulong keyPos = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(CongestionKeyPosOffset, 4));
            Assert.Equal(0u, keyPos);
            // Congestion AAD zeroes tag + key_pos (like control).
            Assert.True(TagMatches(verifier, keyPos, packet, CongestionTagOffset, zeroKeyPos: true));
            Assert.NotEqual("00000000", Convert.ToHexString(packet.AsSpan(CongestionTagOffset, 4)));
        }
    }

    [Fact]
    public void SealFeedbackPacket_EncryptsPayload_AndTheGmacVerifiesTagOnly()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[FeedbackPayloadOffset + 28];
            Random.Shared.NextBytes(packet.AsSpan(FeedbackPayloadOffset));
            byte[] originalPayload = packet.AsSpan(FeedbackPayloadOffset).ToArray();

            client.SealFeedbackPacket(packet, FeedbackPayloadOffset);

            ulong keyPos = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(FeedbackKeyPosOffset, 4));
            Assert.Equal(0u, keyPos);

            // The payload was AES-CTR encrypted in place.
            Assert.False(packet.AsSpan(FeedbackPayloadOffset).SequenceEqual(originalPayload));

            // Feedback AAD zeroes ONLY the tag (not key_pos), matching A/V.
            Assert.True(TagMatches(verifier, keyPos, packet, FeedbackTagOffset, zeroKeyPos: false));
            Assert.True(verifier.VerifyPacket(keyPos, packet, FeedbackTagOffset));

            // And the peer recovers the plaintext by CTR-decrypting with the same key/nonce.
            verifier.CryptPayloadInPlace(keyPos, packet.AsSpan(FeedbackPayloadOffset));
            Assert.True(packet.AsSpan(FeedbackPayloadOffset).SequenceEqual(originalPayload));
        }
    }

    [Fact]
    public void AllThreeFormats_ShareOneAdvancingKeyPosition()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            // control: len 32 -> advance 32; congestion: len 23 -> advance 32; feedback: len 36 -> advance 48.
            byte[] control = new byte[32];
            byte[] congestion = new byte[CongestionLength];
            byte[] feedback = new byte[FeedbackPayloadOffset + 16];

            client.SealControlMessage(control);
            client.SealCongestionPacket(congestion);
            client.SealFeedbackPacket(feedback, FeedbackPayloadOffset);

            ulong kpControl = BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(ControlKeyPosOffset, 4));
            ulong kpCongestion = BinaryPrimitives.ReadUInt32BigEndian(congestion.AsSpan(CongestionKeyPosOffset, 4));
            ulong kpFeedback = BinaryPrimitives.ReadUInt32BigEndian(feedback.AsSpan(FeedbackKeyPosOffset, 4));

            Assert.Equal(0u, kpControl);
            Assert.Equal(32u, kpCongestion); // aligned(32)
            Assert.Equal(64u, kpFeedback);   // aligned(32) + aligned(23)=32

            // Each remains individually verifiable at its own (distinct) key position.
            Assert.True(TagMatches(verifier, kpControl, control, ControlTagOffset, zeroKeyPos: true));
            Assert.True(TagMatches(verifier, kpCongestion, congestion, CongestionTagOffset, zeroKeyPos: true));
            Assert.True(verifier.VerifyPacket(kpFeedback, feedback, FeedbackTagOffset));
        }
    }

    [Fact]
    public void SealMethods_AreNoOpsBeforeStreamEstablished()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] packet = new byte[32];

        client.SealControlMessage(packet);
        client.SealCongestionPacket(packet);
        client.SealFeedbackPacket(packet, FeedbackPayloadOffset);

        Assert.All(packet, b => Assert.Equal(0, b)); // untouched: nothing is sent before keys exist
    }
}
