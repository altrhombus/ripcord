using System.Net;
using System.Net.Sockets;

namespace Ripcord.Core.Net.Udp;

/// <summary>
/// Thin async wrapper over a UDP socket: send to a peer, broadcast to the LAN, and receive
/// datagrams. Used for the 9302 SRCH discovery probe and for the media/input stream port.
/// </summary>
public sealed class UdpChannel : IDisposable
{
    // SIO_UDP_CONNRESET: whether an ICMP "port unreachable" for something this socket sent fails its next receive.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly UdpClient _client;

    /// <param name="receiveBufferBytes">
    /// SO_RCVBUF for the socket. The OS default (~64 KB) is far too small for a high-bitrate A/V stream: a
    /// single receive-loop stall (GC pause, a slow decode dispatch) lets the kernel buffer overflow and drop
    /// datagrams, which shows up as unrecoverable video-slice corruption and choppy audio. A few MB absorbs
    /// those bursts. Ignored when 0 (leaves the OS default) — discovery/probe sockets don't need it.
    /// </param>
    public UdpChannel(int localPort = 0, bool enableBroadcast = false, int receiveBufferBytes = 0)
    {
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, localPort)) { EnableBroadcast = enableBroadcast };
        if (receiveBufferBytes > 0)
        {
            _client.Client.ReceiveBufferSize = receiveBufferBytes;
        }

        // Off, on Windows. Left on, one ICMP "port unreachable" (a probe to a port the console has closed, a
        // datagram that outlived the far end's socket) makes the next receive throw ConnectionReset, and every
        // receive loop here ends on an exception: the session went with it, silently (the 2026-09-30 review). A
        // datagram socket has no connection to reset, so there is nothing this error could tell us.
        if (OperatingSystem.IsWindows())
        {
            _client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken cancellationToken)
        => _client.SendAsync(data, remote, cancellationToken);

    public ValueTask<int> SendBroadcastAsync(ReadOnlyMemory<byte> data, int port, CancellationToken cancellationToken)
        => _client.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, port), cancellationToken);

    /// <summary>
    /// The next datagram. A connection reset is not one, so it is skipped rather than thrown: the IOControl above
    /// already prevents it on Windows, and this keeps every receive loop alive if a platform reports one anyway.
    /// </summary>
    public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // An ICMP report about an earlier send. Nothing arrived; wait for what does.
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
