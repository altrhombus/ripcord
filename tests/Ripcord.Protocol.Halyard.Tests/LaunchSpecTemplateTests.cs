using System.Text.Json;
using Ripcord.Core.Net.Udp;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The launchSpec as a whole, pinned against the captured vendor template.
///
/// <para>
/// The console parses this and rejects an incomplete one <em>silently</em> — no SESSION_REPLY, no diagnostic — so
/// a divergence costs a debugging session to find. Two were found exactly that way: <c>videoCodec</c> and
/// <c>dynamicRange</c> were absent entirely, and <c>appSpecification.minFps</c> was hardcoded to 30 while the
/// vendor declares its target frame rate, which told the console it was free to halve our frame rate. These
/// tests exist so the next divergence is caught here instead.
/// </para>
/// </summary>
public class LaunchSpecTemplateTests
{
    private static readonly byte[] Handshake = new byte[16];

    private static JsonElement Spec(
        int w = 1920,
        int h = 1080,
        int fps = 60,
        int bitrate = 15_000,
        VideoCodec codec = VideoCodec.H264,
        DynamicRange range = DynamicRange.Sdr,
        int? mtu = null,
        double? rttMs = null)
        => JsonDocument.Parse(
            HalyardStreamingSession.BuildLaunchSpecJson(Handshake, w, h, fps, bitrate, codec, range, mtu, rttMs))
            .RootElement;

