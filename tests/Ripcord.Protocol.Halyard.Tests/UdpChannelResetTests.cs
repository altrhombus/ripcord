using System.Net;
using Ripcord.Core.Net.Udp;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The 2026-09-30 review: on Windows, one ICMP "port unreachable" made the next receive on a UDP socket throw
/// ConnectionReset, and every receive loop ended on it, taking the session along silently. Loopback produces the
/// real thing: send to a port nothing is listening on, and Windows reports it back to the sender.
/// </summary>
public class UdpChannelResetTests
{
    [Fact]
    public async Task APortUnreachable_DoesNotEndReceiving()
    {
        using var channel = new UdpChannel();
        var self = new IPEndPoint(IPAddress.Loopback, channel.LocalEndPoint.Port);

        // A port that was open a moment ago and is closed now.
        int closedPort;
        using (var gone = new UdpChannel())
        {
            closedPort = gone.LocalEndPoint.Port;
        }

        await channel.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, closedPort), CancellationToken.None);
        await Task.Delay(100);   // let the ICMP report land

        using var peer = new UdpChannel();
        await peer.SendAsync(new byte[] { 0x42 }, self, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await channel.ReceiveAsync(timeout.Token);

        Assert.Equal(new byte[] { 0x42 }, received.Buffer);
    }
}
