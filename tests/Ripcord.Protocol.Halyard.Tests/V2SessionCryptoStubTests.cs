using Ripcord.Protocol.Halyard.Common.Crypto;
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
    public void UnbuiltTransforms_ThrowNotImplemented()
    {
        using var crypto = New();
        Assert.Throws<NotImplementedException>(() => crypto.GenerateControlPublicKey());
        Assert.Throws<NotImplementedException>(() => crypto.GenerateEphemeralPublicKey(0x11));
    }
}
