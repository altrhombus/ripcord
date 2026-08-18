using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Ripcord.Core.Net.WebSockets;

/// <summary>
/// The real <see cref="IWebSocketChannel"/> over <see cref="ClientWebSocket"/>.
///
/// <para>
/// Deliberately thin: connect, reassemble text messages, close. The keepalive is the framework's own — setting
/// <see cref="ClientWebSocketOptions.KeepAliveInterval"/> makes <see cref="ClientWebSocket"/> send protocol
/// ping frames on that cadence, which is exactly the ~10 s ping the push service expects, so there is no manual
/// ping loop to get wrong.
/// </para>
/// </summary>
public sealed class ClientWebSocketChannel : IWebSocketChannel
{
    private readonly ClientWebSocket _socket = new();
    private bool _connected;

    public async Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan keepAliveInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(headers);

        foreach ((string name, string value) in headers)
        {
            // Sec-WebSocket-Protocol is not an ordinary request header — ClientWebSocket owns the whole
            // Sec-WebSocket-* handshake and validates the server's echo, so a required subprotocol must be
            // offered through AddSubProtocol, not stuffed into the header collection (which it would reject).
            if (string.Equals(name, "Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
            {
                _socket.Options.AddSubProtocol(value);
            }
            else
            {
                _socket.Options.SetRequestHeader(name, value);
            }
        }

        if (keepAliveInterval > TimeSpan.Zero)
        {
            _socket.Options.KeepAliveInterval = keepAliveInterval;
        }

        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _connected = true;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("ReceiveAsync called before ConnectAsync.");
        }

        // Rented buffer per receive; text messages here are small (a few KB of JSON) but may still span frames.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // An abnormal close (the push service's app-code 4101 teardown lands here) is reported as
                    // end-of-stream rather than thrown — the receive loop above treats null as "closed".
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    // Binary frames are not expected on this channel; if one arrives, skip it rather than
                    // decode bytes as text, and wait for the next message.
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        message.SetLength(0);
                        continue;
                    }

                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A best-effort clean close; the peer may already be gone. Disposal must not throw.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
