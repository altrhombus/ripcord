using System.Buffers.Binary;
using Ripcord.Core.Input;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Input;

/// <summary>
/// Serializes neutral <see cref="ControllerStateFrame"/> snapshots into up-direction controller-feedback
/// packets and seals them through the crypto seam. Two packet kinds (spec §6.3), both sharing a 12-byte
/// header <c>[type][seq u16 BE][0][keyPos u32][gmac u32]</c> followed by an AES-CTR-encrypted payload:
///
/// <list type="bullet">
///   <item><description><b>Feedback state</b> (type 6): a periodic analog snapshot — sticks, and (when
///   present) motion/orientation. Fixed 0x1c-byte payload.</description></item>
///   <item><description><b>Feedback history</b> (type 1): button/trigger <em>transitions</em> as a list of
///   short events, sent only when something changed. Recent events are re-sent in later history packets so a
///   dropped packet doesn't strand a press/release (the channel is unreliable).</description></item>
/// </list>
///
/// <para>
/// <b>Provenance.</b> Written against spec §6.3, which was derived from our own instrumented capture
/// (<c>cap48</c>: 10,165 state packets and 1,258 history packets decrypted with the session's own stream keys,
/// correlated against a deliberately isolated input sequence). Field positions, encodings and button codes
/// below are each cited to that capture. The few values the capture could *not* settle are marked as
/// unconfirmed — leave them marked, and derive them rather than filling them in from anywhere else.
/// </para>
///
/// All up-direction packets draw their key position from one shared counter inside the crypto seam
/// (<see cref="IHalyardSessionCrypto.SealFeedbackPacket"/>), so feedback, control and congestion never
/// reuse a nonce.
/// </summary>
public sealed class HalyardInputPacketWriter(IHalyardSessionCrypto crypto)
{
    private const byte TypeFeedbackHistory = 0x01;
    private const byte TypeFeedbackState = 0x06;

    /// <summary>
    /// Header before the payload: [type][seq u16 BE][0][keyPos u32][gmac u32]. Verified by decrypting
    /// cap48: the feedback key position reads as a u32 at offset 4 and the payload begins at offset 12.
    /// </summary>
    private const int HeaderLength = 0xc;

    /// <summary>
    /// The feedback-state payload is a fixed 0x1c bytes — 10,165 of 10,165 state packets in cap48 were
    /// exactly this length.
    /// </summary>
    private const int StatePayloadLength = 0x1c;

    /// <summary>Lead byte of every state payload; 0xa0 in 100% of captured state packets.</summary>
    private const byte StateLeadByte = 0xa0;

    /// <summary>
    /// Observed value of the state payload's final byte (0xca in 98% of captured packets; 0x00 in the rest).
    /// Its meaning is unknown. Our own hardware sends 0xca here — if you find a source claiming 1, ours
    /// disagrees with it, so don't "fix" this to 1 without a capture that says otherwise.
    /// </summary>
    private const byte StateTailByte = 0xca;

    /// <summary>
    /// Resting value of the motion fields: the three gyro axes sit at exactly this in a capture of a
    /// stationary pad, i.e. it is the "no rotation" encoding. Also used for accelerometer axes when no
    /// motion data is available, so an absent sensor sends a self-consistent neutral reading.
    /// </summary>
    private const ushort SensorRest = 0x7fff;

    /// <summary>
    /// How often to re-send a state packet when nothing analog changed. This is <em>our</em> send policy, not
    /// a wire requirement — so far as we know the console does not mandate a cadence, though that has never
    /// been tested by changing it. Treat it as a knob that wants a live session to validate rather than a free
    /// one: lengthening it risks a console-side state timeout that would only show up on hardware.
    /// </summary>
    private const long StateKeepaliveTicks = 200 * TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// How many recent history events to repeat in later history packets to survive a dropped packet. Our own
    /// policy and genuinely free to tune: events carry absolute state, so re-sends are idempotent and no value
    /// here can desync the console.
    /// </summary>
    private const int HistoryResendCount = 4;

    private readonly IHalyardSessionCrypto _crypto = crypto;

    private ushort _stateSeq;
    private ushort _historySeq;

