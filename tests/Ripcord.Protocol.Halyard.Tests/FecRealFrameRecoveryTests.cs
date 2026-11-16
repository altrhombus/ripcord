using System.Buffers.Binary;
using System.Text.Json;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// FEC recovery against the console's own parity. Each fixture frame is a complete video frame captured from a
/// v1 session, every unit present, with its session's receive key. The test decrypts the frame, has the demuxer
/// emit it whole, then withholds each source unit in turn: the demuxer must rebuild the identical frame from the
/// console's parity unit, and report no loss.
///
/// <para>The synthetic FEC tests can only show the code agrees with itself; this shows it agrees with what the
/// console coded, at the console's own coded lengths.</para>
///
/// <para>Fixture <c>docs/protocol/captures/fec_frame_vectors.json</c> is gitignored (live session key and
/// packets, from <c>stream-quality/01</c>). Skips when absent.</para>
/// </summary>
public class FecRealFrameRecoveryTests
{
    private static readonly string FixturePath = LocateFixture();

    [SkippableFact]
    public void EveryWithheldSourceUnit_IsRebuiltFromTheConsolesParity()
    {
        Skip.IfNot(File.Exists(FixturePath), $"Captures-folder fixture not present ({FixturePath}).");
        var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath), JsonOpts)!;
        Assert.NotEmpty(fx.Frames!);

        using var crypto = new HalyardPacketCrypto(Hex.Bytes(fx.AesKey), Hex.Bytes(fx.BaseIv));
        int rebuilt = 0;

        foreach (var frame in fx.Frames!)
        {
            List<byte[]> packets = frame.Packets!.Select(p => Open(crypto, Hex.Bytes(p))).ToList();
            Assert.Equal(frame.SourceUnits + frame.ParityUnits, packets.Count);

            (string whole, int wholeLosses) = Emit(packets, frame.FrameIndex, withhold: -1);
            Assert.NotEmpty(whole);
            Assert.Equal(0, wholeLosses);

            for (int withheld = 0; withheld < frame.SourceUnits; withheld++)
            {
                (string recovered, int losses) = Emit(packets, frame.FrameIndex, withheld);
                Assert.Equal(whole, recovered);
                Assert.Equal(0, losses);
                rebuilt++;
            }
        }

        Assert.True(rebuilt > 0);
    }

    /// <summary>Verify the packet's tag with the session key, then decrypt its payload in place.</summary>
    private static byte[] Open(HalyardPacketCrypto crypto, byte[] packet)
    {
        Assert.True(HalyardStreamHeader.TryParse(packet, out var header));
        Assert.True(crypto.VerifyPacket(header.KeystreamOffset, packet));
        crypto.CryptPayloadInPlace(header.KeystreamOffset, packet.AsSpan(header.PayloadOffset));
        return packet;
    }

    /// <summary>Feed one frame's units (all but <paramref name="withhold"/>) through a fresh demuxer.</summary>
    private static (string Frame, int Losses) Emit(List<byte[]> packets, int frameIndex, int withhold)
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        int losses = 0;
        string emitted = "";
        demuxer.VideoLossDetected += (_, _) => losses++;
        demuxer.VideoFrameReady += f => emitted = Convert.ToHexString(f.Payload.Span);

        for (int u = 0; u < packets.Count; u++)
        {
            if (u != withhold)
                demuxer.Ingest(packets[u]);
        }

        demuxer.Ingest(NextFrameMarker((frameIndex + 1) & 0xffff)); // a new frame index flushes this one
        return (emitted, losses);
    }

    private static byte[] NextFrameMarker(int frameIndex)
    {
        const int payloadOffset = 21; // v1 video: base 18 + 3-byte video prefix, no extended header
        var packet = new byte[payloadOffset + 5];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)frameIndex);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), 0); // unit 0 of 1, no parity
        return packet;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string LocateFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "protocol", "captures", "fec_frame_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "fec_frame_vectors.json"); // non-existent -> skip
    }

    private sealed class Fixture
    {
        public string AesKey { get; set; } = "";
        public string BaseIv { get; set; } = "";
        public List<FrameVector>? Frames { get; set; }
    }

    private sealed class FrameVector
    {
        public int FrameIndex { get; set; }
        public int SourceUnits { get; set; }
        public int ParityUnits { get; set; }
        public List<string>? Packets { get; set; }
    }
}
