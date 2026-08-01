using System.Net;
using System.Net.Sockets;

namespace Ripcord.Core.Net.Udp;

/// <summary>
/// Thin async wrapper over a UDP socket: send to a peer, broadcast to the LAN, and receive
/// datagrams. Used for the 9302 SRCH discovery probe and for the media/input stream port.
/// </summary>
public sealed class UdpChannel : IDisposable
{
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
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken cancellationToken)
        => _client.SendAsync(data, remote, cancellationToken);

    public ValueTask<int> SendBroadcastAsync(ReadOnlyMemory<byte> data, int port, CancellationToken cancellationToken)
        => _client.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, port), cancellationToken);

    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => _client.ReceiveAsync(cancellationToken);

    public void Dispose() => _client.Dispose();
}
