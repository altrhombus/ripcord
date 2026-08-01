using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>Tests the pure MachineGuid -> 16-byte device-id parse used for the RP-Did control field.</summary>
public class HalyardDeviceIdentityTests
{
    [Fact]
    public void TryParseMachineGuid_StripsDashesAndHexDecodes()
    {
        Assert.True(HalyardDeviceIdentity.TryParseMachineGuid("00112233-4455-6677-8899-aabbccddeeff", out var bytes));
        Assert.Equal("00112233445566778899aabbccddeeff", Hex.String(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00112233-4455-6677-8899-aabbccddee")]   // too short
    [InlineData("00112233-4455-6677-8899-aabbccddeeffff")] // too long
    [InlineData("zz112233-4455-6677-8899-aabbccddeeff")]  // non-hex
    public void TryParseMachineGuid_RejectsMalformed(string input)
    {
        Assert.False(HalyardDeviceIdentity.TryParseMachineGuid(input, out var bytes));
        Assert.Empty(bytes);
    }
}