    // Previous frame, to diff button/trigger transitions and detect analog change for the keepalive cadence.
    private bool _havePrev;
    private ControllerButtons _prevButtons;
    private byte _prevLeftTrigger;
    private byte _prevRightTrigger;
    private short _prevLeftX, _prevLeftY, _prevRightX, _prevRightY;
    private long _lastStateTicks;

    // Recent history events, newest-first (index 0 = most recent). cap48 confirms newest-first ordering:
    // consecutive history packets are cumulative, with each new event PREPENDED (an older packet is a strict
    // suffix of the next). That property is what let the atomic event boundaries be recovered empirically.
    private readonly List<byte[]> _history = [];

    /// <summary>
    /// Build the feedback packets for a controller frame: a state packet when the analog state changed or the
    /// keepalive elapsed, and a history packet when any button/trigger transitioned. Returns 0–2 packets, each
    /// sealed and ready to send on the stream socket.
    /// </summary>
    public IReadOnlyList<byte[]> BuildPackets(in ControllerStateFrame frame)
    {
        var packets = new List<byte[]>(2);

        // The neutral frame uses stick-up = +1 (GameInput convention); the console's wire expects stick-up
        // negative, so the Y axes are inverted here at the wire boundary (X is unchanged). Confirmed in
        // cap48: the scripted "full up" excursion drove the left-Y field to -32767.
        short leftX = ToAxis(frame.LeftStickX);
        short leftY = ToAxis(-frame.LeftStickY);
        short rightX = ToAxis(frame.RightStickX);
        short rightY = ToAxis(-frame.RightStickY);
        byte leftTrigger = ToU8(frame.LeftTrigger);
        byte rightTrigger = ToU8(frame.RightTrigger);

        // History: emit an event for every button/trigger transition since the previous frame.
        bool historyDirty = false;
        if (_havePrev)
        {
            ControllerButtons changed = _prevButtons ^ frame.Buttons;
            foreach ((ControllerButtons flag, byte code, bool stateInCode) in ButtonMap)
            {
                if ((changed & flag) == 0)
                {
                    continue;
                }

                bool pressed = (frame.Buttons & flag) != 0;
                PushHistory(BuildButtonEvent(code, stateInCode, pressed));
                historyDirty = true;
            }

            if (leftTrigger != _prevLeftTrigger)
            {
                PushHistory(BuildAnalogEvent(L2Code, leftTrigger));
                historyDirty = true;
            }

            if (rightTrigger != _prevRightTrigger)
            {
                PushHistory(BuildAnalogEvent(R2Code, rightTrigger));
                historyDirty = true;
            }
        }

        if (historyDirty)
        {
            packets.Add(BuildHistoryPacket());
        }

        // State: send when a stick moved (quantized) or the keepalive interval elapsed.
        bool analogChanged = !_havePrev
            || leftX != _prevLeftX || leftY != _prevLeftY || rightX != _prevRightX || rightY != _prevRightY;
        bool keepalive = frame.TimestampTicks - _lastStateTicks >= StateKeepaliveTicks;
        if (analogChanged || keepalive)
        {
            packets.Add(BuildStatePacket(frame, leftX, leftY, rightX, rightY));
            _lastStateTicks = frame.TimestampTicks;
        }

        _prevButtons = frame.Buttons;
        _prevLeftTrigger = leftTrigger;
        _prevRightTrigger = rightTrigger;
        _prevLeftX = leftX;
        _prevLeftY = leftY;
        _prevRightX = rightX;
        _prevRightY = rightY;
        _havePrev = true;
        return packets;
    }

