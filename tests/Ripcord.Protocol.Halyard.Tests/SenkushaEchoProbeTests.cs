using System.Buffers.Binary;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The senkusha echo (ping) packet, checked against the bytes a real vendor client put on the wire.
///
/// <para>
/// Source of truth: <c>docs/protocol/captures/session8-wireshark.pcapng</c>, frames 155-175 — ten pings, each
/// returned by the console byte-identically. With the timestamp field zeroed, the entire 548-byte payload is
/// <c>[0] = 0x03</c>, <c>[6] = 0xFF</c>, <c>[9] = 0xFF</c>, the sequence at <c>[5]</c>, and zero everywhere else.
/// These tests encode that observation so a change to the layout has to be deliberate.
/// </para>
/// </summary>
public class SenkushaEchoProbeTests
{
    // Offsets that carried a non-zero value in the capture, excluding the sequence and the timestamp.
    private const int BaseTypeOffset = 0;
    private const int SequenceOffset = 5;
    private const int FirstMarkerOffset = 6;
    private const int SecondMarkerOffset = 9;
    private const int TimestampOffset = 22;

    [Fact]
    public void ThePayloadIsTheLengthTheVendorSends()
    {
        // 548 here, 556 on the wire once the UDP header is counted — the classic IPv4 minimum-MTU-safe size, which
        // is presumably why it was chosen for a probe that has to survive any path.
        Assert.Equal(548, SenkushaEchoProbe.Build(0, 0).Length);
    }

    [Fact]
    public void OnlyTheObservedFieldsAreNonZero()
    {
        // The strongest form of this assertion: with the timestamp zeroed, EVERY other byte is accounted for. If a
        // field is ever added, this fails rather than silently sending something the console has never seen.
        byte[] packet = SenkushaEchoProbe.Build(sequence: 0, microseconds: 0);

        for (int i = 0; i < packet.Length; i++)
        {
            byte expected = i switch
            {
                BaseTypeOffset => 0x03,
                FirstMarkerOffset => 0xFF,
                SecondMarkerOffset => 0xFF,
                _ => 0x00,
            };

            Assert.Equal(expected, packet[i]);
        }
    }

    [Fact]
    public void TheSequenceGoesWhereTheCaptureShowsIt()
    {
        // Byte 5 counted 0..9 across the vendor's ten pings.
        for (byte sequence = 0; sequence < 10; sequence++)
        {
            Assert.Equal(sequence, SenkushaEchoProbe.Build(sequence, 0)[SequenceOffset]);
        }
    }

    [Fact]
    public void TheTimestampIsWrittenBigEndianAcrossTheObservedFiveBytes()
    {
        // Five bytes wide, matching the capture: a 32-bit microsecond counter wraps in ~71 minutes, and the
        // captured high byte was clearly the top of a wider value.
        const long micros = 0x12_3456_789AL;
        byte[] packet = SenkushaEchoProbe.Build(0, micros);

        Assert.Equal(0x12, packet[TimestampOffset]);
        Assert.Equal(0x3456789AU, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(TimestampOffset + 1)));
    }

    [Fact]
    public void ATimestampWiderThanTheFieldIsTruncatedNotOverflowed()
    {
        // Writing past the field would corrupt the bytes after it. Verified by checking the following byte stays 0.
        byte[] packet = SenkushaEchoProbe.Build(0, long.MaxValue);
        Assert.Equal(0x00, packet[TimestampOffset + 5]);
    }

    [Fact]
    public void TheRttPingTailIsZeroAndTheMtuProbeTailIsNot()
    {
        // Observed identically in TWO independent captures (session8 frame 187 and cap47): the 548-byte ping has an
        // all-zero tail, while the 1226-byte MTU probe is filled with 0x47 from offset 27. The asymmetry is the
        // point — zero-filled payloads are compressible, so a link that compresses one would let an MTU test pass at
        // a size the path cannot actually deliver.
        byte[] ping = SenkushaEchoProbe.Build(0, 0);
        Assert.All(ping[SenkushaEchoProbe.PaddingOffset..], b => Assert.Equal(0x00, b));

        byte[] mtuProbe = SenkushaEchoProbe.Build(
            0, 0, SenkushaEchoProbe.PayloadForMtu(1254), SenkushaEchoProbe.MtuPaddingByte);

        Assert.Equal(1226, mtuProbe.Length);   // 1254 - 28, matching both captures
        Assert.All(
            mtuProbe[SenkushaEchoProbe.PaddingOffset..],
            b => Assert.Equal(SenkushaEchoProbe.MtuPaddingByte, b));
    }

    [Fact]
    public void PaddingNeverOverwritesTheHeaderOrTimestamp()
    {
        // The fill starts after the fields, so a padded packet must still be recognisable and still carry its
        // sequence and timestamp intact.
        byte[] packet = SenkushaEchoProbe.Build(
            5, 0x99, SenkushaEchoProbe.PayloadForMtu(1254), SenkushaEchoProbe.MtuPaddingByte);

        Assert.True(SenkushaEchoProbe.TryReadEcho(packet, out byte sequence));
        Assert.Equal(5, sequence);
        Assert.Equal(0x99U, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(TimestampOffset + 1)));
    }

    // ---- recognising the echo ----

    [Fact]
    public void OurOwnPingIsRecognisedAsAnEcho()
    {
        // The console returns the packet unchanged, so a round trip is exactly our own bytes coming back.
        byte[] packet = SenkushaEchoProbe.Build(sequence: 7, microseconds: 1234);

        Assert.True(SenkushaEchoProbe.TryReadEcho(packet, out byte sequence));
        Assert.Equal(7, sequence);
    }

    [Fact]
    public void ControlAndMtuPacketsAreNotMistakenForEchoes()
    {
        // The receive loop sees every datagram on the socket, so a control packet or the console's own MTU probe
        // must not be credited as a ping reply — that would report a round trip that never happened.
        byte[] packet = SenkushaEchoProbe.Build(0, 0);

        packet[BaseTypeOffset] = TakionMessageHeader.BaseTypeControl;
        Assert.False(SenkushaEchoProbe.TryReadEcho(packet, out _));

        packet[BaseTypeOffset] = TakionMessageHeader.BaseTypeSenkushaMtu;
        Assert.False(SenkushaEchoProbe.TryReadEcho(packet, out _));
    }

    [Fact]
    public void AMissingMarkerByteIsRejected()
    {
        // Both 0xFF markers were constant across every observed ping and both observed sizes, so they are structural
        // and worth checking rather than ignoring.
        foreach (int offset in (int[])[FirstMarkerOffset, SecondMarkerOffset])
        {
            byte[] packet = SenkushaEchoProbe.Build(0, 0);
            packet[offset] = 0x00;
            Assert.False(SenkushaEchoProbe.TryReadEcho(packet, out _));
        }
    }

    [Fact]
    public void ARuntDatagramIsRejectedRatherThanReadOutOfBounds()
    {
        Assert.False(SenkushaEchoProbe.TryReadEcho([], out _));
        Assert.False(SenkushaEchoProbe.TryReadEcho([0x03], out _));
        Assert.False(SenkushaEchoProbe.TryReadEcho(new byte[TimestampOffset], out _));
    }
}
