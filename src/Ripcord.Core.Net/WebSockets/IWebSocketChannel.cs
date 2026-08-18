namespace Ripcord.Core.Net.WebSockets;

/// <summary>
/// A text-message WebSocket, reduced to what a push/notification consumer needs: connect with headers, receive
/// whole text messages one at a time, close.
///
/// <para>
/// A seam, not because WebSockets have many implementations worth swapping, but because the code that decides
/// <em>what a received message means</em> should be testable without a live server. A fake that yields a
/// scripted sequence of frames exercises the whole dispatch/keepalive/reconnect logic on the desk; the real
/// adapter over <see cref="System.Net.WebSockets.ClientWebSocket"/> is then just the I/O.
/// </para>
/// </summary>
public interface IWebSocketChannel : IAsyncDisposable
{
    /// <summary>
    /// Open the connection. <paramref name="headers"/> are sent on the upgrade request (e.g. authorization),
    /// and <paramref name="keepAliveInterval"/> sets how often a protocol-level ping is sent to hold the
    /// connection open — the push service drops a client that goes quiet.
    /// </summary>
    Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan keepAliveInterval,
        CancellationToken cancellationToken);

    /// <summary>
    /// The next complete text message, or null when the peer has closed the connection. A message split across
    /// several WebSocket frames is reassembled before it is returned, so a caller never sees a partial one.
    /// </summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}
