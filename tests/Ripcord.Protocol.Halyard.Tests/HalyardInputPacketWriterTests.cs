using Ripcord.Core.Input;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Controller-input serialization: the writer emits input state (type 6) and history (type 1) packets.
/// These assert the byte layout against spec §6.3 — re-derived from our own instrumented capture
/// (<c>cap48</c>) on 2026-07-29 — covering the header, the 0x1c state payload, button-history events, and
/// re-send behaviour, so a wire-format regression is caught off-device.
/// </summary>
public class HalyardInputPacketWriterTests
{
    private const int HeaderLength = 0xc;
    private const long Ms = TimeSpan.TicksPerMillisecond;

    private static HalyardInputPacketWriter NewWriter() => new(new PassthroughHalyardSessionCrypto());

    private static ControllerStateFrame Frame(
        long ticks = 0,
        ControllerButtons buttons = ControllerButtons.None,
        float lx = 0, float ly = 0, float rx = 0, float ry = 0,
        float lt = 0, float rt = 0)
        => new(ticks, buttons, lx, ly, rx, ry, lt, rt, null, null, null);

    [Fact]
    public void FirstFrameEmitsStateOnly()
    {
        var writer = NewWriter();
        var packets = writer.BuildPackets(Frame());

        Assert.Single(packets);
        Assert.Equal(0x06, packets[0][0]); // input state
        Assert.Equal(HeaderLength + 0x1c, packets[0].Length);
    }

    [Fact]
    public void StatePacketHeaderAndPayloadLayout()
    {
        var writer = NewWriter();
        // Full-right (lx +1) and full-up (ly +1). Y is inverted at the wire boundary, so ly +1 -> +0x7fff.
        byte[] state = writer.BuildPackets(Frame(lx: 1f, ly: 1f, rx: 0f, ry: 0f))[0];

        Assert.Equal(0x06, state[0]);
        Assert.Equal(0x00, state[1]); // seq high
        Assert.Equal(0x00, state[2]); // seq low (first packet)
        Assert.Equal(0x00, state[3]);

        // Layout per spec §6.3, re-derived from our own capture cap48 (2026-07-29).
        ReadOnlySpan<byte> p = state.AsSpan(HeaderLength);
        Assert.Equal(0xa0, p[0]);                       // lead byte: 0xa0 in 100% of captured state packets
        // Absent gyro/accel encode to the observed resting value 0x7fff (u16 little-endian) — the value the
        // three gyro axes sit at on stationary hardware.
        Assert.Equal(0xff, p[0x1]);
        Assert.Equal(0x7f, p[0x2]);
        // left_x = +1.0 -> 0x7fff big-endian. left_y neutral = +1.0 (up); Y is inverted at the wire boundary,
        // so it encodes -32767 = 0x8001 big-endian (stick-up = negative on the console wire — confirmed in
        // cap48, where the scripted "full up" excursion drove this field to -32767).
        Assert.Equal(0x7f, p[0x11]);
        Assert.Equal(0xff, p[0x12]);
        Assert.Equal(0x80, p[0x13]);
        Assert.Equal(0x01, p[0x14]);
        // Tail. [0x19]/[0x1a] are zero in 100% of captured packets; [0x1b] is 0xca in 98% of them.
        // This last byte is deliberately NOT 0x01: that was the pre-2026-07-29 value, taken from an outside
        // reference, and our own hardware does not send it. Don't "fix" this back without new evidence.
        Assert.Equal(0x00, p[0x19]);
        Assert.Equal(0x00, p[0x1a]);
        Assert.Equal(0xca, p[0x1b]);
    }

    [Fact]
    public void StateSequenceIncrements()
    {
        var writer = NewWriter();
        byte[] a = writer.BuildPackets(Frame(ticks: 0, lx: 0.1f))[0];
        byte[] b = writer.BuildPackets(Frame(ticks: 15 * Ms, lx: 0.2f))[0];
        Assert.Equal(0x00, a[2]);
        Assert.Equal(0x01, b[2]);
    }

