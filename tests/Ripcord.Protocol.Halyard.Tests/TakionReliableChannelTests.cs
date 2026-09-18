using System.Net;
using Avstream;
using Google.Protobuf;
using Ripcord.Core.Net.Udp;
using Ripcord.Protocol.Halyard.Takion;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// End-to-end reliable-delivery test over loopback UDP: the client channel sends a control message, a mock
/// console SACKs it (clearing the retransmit queue) and sends a reply DATA, and the client reassembles +
/// delivers the reply. Exercises send, cumulative-ack clearing, receive, SACK generation, and reassembly
/// through the real socket path.
/// </summary>
public class TakionReliableChannelTests
{
    private const uint ClientTag = 0x00001111;
    private const uint ServerTag = 0x00002222;

    [Fact]
    public async Task SendAndReceive_OverLoopback_AcksAndDelivers()
    {
        using var serverSocket = new UdpChannel();
        using var clientSocket = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverSocket.LocalEndPoint.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockConsoleAsync(serverSocket, cts.Token);

        await using var client = new TakionReliableChannel(
            clientSocket, serverEndpoint, ClientTag, ServerTag, retransmitInterval: TimeSpan.FromMilliseconds(150));
        client.Start();

        var request = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload { ClientVersion = 9, SessionKey = "abc" },
        };
        await client.SendMessageAsync(TakionDataChunk.ChannelSession, request, cts.Token);

        ControlMessage reply = await client.ReceiveMessageAsync(cts.Token);
        Assert.Equal(ControlMessage.Types.MessageType.SessionReply, reply.Type);

        // The console SACKed our request, so the retransmit queue drains.
        await WaitForAsync(() => client.UnackedCount == 0, cts.Token, "every DATA chunk to be acknowledged");
        Assert.Equal(0, client.UnackedCount);

        await cts.CancelAsync();
        await SwallowAsync(serverTask);
    }

    [Fact]
    public async Task SackTiming_ProducesARoundTripEstimate()
    {
        using var serverSocket = new UdpChannel();
        using var clientSocket = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverSocket.LocalEndPoint.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockConsoleAsync(serverSocket, cts.Token);

        await using var client = new TakionReliableChannel(
            clientSocket, serverEndpoint, ClientTag, ServerTag, retransmitInterval: TimeSpan.FromSeconds(30));
        client.Start();

        // No sample yet: RTT must read 0 rather than a fabricated value, and the sample count says so
        // unambiguously — "0 ms" alone cannot distinguish a fast link from an unwired metric.
        Assert.Equal(0, client.RoundTripTimeMs);
        Assert.Equal(0, client.RoundTripSampleCount);

        var request = new ControlMessage
        {
            Type = ControlMessage.Types.MessageType.SessionRequest,
            SessionRequestPayload = new SessionRequestPayload { ClientVersion = 9, SessionKey = "abc" },
        };
        await client.SendMessageAsync(TakionDataChunk.ChannelSession, request, cts.Token);

        // Once the SACK lands the estimate is populated. Over loopback it is sub-millisecond, so assert only
        // that a sample was taken and the value is sane — not a specific figure.
        await WaitForAsync(() => client.RoundTripSampleCount > 0, cts.Token, "the first RTT sample");
        Assert.InRange(client.RoundTripTimeMs, 0, 5_000);

        await cts.CancelAsync();
        await SwallowAsync(serverTask);
    }

    [Fact]
    public async Task SubMillisecondRtt_IsNotRoundedAwayToZero()
    {
        // The regression this guards: RoundTripTimeMs used to be an int, so a genuine 0.4 ms wired-LAN RTT
        // reported as "0 ms" — technically true, indistinguishable from "not implemented", and it erased the
        // difference between a wired link and a Wi-Fi one, which is the comparison that matters on a handheld.
        using var serverSocket = new UdpChannel();
        using var clientSocket = new UdpChannel();
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverSocket.LocalEndPoint.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockConsoleAsync(serverSocket, cts.Token);

        await using var client = new TakionReliableChannel(
            clientSocket, serverEndpoint, ClientTag, ServerTag, retransmitInterval: TimeSpan.FromSeconds(30));
        client.Start();

        // Several exchanges, so the EWMA is exercised rather than just the seeding path.
        for (int i = 0; i < 5; i++)
        {
            await client.SendMessageAsync(
                TakionDataChunk.ChannelSession,
                new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.Heartbeat,
                },
                cts.Token);
            await Task.Delay(10, cts.Token);
        }

        await WaitForAsync(() => client.RoundTripSampleCount >= 2, cts.Token, "two RTT samples");

        // THE REGRESSION IS TRUNCATION, so the load-bearing assertion is that the estimate is a positive
        // fraction rather than an integer 0 - which is what an int-typed RoundTripTimeMs produced from a
        // 0.4 ms round trip.
        Assert.True(client.RoundTripTimeMs > 0, "a real sample must produce a positive estimate");

        // A SANITY BOUND, NOT A PERFORMANCE ASSERTION, and it is loose on purpose. This drives real sockets,
        // and how long a loopback round trip takes on a shared CI runner is a fact about the runner; an
        // earlier `< 100` was tight enough that a busy host could cross it, which made a truncation test
        // report a latency failure. What must not happen is a figure so large it could only be a unit or
        // clock-source mistake, and that is all this catches.
        Assert.True(client.RoundTripTimeMs < 5000, $"RTT should be a plausible millisecond figure, got {client.RoundTripTimeMs} ms");

        await cts.CancelAsync();
        await SwallowAsync(serverTask);
    }

    private static async Task RunMockConsoleAsync(UdpChannel server, CancellationToken ct)
    {
        bool replied = false;
        while (!ct.IsCancellationRequested)
        {
            var received = await server.ReceiveAsync(ct).ConfigureAwait(false);
            if (!TakionDataChunk.TryParse(received.Buffer, out var data))
            {
                continue; // ignore the client's SACKs
            }

            var client = received.RemoteEndPoint;
            // Cumulative-ack the client's DATA (its TSN starts at the client tag).
            await server.SendAsync(TakionSackChunk.Build(ClientTag, data.Seq), client, ct).ConfigureAwait(false);

            if (!replied)
            {
                replied = true;
                var reply = new ControlMessage
                {
                    Type = ControlMessage.Types.MessageType.SessionReply,
                    SessionReplyPayload = new SessionReplyPayload { ServerVersion = 9, Token = 1, EncryptedKeyAccepted = true, VersionAccepted = true, SessionKey = "abc" },
                };
                byte[] packet = TakionDataChunk.Build(ClientTag, seq: ServerTag, channel: 0, reply.ToByteArray());
                await server.SendAsync(packet, client, ct).ConfigureAwait(false);
            }
        }
    }

    // THROWS when the condition never arrives, rather than returning as though it had. It used to exit on
    // cancellation and let the caller assert against whatever state existed - so "the console never answered
    // inside ten seconds" surfaced as whichever assertion happened to read an unset field next, naming a
    // value rather than the wait. That is the same misdiagnosis shape as a stale-package check reporting a
    // version mismatch: the guard fired, and pointed somewhere else.
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, string what)
    {
        while (!condition())
        {
            if (ct.IsCancellationRequested)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
