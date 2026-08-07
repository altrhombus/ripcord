using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Audio demux: an audio packet packs <c>TotalUnits</c> equal-size units back to back — the first
/// is the source Opus frame, the rest are redundant copies of the same 10 ms for loss concealment. The frame
/// index advances by one per packet, so there is exactly one source frame per packet; the demuxer emits only
/// that first unit (unit_size = payload / TotalUnits). Feeding the whole payload (source + redundant copies)
/// to the decoder corrupts the high CELT bands ("underwater" audio) because Opus sizes its bit budget from the
/// packet length. Audio's payload starts at base(18) + 2 (unknown + haptics prefix bytes).
/// </summary>
public class HalyardStreamDemuxerAudioTests
{
    private const int AudioPayloadOffset = 20; // base(18) + v12 audio prefix(2: unknown + haptics), no NALU-info
    private const byte OpusCodec = 5;

    /// <summary>
    /// Build an audio packet whose payload is <paramref name="units"/> copies of <paramref name="unit"/>. The
    /// bytes-5..8 field encodes total_units-1 in byte 6 (audio's byte-packed layout).
    /// </summary>
    private static byte[] AudioPacket(byte[] unit, int units = 1, byte codec = OpusCodec)
    {
        var packet = new byte[AudioPayloadOffset + unit.Length * units];
        packet[0] = HalyardStreamHeader.TypeAudio;
        packet[6] = (byte)(units - 1); // total_units - 1
        packet[9] = codec;
        for (int i = 0; i < units; i++)
        {
            unit.CopyTo(packet, AudioPayloadOffset + i * unit.Length);
        }

        return packet;
    }

    [Fact]
    public void HeaderParsesAudioPayloadOffsetCodecAndUnitCount()
    {
        byte[] pkt = AudioPacket(new byte[] { 1, 2, 3, 4 }, units: 3);
        Assert.True(HalyardStreamHeader.TryParse(pkt, out var header));
        Assert.True(header.IsAudio);
        Assert.Equal(AudioPayloadOffset, header.PayloadOffset);
        Assert.Equal(OpusCodec, header.Codec);
        Assert.Equal(3, header.TotalUnits);
    }

    [Fact]
    public void EmitsOnlyFirstUnitWhenPayloadCarriesRedundantCopies()
    {
        // The real stream sends 3 units of 80 bytes (1 source + 2 redundant). Here: 3 copies of a 7-byte unit.
        var unit = new byte[] { 0xF4, 0xDF, 0x3E, 0xFB, 0x3D, 0x10, 0x20 };

        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var frames = new List<byte[]>();
        demuxer.AudioFrameReady += f => frames.Add(f.Payload.ToArray());

        demuxer.Ingest(AudioPacket(unit, units: 3));

        Assert.Single(frames);
        Assert.Equal(Convert.ToHexString(unit), Convert.ToHexString(frames[0]));
    }

    [Fact]
    public void EmitsWholePayloadWhenSingleUnit()
    {
        var opus = new byte[] { 0xF4, 0xDF, 0x3E, 0xFB, 0x3D, 0x10, 0x20 };

        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var frames = new List<byte[]>();
        demuxer.AudioFrameReady += f => frames.Add(f.Payload.ToArray());

        demuxer.Ingest(AudioPacket(opus, units: 1));

        Assert.Single(frames);
        Assert.Equal(Convert.ToHexString(opus), Convert.ToHexString(frames[0]));
    }

    [Fact]
    public void IgnoresNonOpusCodec()
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var frames = new List<byte[]>();
        demuxer.AudioFrameReady += f => frames.Add(f.Payload.ToArray());

        demuxer.Ingest(AudioPacket(new byte[] { 1, 2, 3, 4 }, codec: 0)); // not Opus
        Assert.Empty(frames);
    }

    [Fact]
    public void IgnoresEmptyPayload()
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        var frames = new List<byte[]>();
        demuxer.AudioFrameReady += f => frames.Add(f.Payload.ToArray());

        demuxer.Ingest(AudioPacket(Array.Empty<byte>()));
        Assert.Empty(frames);
    }
}
