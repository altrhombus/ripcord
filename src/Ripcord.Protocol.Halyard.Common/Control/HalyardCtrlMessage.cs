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
    /// "you may stream now" signal is <see cref="TypeSessionId"/>.
    /// <para>Payload decrypts to a single <c>0x00</c> on the unlocked path (2026-09-04, live).</para></summary>
    public const ushort TypeLogin = 0x0005;

    /// <summary>
    /// <see cref="TypeLogin"/>'s payload: the passcode was accepted.
    ///
    /// <para>
    /// **[C]** by controlled experiment against hardware (2026-09-05): one console, one variable changed, two
    /// outcomes — the right passcode drew <c>0x00</c> and a deliberately wrong one drew
    /// <see cref="LoginRejected"/>. This byte was previously recorded as opaque and different every time,
    /// which it is as <em>ciphertext</em>: a stream cipher at a fresh counter gives different bytes for the
    /// same plaintext by construction, so that was a fact about not being able to decrypt it. Values other
    /// than these two remain unseen.
    /// </para>
    /// </summary>
    public const byte LoginAccepted = 0x00;

    /// <summary>
    /// <see cref="TypeLogin"/>'s payload: the passcode was wrong. See <see cref="LoginAccepted"/> for how
    /// both were established.
    /// </summary>
    public const byte LoginRejected = 0x01;

    /// <summary>
    /// Console → client: session-ready. After a login it is what gates the Takion bring-up.
    ///
    /// <para>
    /// Its payload decrypts to a <b>length-prefixed ASCII session id</b> — <c>0x10</c> followed by
    /// <c>"InvalidSessionId"</c>, the same literal the <c>SESSION_REQUEST</c> carries. That is what made it
    /// the known-plaintext oracle for the control-channel counter model; see
    /// <c>docs/protocol/ps5-session-transport.md</c>.
    /// </para>
    /// </summary>
    public const ushort TypeSessionId = 0x0033;

    /// <summary>
    /// Client → console: the report the captured client sends a second or two after the bandwidth probe, on
    /// both routes. The console answers <see cref="TypeProbeReportAck"/>, and on the rendezvous route then
    /// sends <see cref="TypeStreamReady"/>.
    ///
    /// <para>
    /// <b>Structure is solved; content is not.</b> The payload is four <c>uint32</c> in network byte order
    /// (first-party RE of our own client: builder <c>FUN_1020c1c0</c>, each word through the <c>htonl</c>
    /// thunk <c>FUN_101ee560</c>, read from a four-slot object in the order <c>+4, +8, +0xc, +0x10</c>, and
    /// not emitted at all until every slot has been measured). **[X]** which measurement each slot carries.
    /// The binary says the slots are three independent measurements — <c>+4</c>, <c>+8</c>, and the pair
    /// <c>+0xc</c>/<c>+0x10</c> — and that <c>+0x10</c> is a time in milliseconds. The quantities in play are
    /// bandwidth, loss, mtu, upMtu and rtt. See <c>docs/protocol/ps5-session-transport.md</c>.
    /// </para>
    /// </summary>
    public const ushort TypeProbeReport = 0x000d;

    /// <summary>Console → client: the 8-byte answer to <see cref="TypeProbeReport"/>. **[X]** contents.</summary>
    public const ushort TypeProbeReportAck = 0x0010;

    /// <summary>
    /// Client → console, 4 bytes, answered by <see cref="TypeEchoProbeAck"/> — the high bit of the type marks
    /// the answer, as it does for the login pair. **[X]** contents; it is useful here precisely because it is
    /// the one client frame with a guaranteed reply, which makes it an oracle for whether the console is
    /// reading what we send at all.
    /// </summary>
    public const ushort TypeEchoProbe = 0x0910;

    /// <summary>Console → client: the 4-byte answer to <see cref="TypeEchoProbe"/>.</summary>
    public const ushort TypeEchoProbeAck = 0x8910;

    /// <summary>
    /// Console → client. <b>Exactly 2 bytes</b>, or the client drops it; read as one 16-bit value and raised
    /// as an internal event. Observed payload <c>01 FF</c>.
    ///
    /// <para>
    /// Structure from first-party RE of our own client (<c>FUN_102089b0</c>, case <c>0x16</c>). **[X]** what
    /// the value means, and its byte order is not determinable from a single sample — the surrounding
    /// protocol is big-endian, but this one field is loaded natively where its neighbours are assembled byte
    /// by byte.
    /// </para>
    /// </summary>
    public const ushort TypeUnknown0016 = 0x0016;

    /// <summary>
    /// Console → client. A <b>counted list</b>: one count byte, then <c>count</c> entries of four bytes, each
    /// entry being two big-endian <c>uint16</c>. The client requires the length to be exactly
    /// <c>count * 4 + 1</c> and drops the frame otherwise.
    ///
    /// <para>
    /// Structure from first-party RE (<c>FUN_102089b0</c>, case <c>0x17</c>) and confirmed against the live
    /// payload: <c>02 | 0004 0224 | 0055 1234</c> is count 2 followed by the pairs (4, 548) and (85, 4660).
    /// **[X]** what the pairs are. Worth noting only that 548 is the size of the bandwidth probe's datagrams,
    /// which may be coincidence.
    /// </para>
    /// </summary>
    public const ushort TypeUnknown0017 = 0x0017;

    /// <summary>
    /// Console → client. <b>Exactly 8 bytes</b>, copied verbatim and raised as an internal event; the client
    /// does not interpret them at this layer. Observed payload <c>00 00 00 00 02 01 00 00</c>.
    ///
    /// <para>
    /// Structure from first-party RE (<c>FUN_102089b0</c>, case <c>0x41</c>). **[X]** contents. Arrives on the
    /// LAN route after the stream is up; not seen on the rendezvous route.
    /// </para>
    /// </summary>
    public const ushort TypeUnknown0041 = 0x0041;

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
