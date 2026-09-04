using Ripcord.Core.Net.WebSockets;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// The required values on the push WebSocket upgrade, captured verbatim from a vendor handshake (cap68). These
/// are interoperability facts — the front-end 400s an upgrade that omits the subprotocol, and expects the
/// <c>X-PSN-*</c> set — not naming choices, so they stay as literal protocol constants.
/// </summary>
public static class HalyardPushHeaders
{
    /// <summary>The subprotocol the push front-end requires; omitting it is a 400.</summary>
    public const string SubProtocol = "np-pushpacket";

    public const string UserAgent = "WebSocket++/0.8.2";

    public const string AppType = "REMOTE_PLAY";

    public const string AppVersion = "RemotePlay/1.0";

    public const string OsVersion = "Windows/10.0";

    public const string ProtocolVersion = "2.1";

    public const string KeepAliveStatusType = "3";

    /// <summary>False for the initial connect; a reconnect after a drop would set this true.</summary>
    public const string Reconnection = "false";
}

/// <summary>
/// The persistent push connection: the WebSocket the console's signaling arrives on.
///
/// <para>
/// This is the inbound half of the rendezvous. The client POSTs its OFFER through the session-manager REST
/// surface (<see cref="HalyardCloudClient.SendOfferAsync"/>); the console's OFFER — carrying the candidates the
/// client cannot otherwise learn — comes back here, wrapped in a session-manager push notification. Every text
/// frame is run through <see cref="HalyardSignalingMessage.TryParse"/>; the ones that are signaling are
/// surfaced, everything else (presence, membership, customData) is dropped.
/// </para>
///
/// <para>
/// The socket is injected as <see cref="IWebSocketChannel"/> so the dispatch loop is testable against captured
/// frames without a live server; the front end passes a <see cref="ClientWebSocketChannel"/>.
/// </para>
/// </summary>
public sealed class HalyardPushChannel(IWebSocketChannel socket) : IAsyncDisposable
{
    private readonly IWebSocketChannel _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the WebSocket upgrade has succeeded (or faults if it fails). A caller that must not act
    /// until the push connection exists — creating the cloud session, which binds its message channel to the
    /// account's live push connection — awaits this before proceeding. Without it, the session is created
    /// before the connection exists and its <c>sessionMessage</c> sub-resource 404s.
    /// </summary>
    public Task Connected => _connected.Task;

    /// <summary>
    /// Raised on the receive loop for every signaling message — OFFER, ACCEPT, RESULT, from either peer.
    /// Consumers filter with <see cref="HalyardSignalingMessage.IsConsoleOffer"/> for the candidates that
    /// matter. Handlers must be quick and not throw; an exception from one is swallowed so a bad consumer
    /// cannot kill the connection.
    /// </summary>
    public event Action<HalyardSignalingMessage>? SignalingReceived;

    /// <summary>
    /// Raised on the receive loop with the raw (double-base64) <c>customData1</c> value whenever the console
    /// publishes one — the account ("web"/no-PIN) registration seed, field-encrypted with the connect
    /// command's <c>data1</c>/<c>data2</c>. The protocol layer decrypts it to the seed (this layer holds no
    /// crypto). Same non-throwing contract as <see cref="SignalingReceived"/>.
    /// </summary>
    public event Action<string>? CustomData1Received;

    /// <summary>
    /// Raised when the console joins the session — the first moment a client may message it, and the moment
    /// before which it has not yet started its own control association.
    /// </summary>
    public event Action? ConsoleJoined;

    /// <summary>Raised for every text frame before parsing, for diagnostics/capture. Optional.</summary>
    public event Action<string>? FrameReceived;

    /// <summary>
    /// Connect and pump frames until the connection closes or <paramref name="cancellationToken"/> fires.
    /// Returns when the peer closes (a null receive) — normal at session teardown — so a caller can await the
    /// channel's lifetime directly.
    /// </summary>
    /// <param name="server">The push server from <see cref="HalyardCloudClient.GetPushServerAsync"/>.</param>
    /// <param name="accessToken">
    /// The account access token, sent as <c>Authorization: Bearer</c> on the upgrade — confirmed from a captured
    /// vendor handshake (cap68).
    /// </param>
    public async Task RunAsync(HalyardPushServerInfo server, string accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        // The upgrade request, matched field-for-field to a captured vendor handshake. The subprotocol and the
        // X-PSN-* set are what the push front-end requires — omitting the subprotocol was a 400. Values are
        // required on-wire protocol constants (see HalyardPushHeaders), not naming choices. Sec-WebSocket-Key/
        // Version/Upgrade/Connection are ClientWebSocket's own and must NOT be set here.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Sec-WebSocket-Protocol"] = HalyardPushHeaders.SubProtocol,
            ["User-Agent"] = HalyardPushHeaders.UserAgent,
            ["X-PSN-APP-TYPE"] = HalyardPushHeaders.AppType,
            ["X-PSN-APP-VER"] = HalyardPushHeaders.AppVersion,
            ["X-PSN-OS-VER"] = HalyardPushHeaders.OsVersion,
            ["X-PSN-PROTOCOL-VERSION"] = HalyardPushHeaders.ProtocolVersion,
            ["X-PSN-KEEP-ALIVE-STATUS-TYPE"] = HalyardPushHeaders.KeepAliveStatusType,
            ["X-PSN-RECONNECTION"] = HalyardPushHeaders.Reconnection,
        };

        try
        {
            await _socket.ConnectAsync(server.PushUri, headers, server.ClientKeepAlive, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Surface a failed upgrade to anyone awaiting Connected, not just to this call's awaiter.
            _connected.TrySetException(ex);
            throw;
        }

        _connected.TrySetResult();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? frame = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break; // peer closed
            }

            Dispatch(frame);
        }
    }

    private void Dispatch(string frame)
    {
        Raise(FrameReceived, frame);

        HalyardSignalingMessage? message = HalyardSignalingMessage.TryParse(frame);
        if (message is not null)
        {
            Raise(SignalingReceived, message);
            return; // a frame is signaling or customData, never both
        }

        if (HalyardCustomDataNotification.TryParseCustomData1(frame) is { } customData1)
        {
            Raise(CustomData1Received, customData1);
            return;
        }

        if (HalyardCustomDataNotification.IsConsoleJoined(frame))
        {
            Raise(ConsoleJoined);
        }
    }

    /// <summary>
    /// Invoke a handler without letting its exception escape the receive loop. A consumer that throws is a
    /// consumer bug; it must not take the push connection — and with it the whole connect attempt — down.
    /// </summary>
    private static void Raise(Action? handler)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception)
        {
            // Deliberately swallowed; see the summary.
        }
    }

    private static void Raise<T>(Action<T>? handler, T argument)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(argument);
        }
        catch (Exception)
        {
            // Deliberately swallowed; see the summary.
        }
    }

    public ValueTask DisposeAsync() => _socket.DisposeAsync();
}
