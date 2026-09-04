using System.Buffers.Binary;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Transport;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The socket pump around <see cref="HalyardControlAssociation"/>.
///
/// <para>
/// Deliberately thin, because the pump is deliberately thin: the protocol's decisions are asserted in
/// <see cref="HalyardControlAssociationTests"/>, where they can be checked synchronously. What is left to test
/// here is the pumping itself — that a whole exchange completes, that a quiet peer is re-tried rather than
/// treated as a failure, and that giving up says something a person can act on.
/// </para>
/// </summary>
public class HalyardDatagramControlChannelTests
{
    private static readonly byte[] OurId = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
    private static readonly byte[] ConsoleId = [.. Enumerable.Range(101, 20).Select(i => (byte)i)];

    /// <summary>Short by default: these tests should fail fast, not wait out a production deadline.</summary>
    private static HalyardDatagramControlOptions Quick(TimeSpan? stage = null) => new()
    {
        ReceiveTimeout = TimeSpan.FromMilliseconds(20),
        StageTimeout = stage ?? TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// A console that answers each step as it arrives, rather than replaying a fixed script — so a channel
    /// that sent the steps out of order would stall instead of accidentally passing.
    /// </summary>
    private sealed class ScriptedConsole : IHalyardDatagramTransport
    {
        private readonly Queue<byte[]> _outbound = new();

        public List<byte[]> Sent { get; } = [];

        public uint Token { get; } = 0x01B9ACBE;

        public ushort Sequence { get; } = 0xA65E;

        /// <summary>Drop this many opening Inits, standing in for a hole-punch that needs retries.</summary>
        public int SwallowInits { get; set; }

        public int Disposals { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            byte[] copy = datagram.ToArray();
            Sent.Add(copy);

            if (HalyardControlPrelude.TryParse(copy, out HalyardControlPrelude prelude))
            {
                if (prelude.Type != HalyardControlPrelude.Init)
                {
                    return ValueTask.CompletedTask;
                }

                if (SwallowInits > 0)
                {
                    SwallowInits--;
                    return ValueTask.CompletedTask;
                }

                // Answer, then echo — the shape a real console uses.
                _outbound.Enqueue(new HalyardControlPrelude(
                    HalyardControlPrelude.Init, ConsoleId, OurId, prelude.SwappedTagPair,
                    RequestWord: 0, Token: Token, Tail: ReadOnlyMemory<byte>.Empty).Serialize());
                _outbound.Enqueue(new HalyardControlPrelude(
                    HalyardControlPrelude.CookieEcho, ConsoleId, OurId, prelude.TagPair,
                    RequestWord: 0, Token: prelude.Token, Tail: ReadOnlyMemory<byte>.Empty).Serialize());
                return ValueTask.CompletedTask;
            }

            foreach (HalyardControlChunk chunk in HalyardControlChunkCodec.ReadAll(copy))
            {
                switch (chunk.Type)
                {
                    case HalyardControlChunkType.Hello:
                        _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                            HalyardControlChunkType.Cookie, 0x00,
                            [.. Enumerable.Range(0, 42).Select(i => (byte)(0xE0 + i))]));
                        break;

                    case HalyardControlChunkType.HelloEcho:
                        ushort helloSeq = BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span);
                        byte[] accept = new byte[12];
                        BinaryPrimitives.WriteUInt16BigEndian(accept, Sequence);
                        BinaryPrimitives.WriteUInt16BigEndian(accept.AsSpan(2), (ushort)(helloSeq + 1));
                        _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                            HalyardControlChunkType.Accept, 0x30, accept));
                        break;

                    case HalyardControlChunkType.Data:
                        const string response = "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nabcd";
                        byte[] body = new byte[2 + response.Length];
                        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)(Sequence + 1));
                        Encoding.ASCII.GetBytes(response).CopyTo(body, 2);
                        _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                            HalyardControlChunkType.Data, 0x30, body));
                        break;
                }
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_outbound.Count > 0)
            {
                return _outbound.Dequeue();
            }

            // Nothing to say. Wait to be cancelled, like a real socket with a quiet peer, rather than
            // returning instantly and spinning the pump.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException();
        }

        public void Dispose() => Disposals++;
    }

    private static HalyardDatagramControlChannel Channel(
        IHalyardDatagramTransport transport, HalyardDatagramControlOptions? options = null)
        => new(transport, OurId, ConsoleId, options ?? Quick());

    [Fact]
    public async Task Exchange_CompletesTheWholeCycle_AndReturnsTheHttpResponse()
    {
        var console = new ScriptedConsole();
        await using HalyardDatagramControlChannel channel = Channel(console);

        byte[] response = await channel.ExchangeAsync(
            "POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n\r\n"u8.ToArray(), CancellationToken.None);

        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.ASCII.GetString(response));
        Assert.Equal(HalyardControlPhase.Connected, channel.Phase);
    }

    [Fact]
    public async Task Exchange_RetriesTheOpeningInit_WhenTheFirstProbesGoUnanswered()
    {
        // On a WAN path the prelude doubles as the hole-punch and early probes are expected to be dropped.
        var console = new ScriptedConsole { SwallowInits = 2 };
        await using HalyardDatagramControlChannel channel = Channel(console);

        byte[] response = await channel.ExchangeAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), CancellationToken.None);

        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.ASCII.GetString(response));

        int inits = console.Sent.Count(d =>
            HalyardControlPrelude.TryParse(d, out HalyardControlPrelude p) && p.Type == HalyardControlPrelude.Init);
        Assert.Equal(3, inits);
    }

    [Fact]
    public async Task Exchange_UnansweredPrelude_ReportsSomethingActionable()
    {
        var console = new ScriptedConsole { SwallowInits = int.MaxValue };
        await using HalyardDatagramControlChannel channel = Channel(
            console, Quick(stage: TimeSpan.FromMilliseconds(200)));

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            channel.ExchangeAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), CancellationToken.None));

        Assert.Contains("9303", ex.Message);
        Assert.Contains("candidate exchange", ex.Message);
    }

    [Fact]
    public async Task Exchange_HonoursTheCallersCancellation()
    {
        var console = new ScriptedConsole { SwallowInits = int.MaxValue };
        await using HalyardDatagramControlChannel channel = Channel(
            console, Quick(stage: TimeSpan.FromMinutes(5)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.ExchangeAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), cts.Token));
    }

    [Fact]
    public async Task Dispose_ClosesTheSocket()
    {
        var console = new ScriptedConsole();
        HalyardDatagramControlChannel channel = Channel(console);

        await channel.DisposeAsync();

        Assert.Equal(1, console.Disposals);
    }
}
