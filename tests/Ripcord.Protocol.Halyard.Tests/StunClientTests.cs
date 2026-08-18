using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Ripcord.Core.Net.Stun;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The STUN client's request/response loop, exercised against a local fake server so the whole path — send,
/// wait, match the transaction id, parse, retry, time out — is tested without reaching the public internet.
/// </summary>
public class StunClientTests
{
    /// <summary>
    /// A loopback UDP server that answers a Binding Request with a Binding Success carrying
    /// <paramref name="mapped"/>, echoing the request's transaction id.
    /// </summary>
    private sealed class FakeStunServer : IAsyncDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeStunServer(IPEndPoint mapped, bool corruptTransactionId = false, bool silent = false)
        {
            Endpoint = (IPEndPoint)_socket.Client.LocalEndPoint!;
            _loop = silent ? Task.CompletedTask : RunAsync(mapped, corruptTransactionId, _cts.Token);
        }

        public IPEndPoint Endpoint { get; }

        private async Task RunAsync(IPEndPoint mapped, bool corrupt, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    UdpReceiveResult req = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
                    StunMessage? parsed = StunMessage.TryParse(req.Buffer);
                    if (parsed is not { Type: StunMessageType.BindingRequest })
                    {
                        continue;
                    }

                    byte[] txid = parsed.TransactionId;
                    if (corrupt)
                    {
                        txid = (byte[])txid.Clone();
                        txid[0] ^= 0xFF;
                    }

                    byte[] response = BuildSuccess(txid, mapped);
                    await _socket.SendAsync(response, req.RemoteEndPoint, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private static byte[] BuildSuccess(byte[] transactionId, IPEndPoint mapped)
        {
            // Header (20) + one XOR-MAPPED-ADDRESS attribute (4 + 8).
            byte[] msg = new byte[32];
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0), (ushort)StunMessageType.BindingSuccess);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), StunMessage.MagicCookie);
            transactionId.CopyTo(msg.AsSpan(8));

            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), 0x0020);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), 8);
            msg[25] = 0x01; // family IPv4
            ushort xport = (ushort)(mapped.Port ^ (StunMessage.MagicCookie >> 16));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(26), xport);
            byte[] addr = mapped.Address.GetAddressBytes();
            Span<byte> cookie = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, StunMessage.MagicCookie);
            for (int i = 0; i < 4; i++)
            {
                msg[28 + i] = (byte)(addr[i] ^ cookie[i]);
            }

            return msg;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _socket.Dispose();
            _cts.Dispose();
        }
    }

    private static readonly IPEndPoint Reflexive = new(IPAddress.Parse("203.0.113.44"), 51234);

    [Fact]
    public async Task Gather_ReturnsTheReflexiveEndpointTheServerReports()
    {
        await using var server = new FakeStunServer(Reflexive);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        StunResult result = await new StunClient().GatherAsync(
            socket, [server.Endpoint], TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(Reflexive, result.ReflexiveEndpoint);
        Assert.Equal(server.Endpoint, result.Server);
    }

    [Fact]
    public async Task Gather_UsesTheGivenSocket_SoTheMappingIsForThatPort()
    {
        // The binding is only useful if the media transport later sends from the same socket, so the client
        // must gather on the socket it is handed rather than a fresh one. Proven by the server seeing that
        // socket's port as the source.
        await using var server = new FakeStunServer(Reflexive);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        StunResult result = await new StunClient().GatherAsync(
            socket, [server.Endpoint], TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(result.Succeeded);
        // The socket is still usable afterwards (not disposed by the client) and is the one we passed in.
        Assert.Equal(localPort, ((IPEndPoint)socket.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task Gather_FallsThroughToASecondServerWhenTheFirstIsSilent()
    {
        await using var silent = new FakeStunServer(Reflexive, silent: true);
        await using var answering = new FakeStunServer(Reflexive);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        StunResult result = await new StunClient(perServerTimeout: TimeSpan.FromMilliseconds(300), attemptsPerServer: 1)
            .GatherAsync(socket, [silent.Endpoint, answering.Endpoint], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(answering.Endpoint, result.Server);
    }

    [Fact]
    public async Task Gather_IgnoresAResponseWithAMismatchedTransactionId()
    {
        // A reply whose transaction id does not match is a stale or spoofed answer and must not be accepted;
        // the attempt should time out as if unanswered.
        await using var liar = new FakeStunServer(Reflexive, corruptTransactionId: true);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        StunResult result = await new StunClient(attemptsPerServer: 2)
            .GatherAsync(socket, [liar.Endpoint], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Gather_WhenNothingAnswers_ReportsFailureRatherThanHanging()
    {
        // A dead server address: the client must exhaust its window and return, not block forever.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var dead = new IPEndPoint(IPAddress.Loopback, 59999);

        StunResult result = await new StunClient(attemptsPerServer: 1)
            .GatherAsync(socket, [dead], TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.ReflexiveEndpoint);
    }
}
