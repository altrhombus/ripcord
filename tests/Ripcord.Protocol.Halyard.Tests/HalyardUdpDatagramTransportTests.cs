using System.Net;
using System.Net.Sockets;
using Ripcord.Protocol.Halyard.Transport;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account route's datagram transport takes only the console's datagrams. On a hole-punched public port,
/// one from anyone was taken as the console's, so an injected close ended the session (review, 2026-10-05).
/// </summary>
public class HalyardUdpDatagramTransportTests
{
    [Fact]
    public async Task ReceiveAsync_DropsADatagramFromAnotherAddress_AndReturnsTheConsoles()
    {
        // Two loopback addresses, so the stranger differs from the console by address, as on the internet.
        using var console = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var consoleEndpoint = (IPEndPoint)console.Client.LocalEndPoint!;

        int localPort = FreePort();
        using var transport = new HalyardUdpDatagramTransport(consoleEndpoint, localPort);
        var local = new IPEndPoint(IPAddress.Loopback, localPort);

        await stranger.SendAsync(new byte[] { 0xBA, 0xD0 }, local);
        await console.SendAsync(new byte[] { 0x60, 0x0D }, local);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReadOnlyMemory<byte> received = await transport.ReceiveAsync(timeout.Token);

        Assert.Equal(new byte[] { 0x60, 0x0D }, received.ToArray());
    }

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
