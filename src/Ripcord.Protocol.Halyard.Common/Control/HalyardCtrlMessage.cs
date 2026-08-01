using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>
/// One message on the persistent binary control channel that follows the <c>/sess/ctrl</c> HTTP
/// handshake on the same TCP connection. Wire-confirmed framing (cap22/cap45 stream, all big-endian):
///
/// <code>
///   offset 0: u32  payload length (bytes after the 8-byte header)
///   offset 4: u16  message type
///   offset 6: u16  reserved (always 0)
///   offset 8: payload (length bytes; rpcrypt-encrypted when non-empty)
/// </code>
///
/// The console drives this channel — most importantly it sends <see cref="TypeHeartbeatReq"/> every few
/// seconds and disconnects the whole session if the client does not answer with
/// <see cref="TypeHeartbeatRep"/> (both empty-payload). Heartbeats carry no payload, so they need no
/// crypto; only message types that carry a payload are rpcrypt-encrypted (a later refinement for the
/// feature/keyboard/login messages). The earlier "RPCS"-magic framing was a documentation error — no such
/// magic appears on the wire.
/// </summary>
public readonly struct HalyardCtrlMessage
{
    public const int HeaderLength = 8;

    // Message types observed on the wire / from the authorized cross-reference. Only the heartbeat pair is
    // required to keep a session alive; the rest are functional (session id, login, features, keyboard, mic).
    public const ushort TypeLogin = 0x0005;
    public const ushort TypeSessionId = 0x0033;
    public const ushort TypeHeartbeatReq = 0x00fe;
    public const ushort TypeHeartbeatRep = 0x01fe;

    public HalyardCtrlMessage(ushort type, ReadOnlyMemory<byte> payload = default)
    {
        Type = type;
        Payload = payload;
    }

    public ushort Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public int TotalLength => HeaderLength + Payload.Length;

    /// <summary>Serialize this message into <paramref name="destination"/>; returns bytes written.</summary>
    public int WriteTo(Span<byte> destination)
    {
        if (destination.Length < TotalLength)
        {
            throw new ArgumentException("Destination too small for ctrl message.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)Payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], Type);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], 0);
        Payload.Span.CopyTo(destination[HeaderLength..]);
        return TotalLength;
    }

    public byte[] Serialize()
    {
        var buffer = new byte[TotalLength];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Try to parse a single ctrl message from the front of <paramref name="buffer"/>. On success,
    /// <paramref name="consumed"/> is the whole message length so a byte-stream reader can advance.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out HalyardCtrlMessage message, out int consumed)
    {
        message = default;
        consumed = 0;

        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        int payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (payloadLength < 0)
        {
            return false;
        }

        int total = HeaderLength + payloadLength;
        if (buffer.Length < total)
        {
            return false;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
        message = new HalyardCtrlMessage(type, buffer.Slice(HeaderLength, payloadLength).ToArray());
        consumed = total;
        return true;
    }
}
