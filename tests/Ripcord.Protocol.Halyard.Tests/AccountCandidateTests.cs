using Ripcord.Cloud.Halyard;
using Ripcord.Protocol.Halyard.Discovery;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The candidates we advertise for a connection — which is the whole of whether a console on another network
/// can reach us. Offering the wrong set does not fail loudly: our own packets still leave, and the symptom is
/// silence from a peer that was never told where to send.
/// </summary>
public class AccountCandidateTests
{
    private static (string, int) Local => ("10.0.0.7", 63711);

    [Fact]
    public void OffersStunStaticAndLocal_InThatOrder()
    {
        // The captured client's exact shape: the mapping the NAT actually assigned, then the same public
        // address with our own port as a port-preserving guess, then the local address.
        IReadOnlyList<HalyardCandidate> candidates =
            HalyardAccountPairing.OurCandidates(Local, ("198.51.100.202", 1298));

        Assert.Equal(["STUN", "STATIC", "LOCAL"], candidates.Select(c => c.Type));
        Assert.Equal(("198.51.100.202", 1298), (candidates[0].Address, candidates[0].Port));

        // The guess keeps the public address but our own port -- that is what makes it a different guess.
        Assert.Equal(("198.51.100.202", 63711), (candidates[1].Address, candidates[1].Port));
        Assert.Equal(("10.0.0.7", 63711), (candidates[2].Address, candidates[2].Port));
    }

    [Fact]
    public void APortPreservingNat_DoesNotProduceTwoIdenticalCandidates()
    {
        // When the NAT keeps the port, the guess and the mapping are the same address. Sending it twice says
        // nothing the first one did not.
        IReadOnlyList<HalyardCandidate> candidates =
            HalyardAccountPairing.OurCandidates(Local, ("198.51.100.202", 63711));

        Assert.Equal(["STUN", "LOCAL"], candidates.Select(c => c.Type));
    }

    [Fact]
    public void WithNoReflexiveAddress_TheLocalOneStillStands()
    {
        // Discovery fails on a blocked network, and on the console's own network it is unnecessary. Neither is
        // a reason to offer nothing.
        IReadOnlyList<HalyardCandidate> candidates = HalyardAccountPairing.OurCandidates(Local, reflexive: null);

        Assert.Equal(["LOCAL"], candidates.Select(c => c.Type));
    }

    [Fact]
    public void WithNothingKnown_OffersNothingRatherThanSomethingWrong()
        => Assert.Empty(HalyardAccountPairing.OurCandidates(local: null, reflexive: null));
}
