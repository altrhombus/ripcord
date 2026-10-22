using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 session-crypto seam-and-stub: it is constructible, reports nothing established, and — the design
/// decision worth pinning — the v1 per-field control methods are a deliberate <see cref="NotSupportedException"/>
/// (v2 uses the GCM envelope on <see cref="IHalyardV2ControlCrypto"/>), while the not-yet-built transforms are
/// <see cref="NotImplementedException"/>. When a phase lands, its assertion here flips from "throws" to a real
/// check.
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
        // Phase 2: the stream key agreement + s2c A/V per-packet crypto are implemented (KATs in
        // V2StreamPacketVectorTests). They no longer throw; X arrives as the handshakeKey argument.
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
        // The c2s send-authentication path is implemented (byte-level round-trip in V2C2sSendAuthTests). Before
        // the stream key agreement it has no key, so each Seal is a safe no-op — not a throw — leaving the packet
        // untouched (nothing is ever sent before keys exist anyway).
        byte[] control = new byte[32];
        crypto.SealControlMessage(control);
        Assert.All(control, b => Assert.Equal(0, b));

        byte[] congestion = new byte[32];
        crypto.SealCongestionPacket(congestion);
        Assert.All(congestion, b => Assert.Equal(0, b));

        byte[] feedback = new byte[32];
        crypto.SealFeedbackPacket(feedback, 20);
        Assert.All(feedback, b => Assert.Equal(0, b));
    }
}
