using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ripcord.Core.Net.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 Path-A GCM envelope, end to end with no captures: the test plays the console (its own P-521 key pair,
/// the same Z, the same per-direction sk_p and IV rule) so it can open what the client seals and seal what the
/// client opens. Exercises <see cref="HalyardV2SessionCrypto.EstablishControl"/>, the 16-byte-IV GCM, both
/// directions (sk_9 up / sk_8 down), and that a tampered message is rejected. The byte-exactness of the schedule
/// itself against real hardware is <see cref="V2ControlKeyScheduleTests"/>.
/// </summary>
public class V2ControlEnvelopeTests
{
    [Fact]
    public void Envelope_RoundTrips_InBothDirections()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] clientPub = client.GenerateControlPublicKey();

        var (consoleEcdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        byte[] n16 = RandomNumberGenerator.GetBytes(16);
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        Assert.True(client.EstablishControl(consolePub, n16, k16));

        // The console derives the same Z from the client's public key and the same two sk_p.
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(consoleEcdh, clientPub);
        var skC2S = HalyardStreamKeySchedule.DeriveDirection(z, k16, 0x09);
        var skS2C = HalyardStreamKeySchedule.DeriveDirection(z, k16, 0x08);
        consoleEcdh.Dispose();

        byte[] aad = Encoding.ASCII.GetBytes("RP-Version: 1.0\r\n\r\n");

        // client -> console (sk_9): the client seals, the console opens.
        byte[] up = RandomNumberGenerator.GetBytes(40);
        byte[] sealedUp = client.SealClientToConsole(up, aad, counter: 0);
        Assert.Equal(up, ConsoleOpen(skC2S, n16, aad, sealedUp, 0));

        // console -> client (sk_8): the console seals, the client opens.
        byte[] down = RandomNumberGenerator.GetBytes(24);
        byte[] sealedDown = ConsoleSeal(skS2C, n16, aad, down, 0);
        Assert.Equal(down, client.OpenConsoleToClient(sealedDown, aad, 0));

        // A tampered tag (or body) is rejected, not silently returned.
        sealedDown[0] ^= 0xff;
        Assert.Null(client.OpenConsoleToClient(sealedDown, aad, 0));
    }

    [Fact]
    public void Open_WrongCounterOrAad_IsRejected()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        byte[] clientPub = client.GenerateControlPublicKey();
        var (consoleEcdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        byte[] n16 = RandomNumberGenerator.GetBytes(16);
        byte[] k16 = RandomNumberGenerator.GetBytes(16);
        Assert.True(client.EstablishControl(consolePub, n16, k16));
        byte[] z = HalyardStreamKeySchedule.DeriveSharedSecret(consoleEcdh, clientPub);
        var skS2C = HalyardStreamKeySchedule.DeriveDirection(z, k16, 0x08);
        consoleEcdh.Dispose();

        byte[] aad = Encoding.ASCII.GetBytes("a\r\n\r\n");
        byte[] sealedDown = ConsoleSeal(skS2C, n16, aad, RandomNumberGenerator.GetBytes(16), counter: 1);

        Assert.NotNull(client.OpenConsoleToClient(sealedDown, aad, 1));          // right counter + AAD
        Assert.Null(client.OpenConsoleToClient(sealedDown, aad, 2));             // wrong counter -> wrong IV
        Assert.Null(client.OpenConsoleToClient(sealedDown, Encoding.ASCII.GetBytes("b\r\n\r\n"), 1)); // wrong AAD
    }

    [Fact]
    public void Establish_RejectsBadInputs()
    {
        using var client = new HalyardV2SessionCrypto(TestSecrets.SyntheticControlSecrets());
        client.GenerateControlPublicKey();
        var (ecdh, consolePub) = HalyardStreamKeySchedule.GenerateKeyPair(HalyardStreamCurve.NistP521);
        ecdh.Dispose();

        Assert.False(client.EstablishControl(consolePub, new byte[15], new byte[16])); // nonce not 16
        Assert.False(client.EstablishControl(consolePub, new byte[16], new byte[15])); // k16 not 16
        Assert.False(client.EstablishControl(new byte[10], new byte[16], new byte[16])); // bad public key
    }

    // The console half of the envelope, mirroring HalyardV2SessionCrypto's own IV + GCM rules.
    private static byte[] ConsoleSeal(in HalyardStreamKeySchedule.DirectionKeys sk, byte[] n16, byte[] aad, byte[] pt, ulong counter)
    {
        var (ct, tag) = AesGcmCore.Encrypt(sk.AesKey, Iv(sk, n16, counter), aad, pt);
        return [.. ct, .. tag];
    }

    private static byte[]? ConsoleOpen(in HalyardStreamKeySchedule.DirectionKeys sk, byte[] n16, byte[] aad, byte[] ctTag, ulong counter)
        => AesGcmCore.Decrypt(sk.AesKey, Iv(sk, n16, counter), aad,
                              ctTag.AsSpan(0, ctTag.Length - 16), ctTag.AsSpan(ctTag.Length - 16));

    private static byte[] Iv(in HalyardStreamKeySchedule.DirectionKeys sk, byte[] n16, ulong counter)
    {
        byte[] data = new byte[16 + sizeof(ulong)];
        sk.BaseIv.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(16), counter);
        return HMACSHA256.HashData(n16, data)[..16];
    }
}
