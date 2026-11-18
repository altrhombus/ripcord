using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 session crypto as constructed: it reports nothing established, and — the design decision worth
/// pinning — the v1 per-field control methods are a deliberate <see cref="NotSupportedException"/> (v2 uses
/// the GCM envelope on <see cref="IHalyardV2ControlCrypto"/>). The stream key agreement establishes, and the
/// c2s seals leave a packet untouched before it has.
/// </summary>
public class V2SessionCryptoStubTests
{
    private static HalyardV2SessionCrypto New() => new(TestSecrets.SyntheticControlSecrets());

    [Fact]
    public void Stub_ReportsNothingEstablished()
    {
        using var crypto = New();
        Assert.False(((IHalyardSessionCrypto)crypto).IsControlEstablished);
        Assert.False(((IHalyardSessionCrypto)crypto).IsStreamEstablished);
        Assert.False(((IHalyardV2ControlCrypto)crypto).IsControlEstablished);
    }

    [Fact]
    public void V1FieldCrypto_IsDeliberatelyNotSupported()
    {
        using var crypto = New();
        // v2 does not field-encrypt the control plane; this is a contract, not an unfinished path.
        Assert.Throws<NotSupportedException>(() => crypto.EncryptControlField(0, new byte[16]));
        Assert.Throws<NotSupportedException>(() => crypto.DecryptControlField(0, new byte[16]));
    }

    [Fact]
    public void StreamKeyAgreementAndAvPackets_AreBuilt()
    {
        using var crypto = New();
        // The stream key agreement (its s2c A/V per-packet crypto has KATs in V2StreamPacketVectorTests) does
        // not throw; X arrives as the handshakeKey argument.
        byte[] pub = crypto.GenerateEphemeralPublicKey(0x11);
        Assert.Equal(HalyardStreamKeySchedule.PublicKeyLengthP521, pub.Length);
        Assert.True(crypto.TryEstablishStream(
            New().GenerateEphemeralPublicKey(0x11), new byte[16]));
        Assert.True(crypto.IsStreamEstablished);
    }

    [Fact]
    public void C2sSendAuth_IsImplemented_AndNoOpsBeforeStreamEstablished()
    {
        using var crypto = New();
        // The c2s send-authentication path (byte-level round-trip in V2C2sSendAuthTests) has no key before the
        // stream key agreement, so each Seal is a safe no-op — not a throw — leaving the packet untouched
        // (nothing is ever sent before keys exist anyway).
        byte[] control = new byte[32];
        crypto.SealControlMessage(control);
        Assert.All(control, b => Assert.Equal(0, b));

        byte[] lossReport = new byte[32];
        crypto.SealLossReport(lossReport);
        Assert.All(lossReport, b => Assert.Equal(0, b));

        byte[] input = new byte[32];
        crypto.SealInputPacket(input, 20);
        Assert.All(input, b => Assert.Equal(0, b));
    }
}
