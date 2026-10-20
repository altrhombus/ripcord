using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// v2 PIN registration RP-Hmac = HMAC-SHA256(K16_reg, PIN uint32 little-endian) (spec §1). The expected values
/// are an independent (Python) computation of the same formula over the bundled PS5 K16_reg — a cross-check that
/// locks the PIN encoding (decimal → uint32 → little-endian), which is the one thing easy to get wrong. The
/// console-byte-exact confirmation is in the captures (002/003/004/009).
/// </summary>
public class V2RegistrationTests
{
    private static readonly byte[] K16Reg = Convert.FromHexString("2736d42c9327ccbb1322e21133dab093");

    [Theory]
    [InlineData(12345678u, "8297c40f1ed39bcee3781f76f58fa15c42295042bf7450e5fd5557a8d78b9701")]
    [InlineData(1u, "517551546e12a708530e72ad0d448a26b2e374b14a72a855498fa0bd201cc119")]
    [InlineData(99999999u, "f3deb4afad8f2422daa443c12f8897ac88968d20953d4493e5e3e1a612962c4a")]
    public void RegistrationHmac_IsHmacOverPinLittleEndian(uint pin, string expected)
        => Assert.Equal(
            expected,
            Convert.ToHexString(HalyardV2Registration.ComputeRegistrationHmac(K16Reg, pin)).ToLowerInvariant());

    [Fact]
    public void ParsePin_ParsesDecimal_AndRejectsNonDigits()
    {
        Assert.Equal(12345678u, HalyardV2Registration.ParsePin("12345678"));
        Assert.Equal(1u, HalyardV2Registration.ParsePin("1"));
        Assert.Throws<ArgumentException>(() => HalyardV2Registration.ParsePin("12ab5678"));
        Assert.Throws<ArgumentException>(() => HalyardV2Registration.ParsePin("-1"));
        Assert.Throws<ArgumentException>(() => HalyardV2Registration.ParsePin(" 12 "));
    }
}
