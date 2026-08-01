using System.Buffers.Binary;
using System.Linq;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Keyframe detection (by slice NAL type) and per-IDR SPS/PPS re-send: the parameter sets arrive out-of-band
/// in STREAM_INFO, so they must be prepended ahead of every IDR (not just the first) for a decoder to resync.
/// </summary>
public class HalyardStreamDemuxerKeyframeTests
{
    private const int PayloadOffset = 21;
    private static readonly byte[] VideoHeader = { 0, 0, 0, 1, 0x67, 0xAA, 0xBB, 0, 0, 0, 1, 0x68, 0xCC };

    private static byte[] SourceUnitPacket(ushort frameIndex, byte nalHeader, params byte[] sliceBody)
    {
        // Unit payload = [2-byte size-ext = 0][Annex-B start code][NAL header][body].
        var payload = new List<byte> { 0, 0, 0, 0, 0, 1, nalHeader };
        payload.AddRange(sliceBody);

        var packet = new byte[PayloadOffset + payload.Count];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);
        uint packed = (0u << 21) | (0u << 10) | 0u; // unit 0, 1 unit total, 0 fec
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), packed);
        payload.ToArray().CopyTo(packet, PayloadOffset);
        return packet;
    }

    private static (HalyardStreamDemuxer d, List<EncodedVideoFrame> frames) NewDemuxer(bool withHeader)
    {
        var d = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        if (withHeader)
        {
            d.SetVideoHeader(VideoHeader);
        }

        var frames = new List<EncodedVideoFrame>();
        // Copy on receipt. Payload is a slice of a buffer the demuxer reuses for the next frame, so retaining the
        // frame as-handed-out would have these assertions read whatever arrived later. They passed regardless only
        // because the buffer happened to still hold the frame under test.
        d.VideoFrameReady += f => frames.Add(f with { Payload = f.Payload.ToArray() });
        return (d, frames);
    }

    [Fact]
    public void ThePayloadBufferIsReusedBetweenFrames()
    {
        // Asserts the lifetime contract rather than leaving it to a comment. The demuxer hands out a slice of a
        // buffer it reuses, which removed an allocation and a full copy of every frame — the sole consumer already
        // copies into its own pooled buffer. A subscriber that retains this memory sees it overwritten mid-decode
        // and gets corruption indistinguishable from packet loss, so if this ever stops being true, the comment on
        // VideoFrameReady is wrong and the copies in these tests can go.
        var d = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var buffers = new List<int>();

        // Identity of the UNDERLYING array, captured without copying.
        d.VideoFrameReady += f =>
        {
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(f.Payload, out var segment));
            buffers.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(segment.Array!));
        };

        d.Ingest(SourceUnitPacket(0, 0x65, 0x11, 0x22));
        d.Ingest(SourceUnitPacket(1, 0x41, 0x33));
        d.Ingest(SourceUnitPacket(2, 0x41, 0x44));

        Assert.True(buffers.Count >= 2, "expected at least two frames to be emitted");
        Assert.Single(buffers.Distinct());
    }

    [Fact]
    public void IdrFrame_IsKeyAndPrependsVideoHeader()
    {
        var (d, frames) = NewDemuxer(withHeader: true);
        d.Ingest(SourceUnitPacket(0, 0x65, 0x11, 0x22)); // NAL type 5 = IDR
        d.Ingest(SourceUnitPacket(1, 0x41, 0x33));       // NAL type 1 = P, flushes frame 0

        Assert.NotEmpty(frames);
        EncodedVideoFrame idr = frames[0];
        Assert.True(idr.IsKeyFrame);
        byte[] payload = idr.Payload.ToArray();
        Assert.Equal(Convert.ToHexString(VideoHeader), Convert.ToHexString(payload.AsSpan(0, VideoHeader.Length)));
        // Slice follows the header.
        Assert.Equal(
            Convert.ToHexString(new byte[] { 0, 0, 0, 1, 0x65, 0x11, 0x22 }),
            Convert.ToHexString(payload.AsSpan(VideoHeader.Length)));
    }

    [Fact]
    public void PFrame_IsNotKeyAndOmitsVideoHeader()
    {
        var (d, frames) = NewDemuxer(withHeader: true);
        d.Ingest(SourceUnitPacket(0, 0x41, 0x33)); // NAL type 1 = P
        d.Ingest(SourceUnitPacket(1, 0x41, 0x44)); // flush frame 0

        Assert.NotEmpty(frames);
        Assert.False(frames[0].IsKeyFrame);
        Assert.Equal(
            Convert.ToHexString(new byte[] { 0, 0, 0, 1, 0x41, 0x33 }),
            Convert.ToHexString(frames[0].Payload.ToArray()));
    }

    [Fact]
    public void EveryIdr_GetsHeaderReSent()
    {
        var (d, frames) = NewDemuxer(withHeader: true);
        d.Ingest(SourceUnitPacket(0, 0x65, 0x01)); // IDR
        d.Ingest(SourceUnitPacket(1, 0x41, 0x02)); // P (flushes frame 0)
        d.Ingest(SourceUnitPacket(2, 0x65, 0x03)); // IDR again (flushes frame 1)
        d.Ingest(SourceUnitPacket(3, 0x41, 0x04)); // flush frame 2

        Assert.Equal(3, frames.Count);
        Assert.True(frames[0].IsKeyFrame);
        Assert.False(frames[1].IsKeyFrame);
        Assert.True(frames[2].IsKeyFrame);
        // Both IDR frames carry the header; the P frame does not.
        Assert.StartsWith(Convert.ToHexString(VideoHeader), Convert.ToHexString(frames[0].Payload.ToArray()));
        Assert.StartsWith(Convert.ToHexString(VideoHeader), Convert.ToHexString(frames[2].Payload.ToArray()));
        Assert.DoesNotContain(Convert.ToHexString(VideoHeader), Convert.ToHexString(frames[1].Payload.ToArray()));
    }
}
