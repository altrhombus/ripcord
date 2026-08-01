using System.Net;
using System.Net.Sockets;

namespace Ripcord.Core.Net.Tcp;

/// <summary>
/// Async byte-stream channel over TCP, used for the LAN-direct control channel (the plain-TCP
/// transport observed for directly-reachable consoles, docs/protocol/ps5-local-discovery.md). The
/// caller layers the /sess request/response and RPCS framing on top.
/// </summary>
public sealed class TcpByteChannel : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private TcpByteChannel(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<TcpByteChannel> ConnectAsync(IPEndPoint remote, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
        return new TcpByteChannel(client);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => _stream.WriteAsync(data, cancellationToken);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _stream.ReadAsync(buffer, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
