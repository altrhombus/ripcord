using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The demuxer's video-loss detection (feeds the IDR-request feedback path). Uses passthrough crypto so
/// synthetic video packets flow straight through. A frame is "complete" once all its declared source units
/// arrive; a shortfall, or a forward jump in the frame index, is a loss. Backward index jumps are stragglers,
/// not losses.
/// </summary>
public class HalyardStreamDemuxerLossTests
{
    private static byte[] VideoPacket(ushort frameIndex, int unitIndex, int unitsTotal, int fec)
    {
        const int payloadOffset = 21; // video: base 18 + 3-byte video prefix, no NALU-info
        var packet = new byte[payloadOffset + 8];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), 0);           // packet index (unused here)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);
        uint packed = ((uint)unitIndex << 21) | ((uint)(unitsTotal - 1) << 10) | (uint)fec;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), packed);
        return packet;
    }

    private static (HalyardStreamDemuxer demuxer, List<(int start, int end)> losses) NewDemuxer()
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var losses = new List<(int, int)>();
        demuxer.VideoLossDetected += (s, e) => losses.Add((s, e));
        return (demuxer, losses);
    }

    [Fact]
    public void CompleteInOrderFrames_NoLoss()
    {
        var (demuxer, losses) = NewDemuxer();
        // Frames 0,1,2 each with 2 source units, all delivered in order.
        for (ushort f = 0; f <= 2; f++)
        {
            demuxer.Ingest(VideoPacket(f, unitIndex: 0, unitsTotal: 2, fec: 0));
            demuxer.Ingest(VideoPacket(f, unitIndex: 1, unitsTotal: 2, fec: 0));
        }
        Assert.Empty(losses);
    }

    [Fact]
    public void IncompleteFrame_ReportsThatFrame()
    {
        var (demuxer, losses) = NewDemuxer();
        // Frame 0 declares 3 source units but only 2 arrive; frame 1 arrives and flushes frame 0.
        demuxer.Ingest(VideoPacket(0, unitIndex: 0, unitsTotal: 3, fec: 0));
        demuxer.Ingest(VideoPacket(0, unitIndex: 1, unitsTotal: 3, fec: 0));
        demuxer.Ingest(VideoPacket(1, unitIndex: 0, unitsTotal: 1, fec: 0));

        Assert.Contains((0, 0), losses);
    }

    [Fact]
    public void FecUnitsDoNotCountTowardCompleteness()
    {
        var (demuxer, losses) = NewDemuxer();
        // total=3, fec=1 => 2 source units. Both source units arrive; the FEC unit is skipped by the demuxer.
        demuxer.Ingest(VideoPacket(0, unitIndex: 0, unitsTotal: 3, fec: 1));
        demuxer.Ingest(VideoPacket(0, unitIndex: 1, unitsTotal: 3, fec: 1));
        demuxer.Ingest(VideoPacket(0, unitIndex: 2, unitsTotal: 3, fec: 1)); // FEC (index >= source) — ignored
        demuxer.Ingest(VideoPacket(1, unitIndex: 0, unitsTotal: 1, fec: 0));

        Assert.Empty(losses);
    }

    [Fact]
    public void ForwardFrameGap_ReportsMissingRange()
    {
        var (demuxer, losses) = NewDemuxer();
        // Frame 0 complete, then frame 3 — frames 1 and 2 went missing entirely.
        demuxer.Ingest(VideoPacket(0, unitIndex: 0, unitsTotal: 1, fec: 0));
        demuxer.Ingest(VideoPacket(3, unitIndex: 0, unitsTotal: 1, fec: 0));

        Assert.Contains((1, 2), losses);
    }

    [Fact]
    public void BackwardStraggler_NotReportedAsGap()
    {
        var (demuxer, losses) = NewDemuxer();
        demuxer.Ingest(VideoPacket(5, unitIndex: 0, unitsTotal: 1, fec: 0));
        demuxer.Ingest(VideoPacket(3, unitIndex: 0, unitsTotal: 1, fec: 0)); // out-of-order straggler

        Assert.Empty(losses);
    }

    [Fact]
    public void FrameIndexWraps_NoFalseGap()
    {
        var (demuxer, losses) = NewDemuxer();
        demuxer.Ingest(VideoPacket(0xffff, unitIndex: 0, unitsTotal: 1, fec: 0));
        demuxer.Ingest(VideoPacket(0x0000, unitIndex: 0, unitsTotal: 1, fec: 0)); // 65535 -> 0 is contiguous

        Assert.Empty(losses);
    }
}
