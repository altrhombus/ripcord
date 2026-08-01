using System.Buffers.Binary;
using System.Security.Cryptography;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Ripcord.Protocol.Halyard.Common.Streaming.Fec;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// End-to-end FEC through the demuxer: a frame is transmitted as source units (each <c>[2-byte size-ext][slice]</c>
/// padded to a common size) plus Cauchy-RS parity units. Dropping a source unit but delivering enough parity must
/// reconstruct the exact original frame — and NOT fire a loss/IDR request. Uses passthrough crypto.
/// </summary>
public class HalyardStreamDemuxerFecTests
{
    private const int PayloadOffset = 21; // v1 video: base 18 + 3-byte video prefix, no NALU-info

    private static byte[] VideoPacket(ushort frameIndex, int unitIndex, int unitsTotal, int fec, ReadOnlySpan<byte> unitPayload)
    {
        var packet = new byte[PayloadOffset + unitPayload.Length];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);
        uint packed = ((uint)unitIndex << 21) | ((uint)(unitsTotal - 1) << 10) | (uint)fec;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), packed);
        unitPayload.CopyTo(packet.AsSpan(PayloadOffset));
        return packet;
    }

    [Fact]
    public void ReconstructsDroppedSourceUnitFromParity()
    {
        const int source = 3, fec = 2, padded = 48, stride = 48, total = source + fec;
        int[] sliceLen = { 40, 30, 20 };

        // Build the padded slot buffer: each source unit = [2-byte ext][slice][zero pad], then compute parity.
        var buf = new byte[total * stride];
        var slices = new byte[source][];
        for (int u = 0; u < source; u++)
        {
            int dataSize = 2 + sliceLen[u];
            int ext = padded - dataSize; // padding that brings this unit up to the common size
            int off = u * stride;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off), (ushort)ext);
            slices[u] = RandomNumberGenerator.GetBytes(sliceLen[u]);
            slices[u].CopyTo(buf.AsSpan(off + 2));
        }
        CauchyReedSolomon.Encode(buf, padded, stride, source, fec);

        byte[] expected = slices.SelectMany(s => s).ToArray(); // the frame the demuxer should emit

        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var losses = new List<(int, int)>();
        demuxer.VideoLossDetected += (a, b) => losses.Add((a, b));
        EncodedVideoFrame? emitted = null;
        // Copied: Payload is only valid during the callback (the demuxer reuses the buffer).
        demuxer.VideoFrameReady += f => emitted = f with { Payload = f.Payload.ToArray() };

        int DataSize(int u) => 2 + sliceLen[u];

        // Deliver source unit 0 first (fixes the padded size), DROP source unit 1, deliver source unit 2,
        // then both parity units (full padded size each).
        demuxer.Ingest(VideoPacket(0, 0, total, fec, buf.AsSpan(0 * stride, DataSize(0))));
        demuxer.Ingest(VideoPacket(0, 2, total, fec, buf.AsSpan(2 * stride, DataSize(2))));
        demuxer.Ingest(VideoPacket(0, source + 0, total, fec, buf.AsSpan((source + 0) * stride, padded)));
        demuxer.Ingest(VideoPacket(0, source + 1, total, fec, buf.AsSpan((source + 1) * stride, padded)));

        // Next frame flushes frame 0.
        demuxer.Ingest(VideoPacket(1, 0, 1, 0, new byte[] { 0, 0, 1, 2, 3 }));

        Assert.NotNull(emitted);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(emitted!.Payload.ToArray()));
        Assert.Empty(losses); // fully recovered → no IDR request
    }

    [Fact]
    public void UnrecoverableLoss_FiresIdrRequest()
    {
        // source=3, only 1 source + 1 parity delivered (2 < 3) → FEC can't run → loss reported.
        const int source = 3, fec = 2, padded = 32, stride = 32, total = source + fec;
        var buf = new byte[total * stride];
        for (int u = 0; u < source; u++)
        {
            int off = u * stride;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off), (ushort)(padded - (2 + 10)));
            RandomNumberGenerator.GetBytes(10).CopyTo(buf.AsSpan(off + 2));
        }
        CauchyReedSolomon.Encode(buf, padded, stride, source, fec);

        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var losses = new List<(int, int)>();
        demuxer.VideoLossDetected += (a, b) => losses.Add((a, b));

        demuxer.Ingest(VideoPacket(0, 0, total, fec, buf.AsSpan(0, 12)));                 // 1 source
        demuxer.Ingest(VideoPacket(0, source, total, fec, buf.AsSpan(source * stride, padded))); // 1 parity
        demuxer.Ingest(VideoPacket(1, 0, 1, 0, new byte[] { 0, 0, 1, 2, 3 }));            // flush frame 0

        Assert.Contains((0, 0), losses);
    }
}
