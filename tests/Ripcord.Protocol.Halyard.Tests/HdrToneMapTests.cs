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

        Assert.Matches(new Regex($@"kSourcePeakNits\s*=\s*{HdrToneMap.SourcePeakNits:0.0}"), source);
        Assert.Matches(new Regex($@"kSdrPeakNits\s*=\s*{HdrToneMap.SdrPeakNits:0.0}"), source);
        Assert.Matches(new Regex($@"kDisplayGamma\s*=\s*{HdrToneMap.DisplayGamma:0.0}"), source);
        Assert.Contains("1.6605", source);   // BT.2087's first coefficient: the gamut step is present
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
