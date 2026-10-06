namespace Ripcord.Core.Video;

/// <summary>
/// The HDR-to-SDR tone-map the renderer applies when an HDR10 stream is shown on an SDR display: the reference for
/// the HLSL in <c>VideoRenderer.cpp</c> (<c>kUpscaleShaderSource</c>) and the C++ that chooses its source peak, which
/// mirror it step for step so the curve can be tested here rather than only on a GPU. Change one and change the
/// others; a test checks the constants agree.
///
/// <para>
/// <b>Why Ripcord has one.</b> With the console's HDR on, the SDR stream it sends is its own conversion of the
/// game's HDR picture, and that conversion clips: 10–23% of a bright frame arrives pinned at white (2026-10-02 and
/// 2026-10-05, research log). The HDR stream keeps the detail, so Ripcord asks for HDR and converts it itself. The GPU
/// driver can convert too, but every vendor's came out different and none matched the console (2026-10-01).
/// </para>
///
/// <para>
/// <b>The steps</b>, all from public references:
/// PQ to linear light (SMPTE ST 2084); BT.2020 to BT.709 primaries (ITU-R BT.2087); the BT.2390 EETF roll-off from
/// the source peak to <see cref="SdrPeakNits"/>, applied to the brightest channel and the colour scaled with it, so a
/// highlight compresses without changing hue; then gamma 2.2 for the SDR swap chain, <c>RGB_FULL_G22_NONE_P709</c>.
/// </para>
///
/// <para>
/// <b>The source peak is measured, within limits.</b> The stream carries no metadata saying how bright it is
/// (2026-10-05). An SDR game arrives inside HDR10 with its white near 255 nits, and a curve that assumes 1,000 left
/// that white at 91%; an HDR game peaks far higher, and the fixed 1,000-nit curve is the one that tracked the
/// console's own screenshot. So the renderer measures the picture's peak (the brightest luma of 32×18 tiles,
/// <see cref="PeakOfTiles"/>; not the brightest channel, which reads saturated colours far too high), smooths it
/// with <see cref="PeakTracker"/>, and <see cref="SourcePeakFor"/> turns that into the curve's source peak: the
/// measurement itself for SDR-like content, 1,000 nits for HDR content, a blend between.
/// </para>
/// </summary>
public static class HdrToneMap
{
    /// <summary>
    /// The source peak assumed for HDR content: HDR10's common mastering peak [X], and the curve that matched the
    /// console's own screenshot (2026-10-03). Not read from the stream, which does not say.
    /// </summary>
    public const double SourcePeakNits = 1000.0;

    /// <summary>The luminance that becomes SDR full white. Highlights above it are compressed into the top of the range.</summary>
    public const double SdrPeakNits = 250.0;

    /// <summary>The SDR swap chain's transfer: gamma 2.2.</summary>
    public const double DisplayGamma = 2.2;

    /// <summary>
    /// A measured peak at or below this is SDR-like content in an HDR wrapper, tone-mapped from the peak itself. [X]:
    /// tuned on three scenes (2026-10-05), an SDR game measuring 282–291 nits.
    /// </summary>
    public const double SdrContentBelowNits = 400.0;

    /// <summary>
    /// A measured peak at or above this is HDR content, tone-mapped from <see cref="SourcePeakNits"/>. [X]: tuned on
    /// three scenes (2026-10-05), Forza's brightest tile measuring 535–849 nits and an SDR game's 294–389.
    /// </summary>
    public const double HdrContentAboveNits = 500.0;

    /// <summary>
    /// The share of tiles allowed above the measured peak: none. Tried at one in a hundred on 2026-10-05, so that one
    /// small highlight would not decide the curve, and that was backwards. Forza then measured near 318 nits, was
    /// taken for SDR content, and its sun and headlights clipped to white again. One bright tile deciding is right:
    /// in HDR content it selects the 1,000-nit curve, which cannot clip, and SDR content inside HDR10 has no such tile,
    /// its cursor and menus included, because the console renders all of it as SDR.
    /// </summary>
    public const double PeakTileFraction = 0.0;

