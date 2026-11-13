using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>
/// The RPCS command inventory (spec §9), named from session 004's own decrypt (RPCS-SEMANTICS.md) and the
/// decompiled incoming dispatch <c>FUN_1801e5000</c> (RPCS-DISPATCH-NAMES.md). The wire framing and these command
/// numbers + payloads are confirmed from our own capture; the human-readable <em>names</em> are behavioural
/// inferences (<c>[X]</c>) except where noted. A client reaching a stream must, at minimum, <b>send the keepalive
/// (<see cref="Keepalive"/>)</b> and <b>accept</b> the incoming setters/stream-channel messages; <see cref="SessionId"/>
/// and <see cref="ErrorStatus"/> are the two incoming messages worth acting on.
/// </summary>
public static class HalyardRpcsCommands
{
    /// <summary>A command with this bit set is a reply to the request of the same low command (e.g. <c>0x8910</c>→<c>0x0910</c>).</summary>
    public const ushort ReplyBit = 0x8000;

    // ---- outgoing (client -> console), confirmed in session 004 ----

    /// <summary>Periodic keepalive (confirmed periodic; payload <see cref="KeepalivePayload"/>). The client must send this.</summary>
    public const ushort Keepalive = 0x0036;

    /// <summary>The path/MTU probe report — carries the MTU hint (<c>0x05ae</c> = 1454 in 004). See <see cref="BuildProbeReportPayload"/>.</summary>
    public const ushort ProbeReport = 0x000d;

    /// <summary>An echo request the console answers with <see cref="EchoReply"/>; payload <see cref="EchoPayload"/>.</summary>
    public const ushort EchoRequest = 0x0910;

    /// <summary>The console's reply to <see cref="EchoRequest"/> (= <see cref="EchoRequest"/> | <see cref="ReplyBit"/>).</summary>
    public const ushort EchoReply = 0x8910;

    // ---- incoming (console -> client), named from the dispatch handler ----

    /// <summary>Console error/status code (translated into the client's <c>0x80108d1x</c> space) — worth surfacing.</summary>
    public const ushort ErrorStatus = 0x0003;

    /// <summary>A 1-byte toggle (active/standby or video-enable) <c>[X]</c>.</summary>
    public const ushort Toggle = 0x0005;

    /// <summary>Length-prefixed session-id string (carried "InvalidSessionId" in 004) — confirmed.</summary>
    public const ushort SessionId = 0x0033;

    /// <summary>On-screen dialog result: <c>'O'</c>/<c>'X'</c> (PlayStation circle/cross = confirm/cancel) — confirmed.</summary>
    public const ushort DialogResult = 0x0035;

    /// <summary>The four stream-channel setup messages (callback indices 0..3 — the home for video/audio stream params <c>[X]</c>).</summary>
    public const ushort StreamChannel0 = 0x0021;
    public const ushort StreamChannel1 = 0x0022;
    public const ushort StreamChannel2 = 0x0024;
    public const ushort StreamChannel3 = 0x0026;

    /// <summary>Teardown/flush (no payload).</summary>
    public const ushort Teardown = 0x00fe;

    // ---- payloads ----

    /// <summary>The keepalive body, byte-exact from 004 (<c>00 01 01 00</c>, sent unchanged each time).</summary>
    public static ReadOnlySpan<byte> KeepalivePayload => [0x00, 0x01, 0x01, 0x00];

    /// <summary>The echo request/reply body observed in 004 (<c>00 00 00 00</c>).</summary>
    public static ReadOnlySpan<byte> EchoPayload => [0x00, 0x00, 0x00, 0x00];

    /// <summary>The MTU value carried by the probe report in 004 (1454 = <c>0x05ae</c>).</summary>
    public const ushort DefaultMtu = 1454;

    /// <summary>
    /// The <see cref="ProbeReport"/> payload: 16 bytes, the MTU as a big-endian u16 at offset 6, the rest zero —
    /// the byte-exact form from 004 (<c>00 00 00 00 00 00 05 ae</c> + 8×<c>00</c>). The other fields read zero in
    /// the single capture and whether the console reads them is <c>[X]</c>.
    /// </summary>
    public static byte[] BuildProbeReportPayload(ushort mtu = DefaultMtu)
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), mtu);
        return payload;
    }
}
