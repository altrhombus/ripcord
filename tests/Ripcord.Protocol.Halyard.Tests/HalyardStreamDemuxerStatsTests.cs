using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Wire packet-loss accounting that feeds the congestion-feedback packet. Loss is counted against the units
/// actually on the wire (source + real FEC), reported even when FEC later recovers the frame, and reset on read.
/// </summary>
public class HalyardStreamDemuxerStatsTests
{
    private const int PayloadOffset = 21;

    private static byte[] VideoPacket(ushort frameIndex, int unitIndex, int unitsTotal, int fec)
    {
        var packet = new byte[PayloadOffset + 8];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);
        uint packed = ((uint)unitIndex << 21) | ((uint)(unitsTotal - 1) << 10) | (uint)fec;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), packed);
        return packet;
    }

    [Fact]
    public void AllUnitsReceived_NoLoss()
    {
        var d = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        // Frame 0: 2 source + 1 FEC, all delivered.
        d.Ingest(VideoPacket(0, 0, 3, 1));
        d.Ingest(VideoPacket(0, 1, 3, 1));
        d.Ingest(VideoPacket(0, 2, 3, 1)); // FEC unit
        d.Ingest(VideoPacket(1, 0, 1, 0)); // flush frame 0

        (long received, long lost) = d.TakePacketStats();
        Assert.Equal(3, received);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void MissingUnit_CountedAsWireLoss_EvenIfRecoverable()
    {
        var d = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        // Frame 0: 2 source + 1 FEC; drop source unit 1 (deliver source 0 + the FEC unit).
        d.Ingest(VideoPacket(0, 0, 3, 1));
        d.Ingest(VideoPacket(0, 2, 3, 1)); // FEC unit
        d.Ingest(VideoPacket(1, 0, 1, 0)); // flush frame 0

        (long received, long lost) = d.TakePacketStats();
        Assert.Equal(2, received);
        Assert.Equal(1, lost); // one unit was lost on the wire, regardless of FEC recovery
    }

    [Fact]
    public void TakePacketStats_ResetsAccumulators()
    {
        var d = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        d.Ingest(VideoPacket(0, 0, 1, 0));
        d.Ingest(VideoPacket(1, 0, 1, 0)); // flush frame 0

        _ = d.TakePacketStats();
        (long received, long lost) = d.TakePacketStats();
        Assert.Equal(0, received);
        Assert.Equal(0, lost);
    }
}