    /// <summary>
    /// The 0x1c-byte state payload. Offsets are exactly as mapped in spec §6.3 from cap48:
    /// <c>[0x00]</c> lead, <c>[0x01..0x0c]</c> six u16 LE motion fields (three gyro then three accelerometer,
    /// the split established by their resting medians), <c>[0x0d..0x10]</c> packed orientation,
    /// <c>[0x11..0x18]</c> four s16 BE stick axes, <c>[0x19]</c>/<c>[0x1a]</c> zero, <c>[0x1b]</c> tail.
    /// </summary>
    private byte[] BuildStatePacket(in ControllerStateFrame frame, short leftX, short leftY, short rightX, short rightY)
    {
        byte[] packet = new byte[HeaderLength + StatePayloadLength];
        packet[0] = TypeFeedbackState;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), _stateSeq++);
        // packet[3] = 0; keyPos@4 + gmac@8 written by the crypto seal.

        Span<byte> p = packet.AsSpan(HeaderLength);
        p[0x0] = StateLeadByte;
        WriteSensor(p[0x1..], frame.Gyro?.X);
        WriteSensor(p[0x3..], frame.Gyro?.Y);
        WriteSensor(p[0x5..], frame.Gyro?.Z);
        WriteSensor(p[0x7..], frame.Accel?.X);
        WriteSensor(p[0x9..], frame.Accel?.Y);
        WriteSensor(p[0xb..], frame.Accel?.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(p[0xd..], RestingOrientation);
        BinaryPrimitives.WriteInt16BigEndian(p[0x11..], leftX);
        BinaryPrimitives.WriteInt16BigEndian(p[0x13..], leftY);
        BinaryPrimitives.WriteInt16BigEndian(p[0x15..], rightX);
        BinaryPrimitives.WriteInt16BigEndian(p[0x17..], rightY);
        // [0x19] and [0x1a] stay zero — constant in 100% of captured packets.
        p[0x1b] = StateTailByte;

        _crypto.SealFeedbackPacket(packet, HeaderLength);
        return packet;
    }

    private byte[] BuildHistoryPacket()
    {
        int payloadLength = 0;
        foreach (byte[] e in _history)
        {
            payloadLength += e.Length;
        }

        byte[] packet = new byte[HeaderLength + payloadLength];
        packet[0] = TypeFeedbackHistory;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), _historySeq++);

        int offset = HeaderLength;
        foreach (byte[] e in _history) // newest-first
        {
            e.CopyTo(packet.AsSpan(offset));
            offset += e.Length;
        }

        // Keep only the newest few events to re-send in the next history packet (loss resilience).
        if (_history.Count > HistoryResendCount)
        {
            _history.RemoveRange(HistoryResendCount, _history.Count - HistoryResendCount);
        }

        _crypto.SealFeedbackPacket(packet, HeaderLength);
        return packet;
    }

    private void PushHistory(byte[] evt) => _history.Insert(0, evt);

    // ---- history event encoders (spec §6.3, forms observed directly in cap48) ----

    private const byte L2Code = 0x86;
    private const byte R2Code = 0x87;

    /// <summary>Codes at or below this use the 3-byte form; above it, the 2-byte form.</summary>
    private const byte LastThreeByteCode = 0x8b;

    /// <summary>Added to a 2-byte-form code to signal "pressed" (observed as the pair 0x8e ↔ 0xae).</summary>
    private const byte PressedCodeBias = 0x20;

    private static byte[] BuildButtonEvent(byte code, bool stateInCode, bool pressed)
    {
        if (stateInCode)
        {
            // Press/release folded into the code itself; no trailing state byte.
            return [0x80, (byte)(pressed ? code + PressedCodeBias : code)];
        }

        return [0x80, code, pressed ? (byte)0xff : (byte)0x00];
    }

    /// <summary>
    /// L2/R2 carry a 0–255 level in the third byte rather than a boolean. Proven independently from cap48:
    /// codes 0x86/0x87 take 57 and 52 distinct state values across the session, where every other 3-byte
    /// code only ever carries 0x00 or 0xff.
    /// </summary>
    private static byte[] BuildAnalogEvent(byte code, byte state) => [0x80, code, state];

    /// <summary>
    /// Neutral button → feedback-history code, per the spec §6.3 table derived from cap48. Codes 0x80–0x8b
    /// use the 3-byte form (trailing 0xff/0x00 state byte); 0x8c–0x90 use the 2-byte form with the press bit
    /// folded into the code. The whole observed code space is 0x80–0x90 and nothing else appeared.
    /// </summary>
    private static readonly (ControllerButtons Flag, byte Code, bool StateInCode)[] ButtonMap =
    [
        (ControllerButtons.South, 0x88, false),         // cross         — first press t=77.1s
        (ControllerButtons.East, 0x89, false),          // circle        — t=78.9s
        (ControllerButtons.West, 0x8a, false),          // square        — t=80.9s
        (ControllerButtons.North, 0x8b, false),         // triangle      — t=82.4s
        // D-pad up/down are assigned BY ELIMINATION: both were pressed during an earlier period of
        // misbehaving input, so their first-press times predate the scripted D-pad step and cannot order
        // them. Left/right are directly timed. One clean press of each would settle it.
        (ControllerButtons.DPadUp, 0x80, false),
        (ControllerButtons.DPadDown, 0x81, false),
        (ControllerButtons.DPadLeft, 0x82, false),      // t=87.8s
        (ControllerButtons.DPadRight, 0x83, false),     // t=89.1s
        (ControllerButtons.LeftShoulder, 0x84, false),  // L1            — t=91.4s
        (ControllerButtons.RightShoulder, 0x85, false), // R1            — t=92.5s
        (ControllerButtons.LeftStick, 0x8f, true),      // L3            — t=114.5s
        (ControllerButtons.RightStick, 0x90, true),     // R3            — t=115.8s
        (ControllerButtons.Start, 0x8c, true),          // options       — t=119.6s
        (ControllerButtons.Select, 0x8d, true),         // share/create  — t=124.0s
        (ControllerButtons.Guide, 0x8e, true),          // PS            — t=0.0s (opened the session)
        // NOT CONFIRMED by our capture: the scripted touchpad-click step produced no eighteenth code, so
        // this entry is the one value here still owed an independent derivation. It is retained so touchpad
        // clicks keep working, but treat it as provisional — the click may instead be carried by the
        // "00 00 00 21" event family that our history parser currently skips (spec §6.3).
        (ControllerButtons.TouchpadClick, 0x91, true),
    ];

    // ---- analog encoders ----

    /// <summary>
    /// Write one motion axis. A null reading (no motion sensor, or gyro/accel not yet wired) sends the
    /// resting encoding observed on real hardware.
    ///
    /// <para>
    /// <b>The full-scale range is not yet derived.</b> Our capture establishes where these fields live, that
    /// they are u16 LE, and where they rest — but not what physical rate/acceleration the endpoints
    /// correspond to, because deriving that needs a capture with known applied motion. Until then a non-null
    /// reading is treated as an already-normalised [-1,1] fraction of full scale. Wiring real sensor data
    /// (ROADMAP Track E) must derive the scale first, or motion will be sent at the wrong magnitude.
    /// </para>
    /// </summary>
    private static void WriteSensor(Span<byte> dest, float? normalized)
    {
        ushort value = normalized is null
            ? SensorRest
            : (ushort)Math.Clamp(
                SensorRest + (int)MathF.Round(Math.Clamp(normalized.Value, -1f, 1f) * SensorRest),
                ushort.MinValue,
                ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(dest, value);
    }

    /// <summary>
    /// Orientation sent when we have no attitude to report.
    ///
    /// <para>
    /// The field is a packed quaternion ~30 bits wide (cap48's maximum observed value has bit 29 set and bit
    /// 30 clear), but the exact packing is <b>not derived</b>. The captured pad was in motion throughout, so
    /// no resting/identity value can be read off it either — 5,025 distinct values, none dominant.
    /// </para>
    /// <para>
    /// Zero is therefore sent deliberately as a documented placeholder: well-defined, stable, and free of
    /// cost today because we never populate gyro/accel, so orientation is inert either way. Deriving the
    /// packing is a prerequisite for real motion support, not for streaming.
    /// </para>
    /// </summary>
    private const uint RestingOrientation = 0u;

    /// <summary>Map a [-1,1] stick axis to the signed 16-bit range the console expects (±32767 observed).</summary>
    private static short ToAxis(float axis)
        => (short)Math.Clamp((int)MathF.Round(Math.Clamp(axis, -1f, 1f) * 32767f), short.MinValue, short.MaxValue);

    /// <summary>Map a [0,1] trigger to 0..255.</summary>
    private static byte ToU8(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