    [Fact]
    public void TopLevelKeyOrderMatchesTheVendorTemplate()
    {
        // Order is reproduced verbatim because our own notes record the console as order-sensitive. This is now
        // the COMPLETE vendor key set — no key the captured launchSpec sends is missing.
        string[] expected =
        [
            "sessionId", "streamResolutions", "network", "slotId", "appSpecification", "konan",
            "requestGameSpecification", "userProfile", "videoCodec", "dynamicRange", "handshakeKey",
            "audioChannels",
        ];

        Assert.Equal(expected, Spec().EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MinFpsTracksTheRequestedFrameRate()
    {
        // Was hardcoded 30. The vendor sends 60 alongside a 60 request, i.e. "do not give me less than this".
        Assert.Equal(60, Spec(fps: 60).GetProperty("appSpecification").GetProperty("minFps").GetInt32());
        Assert.Equal(30, Spec(fps: 30).GetProperty("appSpecification").GetProperty("minFps").GetInt32());
    }

    [Fact]
    public void DeclaredBandwidthIsTheRequestedBitrate()
    {
        // Confirmed on hardware to be the console's primary quality lever: it selects a resolution rung from the
        // offered ladder to fit this number (30 Mbps kept 1080p, 5 Mbps dropped the stream to 960x540).
        Assert.Equal(5_000, Spec(bitrate: 5_000).GetProperty("network").GetProperty("bwKbpsSent").GetInt32());
    }

    [Fact]
    public void VideoCodecIsSpelledTheWayTheConsoleExpects()
    {
        Assert.Equal("avc", Spec(codec: VideoCodec.H264).GetProperty("videoCodec").GetString());
        Assert.Equal("hevc", Spec(codec: VideoCodec.Hevc).GetProperty("videoCodec").GetString());
    }

    [Fact]
    public void DynamicRangeDefaultsToSdr()
    {
        Assert.Equal("SDR", Spec().GetProperty("dynamicRange").GetString());
    }

    [Fact]
    public void HdrIsSpelledTheWayTheConsoleIsExpectedToUnderstand()
    {
        // "SDR" is wire-confirmed from a captured vendor launchSpec; "HDR" is inferred from the field carrying a
        // value at all. If the console declines the launchSpec, this token is the first thing to vary.
        Assert.Equal("HDR", Spec(range: DynamicRange.Hdr).GetProperty("dynamicRange").GetString());
    }

    [Fact]
    public void MeasuredMtuAndRttReachTheWire()
    {
        // mtu arrives ALREADY in declared form — either senkusha-confirmed or the interface estimate converted by
        // the caller — so the builder must forward it untouched. Converting here as well would subtract the IP/UDP
        // allowance twice and quietly declare 1408 for a verified 1454 path. LinkMetricsTests covers the conversion.
        JsonElement network = Spec(mtu: 1354, rttMs: 4.6).GetProperty("network");

        Assert.Equal(1354, network.GetProperty("mtu").GetInt32());
        Assert.Equal(5, network.GetProperty("rtt").GetInt32());   // rounded: the field is an integer
    }

    [Fact]
    public void AConfirmedMtuIsForwardedWithoutAdjustment()
    {
        // The exact regression guarded above, stated at the value that matters: a senkusha-confirmed 1454 must reach
        // the console as 1454.
        Assert.Equal(
            LinkMetrics.VendorMtu,
            Spec(mtu: LinkMetrics.VendorMtu).GetProperty("network").GetProperty("mtu").GetInt32());
    }

    [Fact]
    public void UnmeasuredLinkFallsBackToWhatWasHardcodedBefore()
    {
        // This change must not make an unmeasurable host worse than the old constants did.
        JsonElement network = Spec().GetProperty("network");

        Assert.Equal(LinkMetrics.VendorMtu, network.GetProperty("mtu").GetInt32());
        Assert.Equal(0, network.GetProperty("rtt").GetInt32());
    }

    [Fact]
    public void AnAbsurdRttIsClampedRatherThanForwarded()
    {
        // A stalled handshake could time a reply in the tens of seconds; telling the console the link has a
        // 30-second round trip would be worse than telling it nothing.
        Assert.Equal(1000, Spec(rttMs: 30_000).GetProperty("network").GetProperty("rtt").GetInt32());
    }

    [Fact]
    public void ASubMillisecondLinkRoundsToZeroAndThatIsCorrect()
    {
        // A LAN really does round-trip in under half a millisecond. Zero here is a measurement, unlike the old
        // unconditional zero — the difference is that it is now reached only by measuring.
        Assert.Equal(0, Spec(rttMs: 0.4).GetProperty("network").GetProperty("rtt").GetInt32());
    }

    [Fact]
    public void YuvCoefficientIsStillSentEvenThoughTheConsoleIgnoresIt()
    {
        // Kept for vendor parity: hardware shows the console encodes BT.709 regardless of this field, which is
        // why the renderer reads the matrix from the bitstream instead of trusting the request.
        Assert.Equal(
            "bt601",
            Spec().GetProperty("requestGameSpecification").GetProperty("yuvCoefficient").GetString());
    }

    [Fact]
    public void AudioChannelsMatchesWhatTheDecodePathActuallyHandles()
    {
        // The point of pinning these: the values are declared to the console, so if they ever drift from what the
        // Opus path can decode, audio breaks with nothing in the logs to say why. 48 kHz stereo 16-bit at 480
        // samples per frame is what OpusAudioDecoder and AudioFormatConverter are built for, and
        // maxFrameDataSize 1920 = 480 x 2 channels x 2 bytes must stay consistent with them.
        JsonElement settings = Spec()
            .GetProperty("audioChannels")
            .GetProperty("audioChannelSettings")[0];

        Assert.Equal("opus", Spec().GetProperty("audioChannels").GetProperty("encoderType").GetString());
        Assert.Equal(48_000, settings.GetProperty("sampleRate").GetInt32());
        Assert.Equal(2, settings.GetProperty("channels").GetInt32());
        Assert.Equal(2, settings.GetProperty("sampleSize").GetInt32());          // bytes per sample => 16-bit
        Assert.Equal(480, settings.GetProperty("samplesPerFrame").GetInt32());
        Assert.Equal(
            settings.GetProperty("samplesPerFrame").GetInt32()
                * settings.GetProperty("channels").GetInt32()
                * settings.GetProperty("sampleSize").GetInt32(),
            settings.GetProperty("maxFrameDataSize").GetInt32());
    }

    [Fact]
    public void AudioChannelsIsNotParameterisedOnOurSettings()
    {
        // Same declaration whatever the video request, because it mirrors a wire-confirmed vendor template rather
        // than our own configuration. If this ever needs to vary, that is a deliberate change, not a side effect.
        string atOneEnd = Spec(640, 360, 30, 5_000, VideoCodec.H264).GetProperty("audioChannels").GetRawText();
        string atTheOther = Spec(1920, 1080, 60, 40_000, VideoCodec.Hevc, DynamicRange.Hdr)
            .GetProperty("audioChannels").GetRawText();

        Assert.Equal(atOneEnd, atTheOther);
    }

    [Fact]
    public void TheWholeSpecIsValidJson()
    {
        // The template is assembled by string concatenation, so a stray comma or quote is a live possibility and
        // would be rejected silently by the console.
        foreach (int fps in (int[])[30, 60])
        {
            foreach ((int w, int h) in new[] { (640, 360), (1280, 720), (1920, 1080) })
            {
                Assert.Equal(JsonValueKind.Object, Spec(w, h, fps).ValueKind);
            }
        }
    }

    [Fact]
    public void HandshakeKeyIsBase64Encoded()
    {
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        string? encoded = JsonDocument
            .Parse(HalyardStreamingSession.BuildLaunchSpecJson(key, 1920, 1080, 60, 15_000, VideoCodec.H264))
            .RootElement.GetProperty("handshakeKey").GetString();

        Assert.Equal(Convert.ToBase64String(key), encoded);
    }
}