    /// <summary>
    /// The picture's peak from its tiles' peaks: the brightest, as <see cref="PeakTileFraction"/> explains. Empty
    /// input is zero.
    /// </summary>
    public static double PeakOfTiles(IReadOnlyList<double> tilePeaks)
    {
        if (tilePeaks.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. tilePeaks];
        Array.Sort(sorted);
        int skip = (int)(sorted.Length * PeakTileFraction);
        return sorted[sorted.Length - 1 - skip];
    }

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
        return 10000.0 * Math.Pow(Math.Max(Math.Max(p - C1, 0) / (C2 - C3 * p), 0), 1.0 / M1);
    }

    /// <summary>Luminance in nits to PQ signal (0..1).</summary>
    public static double NitsToPq(double nits)
    {
        double y = Math.Pow(Math.Max(nits, 0) / 10000.0, M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    /// <summary>
    /// The curve's source peak for a measured (smoothed) picture peak: the measurement for SDR-like content,
    /// <see cref="SourcePeakNits"/> for HDR content, linear between, and never below <see cref="SdrPeakNits"/>.
    /// </summary>
    public static double SourcePeakFor(double measuredPeakNits)
    {
        double m = Math.Max(measuredPeakNits, SdrPeakNits);
        if (m <= SdrContentBelowNits)
        {
            return m;
        }

        if (m >= HdrContentAboveNits)
        {
            return SourcePeakNits;
        }

        double t = (m - SdrContentBelowNits) / (HdrContentAboveNits - SdrContentBelowNits);
        return SdrContentBelowNits + (SourcePeakNits - SdrContentBelowNits) * t;
    }

    /// <summary>BT.2390's EETF from <see cref="SourcePeakNits"/> to <see cref="SdrPeakNits"/>.</summary>
    public static double Eetf(double nits) => Eetf(nits, SourcePeakNits);

    /// <summary>
    /// BT.2390's EETF from <paramref name="sourcePeakNits"/> to <see cref="SdrPeakNits"/>, black at zero for both: the
    /// identity below the knee, a Hermite spline above it that meets the target peak with zero slope.
    /// </summary>
    public static double Eetf(double nits, double sourcePeakNits)
    {
        double sourcePq = NitsToPq(sourcePeakNits);
        double e1 = Math.Min(NitsToPq(nits) / sourcePq, 1.0);
        double maxLum = Math.Min(NitsToPq(SdrPeakNits) / sourcePq, 1.0);
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

    /// <summary>One pixel, from <see cref="SourcePeakNits"/>. See <see cref="ToSdr(double, double, double, double)"/>.</summary>
    public static (double R, double G, double B) ToSdr(double rPq, double gPq, double bPq)
        => ToSdr(rPq, gPq, bPq, SourcePeakNits);

    /// <summary>
    /// One pixel: PQ-encoded BT.2020 RGB (0..1 each) to gamma-encoded BT.709 RGB (0..1 each) for an SDR display,
    /// rolling off from <paramref name="sourcePeakNits"/>.
    /// </summary>
    public static (double R, double G, double B) ToSdr(double rPq, double gPq, double bPq, double sourcePeakNits)
    {
        double r2020 = PqToNits(rPq), g2020 = PqToNits(gPq), b2020 = PqToNits(bPq);

        // ITU-R BT.2087, linear BT.2020 to linear BT.709. Out-of-gamut colours come out negative and are clipped.
        double r = Math.Max(1.6605 * r2020 - 0.5876 * g2020 - 0.0728 * b2020, 0);
        double g = Math.Max(-0.1246 * r2020 + 1.1329 * g2020 - 0.0083 * b2020, 0);
        double b = Math.Max(-0.0182 * r2020 - 0.1006 * g2020 + 1.1187 * b2020, 0);

        // Roll off the brightest channel and scale the others with it, so hue holds as a highlight compresses.
        double peak = Math.Max(r, Math.Max(g, b));
        double scale = peak > 0 ? Eetf(peak, sourcePeakNits) / peak / SdrPeakNits : 0;

        return (Encode(r * scale), Encode(g * scale), Encode(b * scale));

        static double Encode(double linear) => Math.Pow(Math.Clamp(linear, 0, 1), 1.0 / DisplayGamma);
    }

    /// <summary>
    /// The measured peak, smoothed over time rather than frames, so it behaves the same at 30 fps as at 60: it rises
    /// within a few hundred milliseconds, so a brightening scene is not clipped for long, and falls over seconds, so a
    /// dark moment does not pump the brightness.
    ///
    /// <para>
    /// The first measurement is taken as it is, unsmoothed, on purpose. It arrives in the stream's first few frames,
    /// before the picture has settled; smoothing it from the 1,000-nit assumption instead would fade an SDR game up to
    /// full white over seconds at the start of every stream.
    /// </para>
    /// </summary>
    public sealed class PeakTracker
    {
        /// <summary>Time constant of a rising measurement. [X]: chosen, not measured.</summary>
        public const double RiseSeconds = 0.3;

        /// <summary>Time constant of a falling measurement. [X]: chosen, not measured.</summary>
        public const double FallSeconds = 3.0;

        /// <summary>Longest step one update may take, so a stall or a debugger pause does not jump the peak.</summary>
        public const double MaxStepSeconds = 0.5;

        /// <summary>The smoothed peak in nits, or null before the first measurement.</summary>
        public double? Smoothed { get; private set; }

        /// <summary>
        /// Take a measured peak, <paramref name="elapsedSeconds"/> after the last; returns the curve's source peak
        /// (<see cref="SourcePeakFor"/>).
        /// </summary>
        public double Update(double measuredPeakNits, double elapsedSeconds)
        {
            if (Smoothed is not { } previous)
            {
                Smoothed = measuredPeakNits;
                return SourcePeakFor(measuredPeakNits);
            }

            double dt = Math.Clamp(elapsedSeconds, 0, MaxStepSeconds);
            double tau = measuredPeakNits > previous ? RiseSeconds : FallSeconds;
            Smoothed = previous + (measuredPeakNits - previous) * (1 - Math.Exp(-dt / tau));
            return SourcePeakFor(Smoothed.Value);
        }
    }
}
