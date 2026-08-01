using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// The senkusha echo (ping) packet, reconstructed byte-for-byte from a captured vendor session.
///
/// <para>
/// <b>Provenance [W] — observed on the wire.</b> Every field below was derived from
/// <c>docs/protocol/captures/session8-wireshark.pcapng</c> (senkusha is keyless, so the exchange is in the clear)
/// and the frame numbers are cited so each claim is re-checkable. The protobuf field numbers come from
/// <c>bandwidth_probe.proto</c>, whose own header records them as
/// recovered from our copy of the client binary. Generated output was verified byte-identical to captured frame 155.
/// </para>
///
/// <para>
/// Every field here is an observation, not a guess. From <c>session8-wireshark.pcapng</c> frames 155-175 — ten
/// pings, each returned by the console <em>byte-identically</em>:
/// </para>
/// <list type="bullet">
///   <item><description>548-byte payload (556 on the wire, less the 8-byte UDP header).</description></item>
///   <item><description>Byte 0: base type <c>0x03</c>.</description></item>
///   <item><description>Byte 5: sequence, 0..9 across the ten pings.</description></item>
///   <item><description>Bytes 6 and 9: <c>0xFF</c>, constant across every ping and both sizes observed.</description></item>
///   <item><description>Bytes 22-26: a monotonic counter whose deltas matched the capture's own inter-packet
///   timing to within a few microseconds (576 vs 485, 344 vs 340, 406 vs 408, ...), i.e. a microsecond send
///   timestamp.</description></item>
///   <item><description>Everything else: zero.</description></item>
/// </list>
///
/// <para>
/// That timestamp is the reason this is implementable at all. Because the console returns the packet unchanged, the
/// field can only be the CLIENT's own send time, read back to compute a round trip — it is not a checksum over the
/// payload. Had it been a checksum, generating a valid ping would have required reversing it; as a client-local
/// timestamp we are free to write our own. RTT is still measured with a monotonic stopwatch rather than by reading
/// the echoed field, because the wall clock can step and the stopwatch cannot.
/// </para>
/// </summary>
internal static class SenkushaEchoProbe
{
    /// <summary>Payload size the vendor uses for the RTT pings. 556 on the wire, i.e. the classic IPv4
    /// minimum-MTU-safe datagram, which is presumably why a probe that must survive any path uses it.</summary>
    public const int PayloadLength = 548;

    /// <summary>
    /// Bytes between an <c>mtuReq</c> value and the UDP payload that realises it: IPv4 header 20 + UDP header 8.
    ///
    /// <para>
    /// Confirmed twice at different sizes in the same capture, which is why it is stated rather than assumed: the
    /// downstream probe requested 1454 and carried 1426 bytes, and the upstream probe requested 1254 and carried
    /// 1226. So <c>mtuReq</c> denotes the whole IP datagram, not the payload.
    /// </para>
    /// </summary>
    public const int IpAndUdpOverhead = 28;

    /// <summary>The payload length that realises an <c>mtuReq</c> of <paramref name="mtu"/>.</summary>
    public static int PayloadForMtu(int mtu) => mtu - IpAndUdpOverhead;

    /// <summary>
    /// Byte the vendor pads the MTU-test packet with, from <see cref="PaddingOffset"/> to the end.
    ///
    /// <para>
    /// Observed identically in two independent captures (session8 frame 187 and cap47), and it is NOT zero — unlike
    /// the RTT ping, whose tail is all zero in both. That asymmetry looks deliberate: a zero-filled payload is
    /// trivially compressible, so any link performing compression could carry it in fewer bytes than requested and
    /// an MTU test measured that way would pass at a size the path cannot actually deliver. Incompressible padding
    /// makes the datagram genuinely the size it claims.
    /// </para>
    /// </summary>
    public const byte MtuPaddingByte = 0x47;

    /// <summary>First byte of the payload tail, after the header fields and the timestamp.</summary>
    public const int PaddingOffset = 27;

    /// <summary>Pings the vendor sends before turning echo back off.</summary>
    public const int PingCount = 10;

    private const int SequenceOffset = 5;
    private const int FirstMarkerOffset = 6;
    private const int SecondMarkerOffset = 9;
    private const int TimestampOffset = 22;
    private const byte Marker = 0xFF;

    /// <summary>
    /// Build one ping. <paramref name="microseconds"/> goes into the observed timestamp field; only we read it back,
    /// so any monotonic value is as valid as the vendor's.
    /// </summary>
    /// <param name="padding">
    /// Fill for the payload tail. Zero for the RTT ping and <see cref="MtuPaddingByte"/> for the MTU test, matching
    /// what both captures show — see <see cref="MtuPaddingByte"/> for why the difference matters.
    /// </param>
    public static byte[] Build(
        byte sequence, long microseconds, int payloadLength = PayloadLength, byte padding = 0x00)
    {
        // The upstream MTU test reuses this exact format at a different size (1226 bytes in both captures, same
        // header, echoed identically), so the length is a parameter rather than a constant.
        var packet = new byte[payloadLength];
        if (padding != 0x00 && payloadLength > PaddingOffset)
        {
            packet.AsSpan(PaddingOffset).Fill(padding);
        }

        packet[0] = TakionMessageHeader.BaseTypeSenkushaEcho;
        packet[SequenceOffset] = sequence;
        packet[FirstMarkerOffset] = Marker;
        packet[SecondMarkerOffset] = Marker;

        // Five bytes, matching the observed width: a 32-bit microsecond counter wraps in about 71 minutes, and the
        // capture's high byte was clearly the top of a wider value.
        long value = microseconds & 0xFF_FFFF_FFFFL;
        packet[TimestampOffset] = (byte)(value >> 32);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(TimestampOffset + 1), (uint)value);
        return packet;
    }

    /// <summary>
    /// Whether a received datagram is an echo of one of our pings, and if so which sequence it carries.
    ///
    /// <para>
    /// Matched on the sequence byte rather than by comparing the whole packet: the console echoes verbatim today,
    /// but keying off one field we know the meaning of is more robust than depending on every byte we do not.
    /// </para>
    /// </summary>
    public static bool TryReadEcho(ReadOnlySpan<byte> datagram, out byte sequence)
    {
        sequence = 0;
        if (datagram.Length < TimestampOffset + 5
            || datagram[0] != TakionMessageHeader.BaseTypeSenkushaEcho
            || datagram[FirstMarkerOffset] != Marker
            || datagram[SecondMarkerOffset] != Marker)
        {
            return false;
        }

        sequence = datagram[SequenceOffset];
        return true;
    }
}
