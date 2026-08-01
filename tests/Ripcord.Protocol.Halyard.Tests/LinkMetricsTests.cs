using System.Net;
using Ripcord.Core.Net.Udp;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The MTU and round-trip values declared to the console in the launchSpec.
///
/// <para>
/// Both were hardcoded before — <c>mtu: 1454</c> and <c>rtt: 0</c> — so the console was told the link was an
/// ordinary Ethernet path with instantaneous latency regardless of what it actually was. These pin the arithmetic
/// that replaced them, including the clamps that stop a measurement being worse than the old constant.
/// </para>
/// </summary>
public class LinkMetricsTests
{
    // ---- MTU ----

    [Fact]
    public void AnOrdinaryEthernetPathReproducesTheVendorValueExactly()
    {
        // This is the evidence for the 46-byte overhead figure: 1500 - 46 = 1454, the wire-confirmed value a real
        // client sends. If this ever fails, the overhead constant has drifted away from the observation.
        Assert.Equal(LinkMetrics.VendorMtu, LinkMetrics.MtuToDeclare(1500));
        Assert.Equal(1454, LinkMetrics.MtuToDeclare(1500));
    }

    [Fact]
    public void ASmallerPathIsHonoured()
    {
        // The case that motivates measuring at all: a VPN or PPPoE link genuinely below 1500, where declaring 1454
        // means every datagram we describe is larger than the path can carry.
        Assert.Equal(1400 - LinkMetrics.MtuOverheadBytes, LinkMetrics.MtuToDeclare(1400));
        Assert.Equal(1280 - LinkMetrics.MtuOverheadBytes, LinkMetrics.MtuToDeclare(1280));
    }

    [Fact]
    public void AJumboFrameLinkIsStillCappedAtTheVendorValue()
    {
        // A 9000-byte LAN would otherwise have us advertise ~8950, which no real client does and the console has
        // never been observed to be asked for. Lower is measured; higher would be a guess.
        Assert.Equal(LinkMetrics.VendorMtu, LinkMetrics.MtuToDeclare(9000));
    }

    [Fact]
    public void AnAbsurdlySmallInterfaceMtuIsFlooredNotForwarded()
    {
        // A tunnel adapter reporting something tiny must not become the number the console plans around.
        Assert.Equal(LinkMetrics.MinimumMtu, LinkMetrics.MtuToDeclare(100));
        Assert.Equal(LinkMetrics.MinimumMtu, LinkMetrics.MtuToDeclare(1));
    }

    [Fact]
    public void AnUnknownInterfaceMtuFallsBackToTheVendorValue()
    {
        // "Could not measure" must behave exactly as the old hardcoded constant did — this change should never make
        // a working configuration worse.
        Assert.Equal(LinkMetrics.VendorMtu, LinkMetrics.MtuToDeclare(null));
        Assert.Equal(LinkMetrics.VendorMtu, LinkMetrics.MtuToDeclare(0));
        Assert.Equal(LinkMetrics.VendorMtu, LinkMetrics.MtuToDeclare(-1));
    }

    // ---- round-trip time ----

    [Fact]
    public void TheMinimumSampleIsChosenNotTheMean()
    {
        // Each sample is network delay PLUS whatever work that particular reply cost the console, and those differ
        // by message type. The minimum is the sample least contaminated by the console's own latency.
        Assert.Equal(3.0, LinkMetrics.RoundTripToDeclare([12.0, 3.0, 40.0]));
    }

    [Fact]
    public void NoSamplesMeansNullRatherThanZero()
    {
        // The distinction that matters: 0 ms is a measurement, and declaring it when nothing was measured told the
        // console the link was instantaneous.
        Assert.Null(LinkMetrics.RoundTripToDeclare([]));
    }

    [Fact]
    public void NonFiniteAndNegativeSamplesAreIgnored()
    {
        Assert.Equal(5.0, LinkMetrics.RoundTripToDeclare([double.NaN, -3.0, 5.0, double.PositiveInfinity]));
    }

    [Fact]
    public void AllSamplesUnusableMeansNull()
    {
        Assert.Null(LinkMetrics.RoundTripToDeclare([double.NaN, -1.0]));
    }

    [Fact]
    public void AGenuineZeroIsKept()
    {
        // A sub-millisecond LAN really can round-trip in under 0.5 ms; that is a measurement and must survive.
        Assert.Equal(0.0, LinkMetrics.RoundTripToDeclare([0.0, 2.0]));
    }

    // ---- interface lookup ----

    [Fact]
    public void TheInterfaceLookupNeverThrowsAndSendsNothing()
    {
        // Runs during connect, so it must never be the reason a session fails to start. A loopback and an
        // unroutable address are both answered rather than thrown; either result is acceptable, a crash is not.
        int? loopback = LinkMetrics.InterfaceMtuTowards(IPAddress.Loopback);
        int? documentation = LinkMetrics.InterfaceMtuTowards(IPAddress.Parse("192.0.2.10"));

        Assert.True(loopback is null || loopback > 0);
        Assert.True(documentation is null || documentation > 0);
    }

    [Fact]
    public void WhateverTheLookupReturnsProducesAUsableMtu()
    {
        // The composed result is what reaches the console, so it must be in range on any host, including CI.
        int declared = LinkMetrics.MtuToDeclare(LinkMetrics.InterfaceMtuTowards(IPAddress.Loopback));

        Assert.InRange(declared, LinkMetrics.MinimumMtu, LinkMetrics.VendorMtu);
    }
}
