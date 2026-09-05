using System.Buffers.Binary;
using System.Net;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Transport;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The session control plane over the account route's 9303 transport.
///
/// <para>
/// What is worth asserting here is the part that differs from TCP: that several request/response cycles run
/// as separate chunk connections over <b>one</b> association, and that the persistent control frames then
/// flow on the last of them. The HTTP and frame codecs are shared with the TCP channel and tested elsewhere;
/// this suite is about the transport underneath them.
/// </para>
/// </summary>
public class HalyardDatagramSessionControlChannelTests
{
    private static readonly byte[] OurId = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
    private static readonly byte[] ConsoleId = [.. Enumerable.Range(101, 20).Select(i => (byte)i)];
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("10.0.0.7"), 9303);

    private static readonly string CRLF = new([(char)13, (char)10]);

    private static HalyardDatagramControlOptions Quick() => new()
    {
        ReceiveTimeout = TimeSpan.FromMilliseconds(20),
        StageTimeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// A console that answers each chunk connection and then <b>closes it</b>, the way a real one does, so a
    /// channel that tried to reuse a closed connection would stall rather than accidentally pass.
    /// </summary>
    private sealed class ScriptedConsole : IHalyardDatagramTransport
    {
        private readonly Queue<byte[]> _outbound = new();
        private ushort _sequence = 0xA65E;

        /// <summary>One entry per chunk connection the client opened.</summary>
        public List<string> Requests { get; } = [];

        public int Hellos { get; private set; }

        /// <summary>Frames to push once the client has sent a control frame of its own.</summary>
        public Queue<byte[]> CtrlFrames { get; } = new();

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            byte[] copy = datagram.ToArray();

            if (HalyardControlPrelude.TryParse(copy, out HalyardControlPrelude prelude))
            {
                if (prelude.Type == HalyardControlPrelude.Init)
                {
                    _outbound.Enqueue(new HalyardControlPrelude(
                        HalyardControlPrelude.Init, ConsoleId, OurId, prelude.SwappedTagPair,
                        RequestWord: 0, Token: 0x01B9ACBE, Tail: ReadOnlyMemory<byte>.Empty).Serialize());
                    _outbound.Enqueue(new HalyardControlPrelude(
                        HalyardControlPrelude.CookieEcho, ConsoleId, OurId, prelude.TagPair,
                        RequestWord: 0, Token: prelude.Token, Tail: ReadOnlyMemory<byte>.Empty).Serialize());
                }

                return ValueTask.CompletedTask;
            }

            foreach (HalyardControlChunk chunk in HalyardControlChunkCodec.ReadAll(copy))
            {
                switch (chunk.Type)
                {
                    case HalyardControlChunkType.Hello:
                        Hellos++;
                        _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                            HalyardControlChunkType.Cookie, 0x00,
                            [.. Enumerable.Range(0, 42).Select(i => (byte)(0xE0 + i))]));
                        break;

                    case HalyardControlChunkType.HelloEcho:
                        ushort helloSeq = BinaryPrimitives.ReadUInt16BigEndian(chunk.Body.Span);
                        byte[] accept = new byte[12];
                        BinaryPrimitives.WriteUInt16BigEndian(accept, _sequence);
                        BinaryPrimitives.WriteUInt16BigEndian(accept.AsSpan(2), (ushort)(helloSeq + 1));
                        _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                            HalyardControlChunkType.Accept, 0x30, accept));
                        break;

                    case HalyardControlChunkType.Data:
                        OnData(chunk.Body.Span[2..].ToArray());
                        break;
                }
            }

            return ValueTask.CompletedTask;
        }

        private void OnData(byte[] payload)
        {
            // Discriminate on the request line rather than trying the frame parser first: an HTTP request
            // is long enough that the frame parser will happily read a plausible type and length out of it.
            string text = Encoding.ASCII.GetString(payload);
            bool isHttp = text.StartsWith("GET ", StringComparison.Ordinal)
                          || text.StartsWith("POST ", StringComparison.Ordinal);

            if (!isHttp)
            {
                while (CtrlFrames.Count > 0)
                {
                    Push(CtrlFrames.Dequeue());
                }

                return;
            }

            Requests.Add(text.Split((char)13)[0]);

            Push(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK" + CRLF + "RP-Version: 1.0" + CRLF + "Content-Length: 0" + CRLF + CRLF));

            // ...and tear the connection down, which is what makes the next request need a new one.
            _outbound.Enqueue(HalyardControlChunkCodec.Encode(
                HalyardControlChunkType.Close, 0x00, new byte[8]));
        }

        private void Push(byte[] payload)
        {
            byte[] body = new byte[2 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(body, ++_sequence);
            payload.CopyTo(body, 2);
            _outbound.Enqueue(HalyardControlChunkCodec.Encode(HalyardControlChunkType.Data, 0x30, body));
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_outbound.Count > 0)
            {
                return _outbound.Dequeue();
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException();
        }

        public void Dispose()
        {
        }
    }

    private static (HalyardDatagramSessionControlChannel Channel, ScriptedConsole Console) Build()
    {
        var console = new ScriptedConsole();
        var datagram = new HalyardDatagramControlChannel(console, Peer, OurId, ConsoleId, Quick());
        return (new HalyardDatagramSessionControlChannel(datagram, ownsChannel: true), console);
    }

    [Fact]
    public async Task InitAndCtrl_RunAsSeparateConnectionsOverOneAssociation()
    {
        // The shape the captured client uses, and the reason this class exists: the console closes each chunk
        // connection once it has answered, so a session that reconnects between /sess/init and /sess/ctrl
        // gets a new connection rather than a new socket -- and the prelude is paid for once.
        (HalyardDatagramSessionControlChannel channel, ScriptedConsole console) = Build();
        await using var _ = channel;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await channel.ConnectAsync(Peer, cts.Token);
        SessResponse init = await channel.SendRequestAsync(
            new SessRequest(SessHttpMethod.Get, "/sie/ps5/rp/sess/init"), cts.Token);

        await channel.ConnectAsync(Peer, cts.Token);
        SessResponse ctrl = await channel.SendRequestAsync(
            new SessRequest(SessHttpMethod.Get, "/sie/ps5/rp/sess/ctrl"), cts.Token);

        Assert.True(init.IsSuccess);
        Assert.True(ctrl.IsSuccess);
        Assert.Equal(
            ["GET /sie/ps5/rp/sess/init HTTP/1.1", "GET /sie/ps5/rp/sess/ctrl HTTP/1.1"],
            console.Requests);

        // Two connections, one association: two hellos, and the prelude was never rebuilt.
        Assert.Equal(2, console.Hellos);
    }

    [Fact]
    public async Task ControlFrames_FlowOnTheConnectionThatServedCtrl()
    {
        // After /sess/ctrl the same connection carries the persistent binary channel -- the console's
        // heartbeat lives there, and a session that stops answering it is torn down.
        (HalyardDatagramSessionControlChannel channel, ScriptedConsole console) = Build();
        await using var _ = channel;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await channel.ConnectAsync(Peer, cts.Token);
        console.CtrlFrames.Enqueue(new HalyardCtrlMessage(HalyardCtrlMessage.TypeHeartbeatReq).Serialize());

        await channel.SendCtrlMessageAsync(
            new HalyardCtrlMessage(HalyardCtrlMessage.TypeHeartbeatRep), cts.Token);
        HalyardCtrlMessage? received = await channel.ReadCtrlMessageAsync(cts.Token);

        Assert.NotNull(received);
        Assert.Equal(HalyardCtrlMessage.TypeHeartbeatReq, received!.Value.Type);
    }

    [Fact]
    public async Task SendingBeforeConnecting_SaysSoRatherThanHanging()
    {
        (HalyardDatagramSessionControlChannel channel, ScriptedConsole _) = Build();
        await using var __ = channel;

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SendRequestAsync(
                new SessRequest(SessHttpMethod.Get, "/sie/ps5/rp/sess/init"), CancellationToken.None));

        Assert.Contains("ConnectAsync", ex.Message);
    }
}
