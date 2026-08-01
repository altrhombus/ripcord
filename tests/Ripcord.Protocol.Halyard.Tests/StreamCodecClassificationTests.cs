using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Classifying the out-of-band parameter sets as H.264 or HEVC.
///
/// <para>
/// This decides which NAL type means "IDR", which in turn decides whether the parameter sets get prepended to a
/// keyframe at all. Getting it wrong is silent and total: an HEVC decoder handed slices with no VPS/SPS/PPS
/// accepts every packet and returns MF_E_TRANSFORM_NEED_MORE_INPUT forever — full decode and present counters,
/// black screen, no error anywhere. That is precisely what happened, so the boundary cases are pinned here.
/// </para>
/// </summary>
public class StreamCodecClassificationTests
{
    // Annex B start code + NAL header bytes.
    private static byte[] Nal(params byte[] nalBytes) => [0x00, 0x00, 0x00, 0x01, .. nalBytes];

    // H.264: 1-byte header, type = b & 0x1F. 0x67 = SPS (type 7), 0x68 = PPS (type 8).
    private static byte[] H264Sets() => [.. Nal(0x67, 0x42, 0x00, 0x28), .. Nal(0x68, 0xCE, 0x3C, 0x80)];

    // HEVC: 2-byte header, type = (b0 >> 1) & 0x3F. 0x40 = VPS (32), 0x42 = SPS (33), 0x44 = PPS (34).
    private static byte[] HevcSets() =>
        [.. Nal(0x40, 0x01, 0x0C, 0x01), .. Nal(0x42, 0x01, 0x01, 0x01), .. Nal(0x44, 0x01, 0xC1, 0x73)];

    [Fact]
    public void H264ParameterSets_AreNotHevc()
        => Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets(H264Sets()));

    [Fact]
    public void HevcParameterSets_AreHevc()
        => Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets(HevcSets()));

    [Fact]
    public void AnyOneHevcParameterSetIsEnough()
    {
        // The console need not send all three, and order is not guaranteed.
        Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x40, 0x01))); // VPS alone
        Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x42, 0x01))); // SPS alone
        Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x44, 0x01))); // PPS alone
    }

    [Fact]
    public void ThreeByteStartCodesAreAccepted()
    {
        // Annex B permits both 00 00 01 and 00 00 00 01, and the console is not obliged to pick one.
        Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets([0x00, 0x00, 0x01, 0x42, 0x01]));
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets([0x00, 0x00, 0x01, 0x67, 0x42]));
    }

    [Fact]
    public void AnH264SliceByteThatLooksLikeAnHevcIrapIsNotMisclassified()
    {
        // The reason classification is done on parameter sets rather than slices: 0x21 is an everyday H.264
        // non-IDR slice, and reading it as HEVC gives type 16 (BLA_W_LP), an IRAP. A classifier that accepted
        // slices would flip an H.264 stream to HEVC handling on the first ordinary frame.
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x21, 0x00)));
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x41, 0x9A)));
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets(Nal(0x65, 0x88))); // H.264 IDR slice
    }

    [Fact]
    public void EmptyOrGarbageDefaultsToH264()
    {
        // H.264 is the safe default: it is what the console sends unless it agrees to something else, and the
        // previous behaviour was unconditionally H.264.
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets([]));
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets([0xFF, 0xFF, 0xFF]));
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets([0x00, 0x00])); // truncated
        Assert.False(HalyardStreamDemuxer.LooksLikeHevcParameterSets([0x00, 0x00, 0x00, 0x01])); // no NAL body
    }

    [Fact]
    public void LeadingPaddingZerosDoNotDefeatIt()
    {
        Assert.True(HalyardStreamDemuxer.LooksLikeHevcParameterSets(
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x42, 0x01]));
    }
}
