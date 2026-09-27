using System.Net;
using Ripcord.Cloud.Halyard;
using Ripcord.Protocol.Halyard.Discovery;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// One decision about which console candidate to talk to, shared by the transport and the ACCEPT. They used to
/// have separate rules, and a third on the pairing route, which parted company when no candidate parsed: the
/// transport fell back to the known host while the ACCEPT named an unparseable candidate, and the media leg
/// threw on it (engine comparisons, 2026-09-26).
/// </summary>
public class ConsoleCandidatesTests
{
    private static readonly Func<IPAddress, bool> HomeNetwork = a => a.ToString().StartsWith("192.168.1.", StringComparison.Ordinal);
    private static readonly Func<IPAddress, bool> Elsewhere = _ => false;

    private static HalyardSignalingCandidate C(string type, string address, int port = 9303) => new(type, address, port);

    [Fact]
    public void OnTheSameNetwork_TheLocalCandidateWins()
    {
        var offered = new[] { C("STATIC", "203.0.113.9"), C("LOCAL", "192.168.1.20") };
        Assert.Equal("192.168.1.20", HalyardConsoleCandidates.Choose(offered, HomeNetwork)!.Address);
    }

    [Fact]
    public void OffNetwork_TheFirstParseableCandidateWins()
    {
        var offered = new[] { C("LOCAL", "not-an-address"), C("STATIC", "203.0.113.9"), C("LOCAL", "192.168.1.20") };
        Assert.Equal("203.0.113.9", HalyardConsoleCandidates.Choose(offered, Elsewhere)!.Address);
    }

    [Theory]
    [InlineData("10")]            // IPAddress.TryParse takes a bare integer
    [InlineData("console.local")]
    [InlineData("::1")]
    public void OnlyADottedQuadCounts(string address)
        => Assert.Null(HalyardConsoleCandidates.Choose([C("LOCAL", address)], HomeNetwork));

    [Fact]
    public void Resolve_TheEndpointAndTheNamedCandidateAreOneChoice()
    {
        var offered = new[] { C("STATIC", "203.0.113.9", 61000), C("LOCAL", "192.168.1.20", 9303) };
        var resolved = HalyardConsoleCandidates.Resolve(offered, "192.168.1.20", 9303, HomeNetwork)!.Value;
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 9303), resolved.Endpoint);
        Assert.Same(offered[1], resolved.Named);
    }

    /// <summary>The case that used to diverge: nothing parses, so both fall back to the known host.</summary>
    [Fact]
    public void Resolve_WithNothingParseable_FallsBackToTheKnownHostForBoth()
    {
        var offered = new[] { C("LOCAL", "console.local", 1234) };
        var resolved = HalyardConsoleCandidates.Resolve(offered, "192.168.1.20", 9303, HomeNetwork)!.Value;
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 9303), resolved.Endpoint);
        Assert.Equal("192.168.1.20", resolved.Named.Address);
        Assert.Equal(9303, resolved.Named.Port);
    }

    [Fact]
    public void Resolve_WithNoCandidateAndNoKnownAddress_IsAbsentRatherThanAThrow()
        => Assert.Null(HalyardConsoleCandidates.Resolve([C("LOCAL", "console.local")], "", 9303, HomeNetwork));
}
