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
///   offset 8: payload (length bytes; control-plane-encrypted when non-empty)
/// </code>
///
/// The console drives this channel — most importantly it sends <see cref="TypeHeartbeatReq"/> every few
/// seconds and disconnects the whole session if the client does not answer with
/// <see cref="TypeHeartbeatRep"/> (both empty-payload). Heartbeats carry no payload, so they need no
/// crypto; only message types that carry a payload are control-plane-encrypted (a later refinement for the
/// feature/keyboard/login messages). The earlier "RPCS"-magic framing was a documentation error — no such
/// magic appears on the wire.
/// </summary>
public readonly struct HalyardCtrlMessage
{
    public const int HeaderLength = 8;

    // Message types observed on the wire / from the authorized cross-reference. Only the heartbeat pair is
    // required to keep a session alive; the rest are functional (session id, login, features, keyboard, mic).
    /// <summary>
    /// Console → client: the user's account is locked and a login passcode is required before the stream can
    /// open. Wire-confirmed in cap50 (empty payload). Until this is answered with <see cref="TypeLoginSubmit"/>
    /// the console silently drops every Takion <c>INIT</c>, so the whole stream hangs.
    /// </summary>
    public const ushort TypeLoginPrompt = 0x0004;

    /// <summary>
    /// Client → console: the entered login passcode, as the 4 ASCII digits encrypted with the §2.1
    /// control-field cipher at <see cref="Control.HalyardSessCtrlFields.CounterLoginPin"/>. The high bit of the
    /// type (0x8000) marks it as the client's answer to <see cref="TypeLoginPrompt"/>.
    /// </summary>
    public const ushort TypeLoginSubmit = 0x8004;

    /// <summary>Console → client: login result (cap50 carried a single byte). Informational; the reliable
    /// "you may stream now" signal is <see cref="TypeSessionId"/>.</summary>
    public const ushort TypeLogin = 0x0005;

    /// <summary>Console → client: session-ready. After a login it is what gates the Takion bring-up.</summary>
    public const ushort TypeSessionId = 0x0033;

    /// <summary>
    /// Console → client: the stream service is ready for its Takion association. Empty payload.
    ///
    /// <para>
    /// Only the rendezvous route appears to need it. There, every captured session has the console send this
    /// after the client has probed the A/V leg and reported back, and the client opens the stream association
    /// within milliseconds of receiving it — whereas a LAN console answers the SESSION_REQUEST whether or not
    /// anything like this has passed. **[X]** what the console requires before sending it: our own sessions
    /// reach <see cref="TypeSessionId"/> and the probe, and this never arrives.
    /// </para>
    /// </summary>
    public const ushort TypeStreamReady = 0x0034;

    /// <summary>
    /// Client → console: put the console into rest mode on this disconnect. Empty payload. Isolated in cap52
    /// by diffing a rest-on disconnect against a rest-off one — the only difference was this frame (the console
    /// acks with <see cref="TypeRestModeAck"/>). Send it before tearing the session down; omit it to leave the
    /// console awake.
    /// </summary>
    public const ushort TypeRestMode = 0x0050;

    /// <summary>Console → client: acknowledges <see cref="TypeRestMode"/> (carries an opaque token we ignore).</summary>
    public const ushort TypeRestModeAck = 0x8050;

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
