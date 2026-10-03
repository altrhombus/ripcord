namespace Ripcord.Core.Video;

/// <summary>
/// The HDR-to-SDR tone-map the renderer applies when an HDR10 stream is shown on an SDR display: the reference for
/// the HLSL in <c>VideoRenderer.cpp</c> (<c>kUpscaleShaderSource</c>), which mirrors it step for step so the curve
/// can be tested here rather than only on a GPU. Change one and change the other; a test checks the constants agree.
///
/// <para>
/// <b>Why Ripcord has one.</b> With the console's HDR on, the SDR stream it sends is its own conversion of the
/// game's HDR picture, and that conversion clips: 10–12% of a bright frame arrives pinned at white (2026-10-02,
/// research log). The HDR stream keeps the detail, so Ripcord asks for HDR and converts it itself. The GPU driver
/// can convert too, but every vendor's came out different and none matched the console (2026-10-01).
/// </para>
///
/// <para>
/// <b>The steps</b>, all from public references:
/// PQ to linear light (SMPTE ST 2084); BT.2020 to BT.709 primaries (ITU-R BT.2087); the BT.2390 EETF roll-off,
/// applied to the brightest channel and the colour scaled with it, so a highlight compresses without changing hue;
/// then gamma 2.2 for the SDR swap chain, whose colour space is <c>RGB_FULL_G22_NONE_P709</c>.
/// </para>
///
/// <para>
/// <b>The two luminances are assumptions,</b> tuned against the console's own screenshots rather than read from the
/// stream: <see cref="SourcePeakNits"/> is the brightest the stream is taken to carry, and <see cref="SdrPeakNits"/>
/// is the level that becomes SDR full white.
/// </para>
/// </summary>
public static class HdrToneMap
{
    /// <summary>The brightest the HDR stream is taken to carry. HDR10's common mastering peak [X]; not read from the stream.</summary>
    public const double SourcePeakNits = 1000.0;

    /// <summary>The luminance that becomes SDR full white. Highlights above it are compressed into the top of the range.</summary>
    public const double SdrPeakNits = 250.0;

    /// <summary>The SDR swap chain's transfer: gamma 2.2.</summary>
    public const double DisplayGamma = 2.2;

    // SMPTE ST 2084.
    private const double M1 = 2610.0 / 16384.0;
    private const double M2 = 2523.0 / 4096.0 * 128.0;
    private const double C1 = 3424.0 / 4096.0;
    private const double C2 = 2413.0 / 4096.0 * 32.0;
    private const double C3 = 2392.0 / 4096.0 * 32.0;

    /// <summary>PQ signal (0..1) to luminance in nits.</summary>
    public static double PqToNits(double e)
    {
        double p = Math.Pow(Math.Max(e, 0), 1.0 / M2);
        return 10000.0 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1.0 / M1);
    }

    /// <summary>Luminance in nits to PQ signal (0..1).</summary>
    public static double NitsToPq(double nits)
    {
        double y = Math.Pow(Math.Max(nits, 0) / 10000.0, M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    /// <summary>
    /// BT.2390's EETF from <see cref="SourcePeakNits"/> to <see cref="SdrPeakNits"/>, black at zero for both: the
    /// identity below the knee, a Hermite spline above it that meets the target peak with zero slope.
    /// </summary>
    public static double Eetf(double nits)
    {
        double sourcePq = NitsToPq(SourcePeakNits);
        double e1 = Math.Min(NitsToPq(nits) / sourcePq, 1.0);
        double maxLum = NitsToPq(SdrPeakNits) / sourcePq;
        double ks = 1.5 * maxLum - 0.5;

        double e2 = e1;
        if (e1 > ks)
        {
            double t = (e1 - ks) / (1 - ks);
            double t2 = t * t, t3 = t2 * t;
            e2 = (2 * t3 - 3 * t2 + 1) * ks + (t3 - 2 * t2 + t) * (1 - ks) + (-2 * t3 + 3 * t2) * maxLum;
        }

        return PqToNits(e2 * sourcePq);
    }

    /// <summary>
    /// One pixel: PQ-encoded BT.2020 RGB (0..1 each) to gamma-encoded BT.709 RGB (0..1 each) for an SDR display.
    /// </summary>
    public static (double R, double G, double B) ToSdr(double rPq, double gPq, double bPq)
    {
        double r2020 = PqToNits(rPq), g2020 = PqToNits(gPq), b2020 = PqToNits(bPq);

        // ITU-R BT.2087, linear BT.2020 to linear BT.709. Out-of-gamut colours come out negative and are clipped.
        double r = Math.Max(1.6605 * r2020 - 0.5876 * g2020 - 0.0728 * b2020, 0);
        double g = Math.Max(-0.1246 * r2020 + 1.1329 * g2020 - 0.0083 * b2020, 0);
        double b = Math.Max(-0.0182 * r2020 - 0.1006 * g2020 + 1.1187 * b2020, 0);

        // Roll off the brightest channel and scale the others with it, so hue holds as a highlight compresses.
        double peak = Math.Max(r, Math.Max(g, b));
        double scale = peak > 0 ? Eetf(peak) / peak / SdrPeakNits : 0;

        return (Encode(r * scale), Encode(g * scale), Encode(b * scale));

        static double Encode(double linear) => Math.Pow(Math.Clamp(linear, 0, 1), 1.0 / DisplayGamma);
    }
}
