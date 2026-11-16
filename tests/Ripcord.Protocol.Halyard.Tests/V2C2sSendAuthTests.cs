using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 (PS5) c2s send-authentication: <see cref="HalyardV2SessionCrypto.SealControlMessage"/>,
/// <see cref="HalyardV2SessionCrypto.SealLossReport"/> and
/// <see cref="HalyardV2SessionCrypto.SealInputPacket"/>. Each is a v1 layout under the v1 client->console
/// (dir-2) key with the v2 per-format header offsets and the v2 GMAC window rule; the offsets were confirmed
/// byte-exact against our own captures 003/004 (V2-SPEC §; Fork H 004 482/482, 003 287/287), whose fixtures live
/// in the captures tree.
///
/// <para>These tests do not KAT the console (no captured material); they prove the seal is <b>internally
/// byte-exact</b>: a peer that derives the same dir-2 key (one real ECDH side owned by the test) re-verifies the
/// GMAC at each format's offsets and decrypts the input payload, and the three formats share one advancing
/// keystream offset. The console-byte-exactness of the offsets themselves is the captures-tree KAT; the underlying
/// GMAC/CTR primitives are KAT'd s2c in <see cref="V2StreamPacketVectorTests"/>.</para>
/// </summary>
public class V2C2sSendAuthTests
{
    // v2 c2s header offsets (mirror of the constants in HalyardV2SessionCrypto).
    private const int ControlTagOffset = 5, ControlKeystreamOffsetAt = 9;
    private const int LossReportTagOffset = 15, LossReportKeystreamOffsetAt = 19, LossReportLength = 23;
    private const int InputKeystreamOffsetAt = 12, InputTagOffset = 16, InputPayloadOffset = 20;

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
        var verifier = new HalyardPacketCrypto(dir2);
        return (client, verifier);
    }

    private static bool TagMatches(HalyardPacketCrypto verifier, ulong keystreamOffset, byte[] packet, int tagOffset, bool zeroKeystreamOffset)
    {
        byte[] expected = verifier.ComputeTag(keystreamOffset, packet, tagOffset, zeroKeystreamOffset);
        return expected.AsSpan().SequenceEqual(packet.AsSpan(tagOffset, HalyardPacketCrypto.TagLength));
    }

    [Fact]
    public void SealControlMessage_WritesKeystreamOffsetAndAGmacThatThePeerVerifies()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[32];
            Random.Shared.NextBytes(packet);
            client.SealControlMessage(packet);

            ulong keystreamOffset = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(ControlKeystreamOffsetAt, 4));
            Assert.Equal(0u, keystreamOffset); // first seal on a fresh counter
            // Control AAD zeroes tag + keystream_offset.
            Assert.True(TagMatches(verifier, keystreamOffset, packet, ControlTagOffset, zeroKeystreamOffset: true));
            Assert.NotEqual("00000000", Convert.ToHexString(packet.AsSpan(ControlTagOffset, 4)));
        }
    }

    [Fact]
    public void SealLossReport_UsesThe23ByteOffsets_AndVerifies()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[LossReportLength];
            Random.Shared.NextBytes(packet);
            client.SealLossReport(packet);

            ulong keystreamOffset = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(LossReportKeystreamOffsetAt, 4));
            Assert.Equal(0u, keystreamOffset);
            // Loss-report AAD zeroes tag + keystream_offset (like control).
            Assert.True(TagMatches(verifier, keystreamOffset, packet, LossReportTagOffset, zeroKeystreamOffset: true));
            Assert.NotEqual("00000000", Convert.ToHexString(packet.AsSpan(LossReportTagOffset, 4)));
        }
    }

    [Fact]
    public void SealInputPacket_EncryptsPayload_AndTheGmacVerifiesTagOnly()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            byte[] packet = new byte[InputPayloadOffset + 28];
            Random.Shared.NextBytes(packet.AsSpan(InputPayloadOffset));
            byte[] originalPayload = packet.AsSpan(InputPayloadOffset).ToArray();

            client.SealInputPacket(packet, InputPayloadOffset);

            ulong keystreamOffset = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(InputKeystreamOffsetAt, 4));
            Assert.Equal(0u, keystreamOffset);

            // The payload was AES-CTR encrypted in place.
            Assert.False(packet.AsSpan(InputPayloadOffset).SequenceEqual(originalPayload));

            // Input AAD zeroes ONLY the tag (not keystream_offset), matching A/V.
            Assert.True(TagMatches(verifier, keystreamOffset, packet, InputTagOffset, zeroKeystreamOffset: false));
            Assert.True(verifier.VerifyPacket(keystreamOffset, packet, InputTagOffset));

            // And the peer recovers the plaintext by CTR-decrypting with the same key/nonce.
            verifier.CryptPayloadInPlace(keystreamOffset, packet.AsSpan(InputPayloadOffset));
            Assert.True(packet.AsSpan(InputPayloadOffset).SequenceEqual(originalPayload));
        }
    }

    [Fact]
    public void AllThreeFormats_ShareOneAdvancingKeystreamOffset()
    {
        var (client, verifier) = EstablishPair();
        using (client)
        using (verifier)
        {
            // control: len 32 -> advance 32; loss report: len 23 -> advance 32; input: len 36 -> advance 48.
            byte[] control = new byte[32];
            byte[] lossReport = new byte[LossReportLength];
            byte[] input = new byte[InputPayloadOffset + 16];

            client.SealControlMessage(control);
            client.SealLossReport(lossReport);
            client.SealInputPacket(input, InputPayloadOffset);

            ulong kpControl = BinaryPrimitives.ReadUInt32BigEndian(control.AsSpan(ControlKeystreamOffsetAt, 4));
            ulong koLossReport = BinaryPrimitives.ReadUInt32BigEndian(lossReport.AsSpan(LossReportKeystreamOffsetAt, 4));
            ulong koInput = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(InputKeystreamOffsetAt, 4));

            Assert.Equal(0u, kpControl);
            Assert.Equal(32u, koLossReport); // aligned(32)
            Assert.Equal(64u, koInput);   // aligned(32) + aligned(23)=32

            // Each remains individually verifiable at its own (distinct) keystream offset.
            Assert.True(TagMatches(verifier, kpControl, control, ControlTagOffset, zeroKeystreamOffset: true));
            Assert.True(TagMatches(verifier, koLossReport, lossReport, LossReportTagOffset, zeroKeystreamOffset: true));
            Assert.True(verifier.VerifyPacket(koInput, input, InputTagOffset));
        }
    }

    [Fact]
    public void SealMethods_AreNoOpsBeforeStreamEstablished()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] packet = new byte[32];

        client.SealControlMessage(packet);
        client.SealLossReport(packet);
        client.SealInputPacket(packet, InputPayloadOffset);

        Assert.All(packet, b => Assert.Equal(0, b)); // untouched: nothing is sent before keys exist
    }
}
