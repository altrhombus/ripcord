using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>The v1/v2 init-flavour selection rule (spec §3).</summary>
public class V2InitSelectionTests
{
    [Theory]
    // tokenHeld, consoleSupportsGcm, v2PreviouslyFailed -> expected
    [InlineData(true, false, false, HalyardInitFlavor.V2)]   // a held token => v2 (reconnect)
    [InlineData(false, true, false, HalyardInitFlavor.V2)]   // console advertised GCM, no token yet => v2 (first session)
    [InlineData(true, true, false, HalyardInitFlavor.V2)]    // both => v2
    [InlineData(false, false, false, HalyardInitFlavor.V1)]  // neither (incl. every PS4) => v1
    [InlineData(true, true, true, HalyardInitFlavor.V1)]     // a v2 attempt already failed => fall back to v1
    [InlineData(false, true, true, HalyardInitFlavor.V1)]    // fallback wins even with GCM advertised
    public void Select_FollowsTheSpecRule(bool tokenHeld, bool gcm, bool v2Failed, HalyardInitFlavor expected)
        => Assert.Equal(expected, HalyardInitSelection.Select(tokenHeld, gcm, v2Failed));
}