    [Fact]
    public void NoStateWithinFifteenMs_EvenWhenSticksMove()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        Assert.Empty(writer.BuildPackets(Frame(ticks: 8 * Ms, lx: 0.5f)));
    }

    [Fact]
    public void StateEveryFifteenMs_EvenWhenIdle()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        var packets = writer.BuildPackets(Frame(ticks: 15 * Ms));
        Assert.Equal(0x06, Assert.Single(packets)[0]);
    }

    [Fact]
    public void ButtonPressEmitsHistoryEventWithStateByte()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0)); // establish previous
        var packets = writer.BuildPackets(Frame(ticks: 5 * Ms, buttons: ControllerButtons.South));

        byte[] history = Assert.Single(packets, pkt => pkt[0] == 0x01);
        Assert.Equal(0x01, history[0]);
        Assert.Equal(0x00, history[2]); // history seq starts at 0
        // Cross press: [0x80, 0x88, 0xff].
        Assert.Equal(new byte[] { 0x80, 0x88, 0xff }, history.AsSpan(HeaderLength).ToArray());
    }

    [Fact]
    public void StateInCodeButtonFoldsPressIntoCode()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        // Options (Start) press: released code 0x8c, +0x20 when pressed -> 0xac, no state byte.
        byte[] press = writer.BuildPackets(Frame(ticks: 5 * Ms, buttons: ControllerButtons.Start))
            .Single(pkt => pkt[0] == 0x01);
        Assert.Equal(new byte[] { 0x80, 0xac }, press.AsSpan(HeaderLength).ToArray());

        // Release -> 0x8c, newest-first, with the prior press (0xac) re-sent for loss resilience.
        byte[] release = writer.BuildPackets(Frame(ticks: 10 * Ms, buttons: ControllerButtons.None))
            .Single(pkt => pkt[0] == 0x01);
        Assert.Equal(new byte[] { 0x80, 0x8c, 0x80, 0xac }, release.AsSpan(HeaderLength).ToArray());
    }

    [Fact]
    public void TriggerChangeEmitsAnalogHistoryEvent()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        byte[] history = writer.BuildPackets(Frame(ticks: 5 * Ms, lt: 1f)).Single(pkt => pkt[0] == 0x01);
        // L2 analog: [0x80, 0x86, 0xff].
        Assert.Equal(new byte[] { 0x80, 0x86, 0xff }, history.AsSpan(HeaderLength).ToArray());
    }

    [Fact]
    public void HistoryResendsRecentEventsNewestFirst()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        writer.BuildPackets(Frame(ticks: 5 * Ms, buttons: ControllerButtons.South)); // event A
        byte[] second = writer.BuildPackets(Frame(ticks: 10 * Ms,
            buttons: ControllerButtons.South | ControllerButtons.East)) // event B (East press)
            .Single(pkt => pkt[0] == 0x01);

        // Newest-first: East press (0x89) then the re-sent Cross press (0x88).
        Assert.Equal(new byte[] { 0x80, 0x89, 0xff, 0x80, 0x88, 0xff }, second.AsSpan(HeaderLength).ToArray());
    }

    [Fact]
    public void NoHistoryWhenButtonsUnchanged()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0, buttons: ControllerButtons.South));
        var packets = writer.BuildPackets(Frame(ticks: 5 * Ms, buttons: ControllerButtons.South));
        Assert.DoesNotContain(packets, pkt => pkt[0] == 0x01);
    }

    [Fact]
    public void UnchangedHistoryRepeatsEveryTwoHundredMs()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        byte[] first = writer.BuildPackets(Frame(ticks: 5 * Ms, buttons: ControllerButtons.South))
            .Single(pkt => pkt[0] == 0x01);

        Assert.DoesNotContain(
            writer.BuildPackets(Frame(ticks: 150 * Ms, buttons: ControllerButtons.South)), pkt => pkt[0] == 0x01);

        byte[] repeat = writer.BuildPackets(Frame(ticks: 205 * Ms, buttons: ControllerButtons.South))
            .Single(pkt => pkt[0] == 0x01);
        Assert.Equal(first.AsSpan(HeaderLength).ToArray(), repeat.AsSpan(HeaderLength).ToArray());
    }

    [Fact]
    public void HistoryKeepsTheNewestEventsThatFitInOneHundredBytes()
    {
        var writer = NewWriter();
        writer.BuildPackets(Frame(ticks: 0));
        byte[] last = [];
        for (int i = 1; i <= 40; i++)
        {
            var buttons = i % 2 == 1 ? ControllerButtons.South : ControllerButtons.None;
            last = writer.BuildPackets(Frame(ticks: i * 5 * Ms, buttons: buttons)).Single(pkt => pkt[0] == 0x01);
        }

        // Each Cross event is three bytes, so 33 fit; the newest (a release) leads.
        Assert.Equal(99, last.Length - HeaderLength);
        Assert.Equal(new byte[] { 0x80, 0x88, 0x00 }, last.AsSpan(HeaderLength, 3).ToArray());
    }
}
