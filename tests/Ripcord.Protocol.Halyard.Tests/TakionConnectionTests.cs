using System.Buffers.Binary;
using System.Net;
using Ripcord.Core.Net.Udp;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// End-to-end handshake test over real loopback UDP: a mock "console" answers INIT with INIT_ACK and
/// COOKIE_ECHO with COOKIE_ACK, and the real <see cref="TakionConnection"/> must reach ESTABLISHED and
/// learn the server tag. Also covers retransmission (the console ignores the first INIT).
/// </summary>
public class TakionConnectionTests
{
    private const uint ServerTag = 0x00b18ccf;

    [Fact]
    public async Task ConnectAsync_CompletesHandshakeOverLoopback()
    {
        using var server = new UdpChannel();
        using var client = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, server.LocalEndPoint.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockConsoleAsync(server, dropFirstInit: false, cts.Token);

        var connection = new TakionConnection(client, serverEndpoint);
        await connection.ConnectAsync(TimeSpan.FromSeconds(1), maxAttempts: 5, cts.Token);

        Assert.True(connection.IsEstablished);
        Assert.Equal(ServerTag, connection.RemoteTag);
        Assert.NotEqual(0u, connection.LocalTag);

        await cts.CancelAsync();
        await SwallowAsync(serverTask);
    }

    [Fact]
    public async Task ConnectAsync_RetransmitsInitWhenFirstIsDropped()
    {
        using var server = new UdpChannel();
        using var client = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, server.LocalEndPoint.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockConsoleAsync(server, dropFirstInit: true, cts.Token);

        var connection = new TakionConnection(client, serverEndpoint);
        await connection.ConnectAsync(TimeSpan.FromMilliseconds(300), maxAttempts: 5, cts.Token);

        Assert.True(connection.IsEstablished);

        await cts.CancelAsync();
        await SwallowAsync(serverTask);
    }

    private static async Task RunMockConsoleAsync(UdpChannel server, bool dropFirstInit, CancellationToken ct)
    {
        bool droppedInit = false;
        uint clientTag = 0;
        while (!ct.IsCancellationRequested)
        {
            var received = await server.ReceiveAsync(ct).ConfigureAwait(false);
            byte[] packet = received.Buffer;
            if (packet.Length < TakionMessageHeader.Length + 1)
            {
                continue;
            }

            var chunkType = (SctpChunkType)packet[TakionMessageHeader.Length];
            var client = received.RemoteEndPoint;

            switch (chunkType)
            {
                case SctpChunkType.Init:
                    if (dropFirstInit && !droppedInit)
                    {
                        droppedInit = true;
                        continue; // force a retransmit
                    }
                    clientTag = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(TakionMessageHeader.Length + 4));
                    await server.SendAsync(BuildInitAck(clientTag), client, ct).ConfigureAwait(false);
                    break;

                case SctpChunkType.CookieEcho:
                    await server.SendAsync(BuildCookieAck(clientTag), client, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static byte[] BuildInitAck(uint clientTag)
    {
        var cookie = new byte[TakionHandshake.CookieLength];
        for (int i = 0; i < cookie.Length; i++) cookie[i] = (byte)i;

        int chunkLength = 4 + 16 + cookie.Length;
        var packet = new byte[TakionMessageHeader.Length + chunkLength];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: clientTag).Write(span);

        var chunk = span[TakionMessageHeader.Length..];
        chunk[0] = (byte)SctpChunkType.InitAck;
        BinaryPrimitives.WriteUInt16BigEndian(chunk[2..], (ushort)chunkLength);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], ServerTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], TakionHandshake.DefaultReceiveWindow);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[12..], TakionHandshake.DefaultOutStreams);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[14..], TakionHandshake.DefaultInStreams);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[16..], ServerTag);
        cookie.CopyTo(chunk[20..]);
        return packet;
    }

    private static byte[] BuildCookieAck(uint clientTag)
    {
        // COOKIE_ACK echoes the client's verification tag (the client filters TryComplete on its own tag).
        var packet = new byte[TakionMessageHeader.Length + 4];
        var span = packet.AsSpan();
        new TakionMessageHeader(TakionMessageHeader.BaseTypeControl, verificationTag: clientTag).Write(span);
        span[TakionMessageHeader.Length] = (byte)SctpChunkType.CookieAck;
        return packet;
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
