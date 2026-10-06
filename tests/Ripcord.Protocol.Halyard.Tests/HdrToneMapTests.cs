using System.Text.RegularExpressions;
using Ripcord.Core.Video;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The HDR-to-SDR tone-map's curve, checked here because the GPU copy of it can only be looked at. The shader in
/// VideoRenderer.cpp mirrors HdrToneMap; the last test holds their constants together.
/// </summary>
public class HdrToneMapTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(100.0)]
    [InlineData(203.0)]
    [InlineData(1000.0)]
    [InlineData(10000.0)]
    public void Pq_RoundTrips(double nits)
        => Assert.Equal(nits, HdrToneMap.PqToNits(HdrToneMap.NitsToPq(nits)), nits * 1e-6 + 1e-9);

    [Fact]
    public void Pq_MatchesST2084AtItsKnownPoints()
    {
        // ST 2084: 100 nits encodes to about 0.508, 1000 nits to about 0.752, 10000 nits to 1.
        Assert.Equal(0.508, HdrToneMap.NitsToPq(100), 3);
        Assert.Equal(0.752, HdrToneMap.NitsToPq(1000), 3);
        Assert.Equal(1.0, HdrToneMap.NitsToPq(10000), 9);
    }

    [Fact]
    public void Eetf_LeavesTheMidtonesAlone_AndLandsThePeakOnSdrWhite()
    {
        Assert.Equal(50.0, HdrToneMap.Eetf(50), 6);
        Assert.Equal(HdrToneMap.SdrPeakNits, HdrToneMap.Eetf(HdrToneMap.SourcePeakNits), 6);
        Assert.Equal(HdrToneMap.SdrPeakNits, HdrToneMap.Eetf(4000), 6);   // above the source peak: held at white
    }

    [Fact]
    public void Eetf_IsMonotonic_AndNeverPassesSdrWhite()
    {
        double previous = 0;
        for (double nits = 0; nits <= 2000; nits += 0.5)
        {
            double mapped = HdrToneMap.Eetf(nits);
            Assert.True(mapped >= previous - 1e-9, $"fell at {nits} nits");
            Assert.True(mapped <= HdrToneMap.SdrPeakNits + 1e-6, $"passed SDR white at {nits} nits");
            previous = mapped;
        }
    }

    [Fact]
    public void Highlights_KeepTheirDetail()
    {
        // The point of the exercise: 500 and 800 nits used to arrive as the same clipped white. They stay apart.
        double at500 = HdrToneMap.ToSdr(HdrToneMap.NitsToPq(500), HdrToneMap.NitsToPq(500), HdrToneMap.NitsToPq(500)).R;
        double at800 = HdrToneMap.ToSdr(HdrToneMap.NitsToPq(800), HdrToneMap.NitsToPq(800), HdrToneMap.NitsToPq(800)).R;

        Assert.True(at500 < at800, $"{at500} vs {at800}");
        Assert.True(at800 < 1.0);
    }

    [Fact]
    public void Black_IsBlack_AndGreyStaysGrey()
    {
        Assert.Equal((0.0, 0.0, 0.0), HdrToneMap.ToSdr(0, 0, 0));

        double pq = HdrToneMap.NitsToPq(203);
        (double r, double g, double b) = HdrToneMap.ToSdr(pq, pq, pq);
        Assert.Equal(r, g, 3);
        Assert.Equal(g, b, 3);
        Assert.InRange(r, 0.8, 0.95);   // reference white sits below SDR white, leaving room for highlights
    }

    [Fact]
    public void ABrightColour_KeepsItsHue_AsItCompresses()
    {
        // A saturated orange at 600 nits: per-channel clipping would push it toward yellow-white. Scaling all three
        // by the brightest channel's roll-off keeps the ratios.
        (double r, double g, double b) = HdrToneMap.ToSdr(
            HdrToneMap.NitsToPq(600), HdrToneMap.NitsToPq(200), HdrToneMap.NitsToPq(20));

        Assert.True(r > g && g > b);
        Assert.True(r < 1.0);
    }

    [Fact]
    public void TheShader_UsesTheSameConstants()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Ripcord.Media.Interop", "VideoRenderer.cpp"));

        // The shader's own constants.
        Assert.Matches(new Regex($@"kSdrPeakNits\s*=\s*{HdrToneMap.SdrPeakNits:0.0}"), source);
        Assert.Matches(new Regex($@"kDisplayGamma\s*=\s*{HdrToneMap.DisplayGamma:0.0}"), source);
        Assert.Contains("1.6605", source);   // BT.2087's first coefficient: the gamut step is present

        // The C++ that chooses the source peak and smooths the measurement.
        Assert.Matches(new Regex($@"kHdrSourcePeakNits\s*=\s*{HdrToneMap.SourcePeakNits:0.0}"), source);
        Assert.Matches(new Regex($@"kToneMapSdrPeakNits\s*=\s*{HdrToneMap.SdrPeakNits:0.0}"), source);
        Assert.Matches(new Regex($@"kSdrContentBelowNits\s*=\s*{HdrToneMap.SdrContentBelowNits:0.0}"), source);
        Assert.Matches(new Regex($@"kHdrContentAboveNits\s*=\s*{HdrToneMap.HdrContentAboveNits:0.0}"), source);
        Assert.Matches(new Regex($@"kPeakRiseRate\s*=\s*{HdrToneMap.PeakTracker.RiseRate}"), source);
        Assert.Matches(new Regex($@"kPeakFallRate\s*=\s*{HdrToneMap.PeakTracker.FallRate}"), source);
    }

    [Theory]
    [InlineData(100.0, 250.0)]     // never below SDR white
    [InlineData(300.0, 300.0)]     // an SDR game in HDR10: its own peak
    [InlineData(400.0, 400.0)]
    [InlineData(500.0, 700.0)]     // between: a blend
    [InlineData(600.0, 1000.0)]    // HDR content: the fixed curve that matched the console
    [InlineData(4000.0, 1000.0)]
    public void SourcePeak_FollowsTheContent(double measured, double expected)
        => Assert.Equal(expected, HdrToneMap.SourcePeakFor(measured), 6);

    [Fact]
    public void AnSdrGameInHdr10_ReachesFullWhite()
    {
        // 2026-10-05: an SDR game's white arrived near 258 nits, small elements to about 300. Tone-mapped from a
        // fixed 1,000-nit peak its white landed at 91%; from the measured peak it is at full white, or within a hair.
        double white = HdrToneMap.NitsToPq(258);
        double fixedWhite = HdrToneMap.ToSdr(white, white, white).R;
        double adaptedWhite = HdrToneMap.ToSdr(white, white, white, HdrToneMap.SourcePeakFor(300)).R;

        Assert.InRange(fixedWhite, 0.90, 0.93);
        Assert.True(adaptedWhite > 0.97, $"{adaptedWhite}");
    }

    [Fact]
    public void Tracker_RisesQuickly_AndFallsSlowly()
    {
        var tracker = new HdrToneMap.PeakTracker();
        Assert.Equal(300.0, tracker.Update(300), 6);   // the first measurement is taken as it is

        for (int i = 0; i < 30; i++) tracker.Update(900);   // half a second of a bright scene
        Assert.True(tracker.Smoothed > 700, $"{tracker.Smoothed}");

        for (int i = 0; i < 30; i++) tracker.Update(300);   // half a second dark again
        Assert.True(tracker.Smoothed > 700, $"fell too fast: {tracker.Smoothed}");   // about 75 nits, where the rise took nearly 500
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ripcord.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
