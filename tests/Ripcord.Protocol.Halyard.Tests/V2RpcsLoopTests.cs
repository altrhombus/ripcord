using System.Threading.Channels;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Protocol.Halyard.Transport;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The v2 RPCS loop driver (<see cref="HalyardRpcsLoop"/>): it sends the probe report up front, keepalives on a
/// cadence, surfaces each decoded incoming message, and ends on teardown or a closed connection. Driven over a
/// mock <see cref="IHalyardRpcsTransport"/> with the shared GCM double, so incoming frames sealed by a matching
/// channel decode exactly as they would on the wire — the schedule behaviour, not AES-GCM.
/// </summary>
public class V2RpcsLoopTests
{
    private static HalyardRpcsChannel NewChannel() => new(new FakeV2ControlCrypto());

    [Fact]
    public async Task SendsTheProbeReportFirst_ThenStopsOnTeardown()
    {
        var transport = new MockRpcsTransport();
        var console = NewChannel();
        transport.EnqueueIncoming(console.SealMessage(HalyardRpcsCommands.Teardown, []));

        var teardownSeen = false;
        var loop = new HalyardRpcsLoop(transport, NewChannel(), keepaliveInterval: TimeSpan.FromMinutes(1));
        loop.MessageReceived += m => teardownSeen |= m.Cmd == HalyardRpcsCommands.Teardown;

        await loop.RunAsync(CancellationToken.None);

        Assert.True(teardownSeen);
        byte[][] sent = transport.SnapshotSent();
        Assert.NotEmpty(sent);
        HalyardRpcsMessage? first = NewChannel().OpenMessage(sent[0]); // console-side reader, counters aligned
        Assert.Equal(HalyardRpcsCommands.ProbeReport, first!.Value.Cmd);
        Assert.Equal(HalyardRpcsCommands.BuildProbeReportPayload(), first.Value.Payload);
    }

    [Fact]
    public async Task SurfacesIncomingMessages_InOrder()
    {
        var transport = new MockRpcsTransport();
        var console = NewChannel();
        transport.EnqueueIncoming(console.SealMessage(HalyardRpcsCommands.SessionId, "10InvalidSessionId"u8));
        transport.EnqueueIncoming(console.SealMessage(HalyardRpcsCommands.ErrorStatus, [0x80, 0x10, 0x8d, 0x13]));
        transport.EnqueueIncoming(console.SealMessage(HalyardRpcsCommands.Teardown, []));

        var seen = new List<ushort>();
        var loop = new HalyardRpcsLoop(transport, NewChannel(), keepaliveInterval: TimeSpan.FromMinutes(1));
        loop.MessageReceived += m => seen.Add(m.Cmd);

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(
            new ushort[] { HalyardRpcsCommands.SessionId, HalyardRpcsCommands.ErrorStatus, HalyardRpcsCommands.Teardown },
            seen);
    }

    [Fact]
    public async Task StopsWhenTheConnectionCloses()
    {
        var transport = new MockRpcsTransport();
        transport.Close(); // ReadRpcsFrameAsync returns null

        var loop = new HalyardRpcsLoop(transport, NewChannel(), keepaliveInterval: TimeSpan.FromMinutes(1));
        await loop.RunAsync(CancellationToken.None); // returns rather than hanging

        Assert.Single(transport.SnapshotSent()); // just the probe report
    }

    [Fact]
    public async Task SendsKeepalivesOnTheCadence()
    {
        var transport = new MockRpcsTransport();
        var loop = new HalyardRpcsLoop(transport, NewChannel(), keepaliveInterval: TimeSpan.FromMilliseconds(25));

        Task run = loop.RunAsync(CancellationToken.None);
        await Task.Delay(150); // ~6 intervals
        transport.Close();
        await run;

        // Decode every sent frame in order: probe report, then keepalives.
        var reader = NewChannel();
        List<ushort> cmds = [.. transport.SnapshotSent().Select(f => reader.OpenMessage(f)!.Value.Cmd)];
        Assert.Equal(HalyardRpcsCommands.ProbeReport, cmds[0]);
        Assert.True(cmds.Count(c => c == HalyardRpcsCommands.Keepalive) >= 2,
            $"expected >= 2 keepalives, saw {cmds.Count(c => c == HalyardRpcsCommands.Keepalive)}");
    }

    private sealed class MockRpcsTransport : IHalyardRpcsTransport
    {
        private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
        private readonly List<byte[]> _sent = [];
        private readonly object _gate = new();

        public void EnqueueIncoming(byte[] frame) => _incoming.Writer.TryWrite(frame);
        public void Close() => _incoming.Writer.TryWrite(null); // null sentinel = connection closed

        public Task SendRpcsFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            lock (_gate)
                _sent.Add(frame.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReadRpcsFrameAsync(CancellationToken cancellationToken)
            => await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public byte[][] SnapshotSent()
        {
            lock (_gate)
                return [.. _sent];
        }
    }
}
