using System.Net;
using Ripcord.Protocol.Halyard.Common.Control;


namespace Ripcord.Protocol.Halyard.Transport;

/// <summary>
/// The session control plane over the account route's UDP 9303 transport, rather than LAN TCP 9295.
///
/// <para>
/// <b>Why this exists.</b> A console reached through the account ("web"/no-PIN) route answers
/// <c>/sess/init</c> and <c>/sess/ctrl</c> on the same 9303 association that carried <c>/sess/rgst</c>, and
/// refuses the PIN route's TCP 9295 with a generic 403. So the whole control plane has to ride the datagram
/// transport, and the session must not care which it got.
/// </para>
///
/// <para>
/// <b>Shape.</b> Deliberately the same as <see cref="HalyardTcpControlChannel"/>: a byte pipe plus the two
/// <c>TryParse</c> loops. Everything that knows about HTTP or control-frame framing already lives in
/// <see cref="SessResponse"/> and <see cref="HalyardCtrlMessage"/>, and duplicating that knowledge here to
/// suit a different transport is how two codecs drift apart. The only real difference is what fills the
/// buffer.
/// </para>
///
/// <para>
/// <b>One association, several connections.</b> The console tears down each chunk connection once it has
/// answered, so <see cref="ConnectAsync"/> opens a fresh one over the existing prelude instead of a fresh
/// socket — which is exactly what the captured client does, running <c>rgst</c>, <c>init</c> and <c>ctrl</c>
/// as three connections over one association, then keeping the last for the persistent binary frames.
/// </para>
/// </summary>
public sealed class HalyardDatagramSessionControlChannel : IHalyardControlChannel
{
    private readonly HalyardDatagramControlChannel _channel;
    private readonly bool _ownsChannel;
    private readonly List<byte> _pending = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _connected;

    /// <param name="channel">
    /// The association to run on. Usually one already established by the pairing or rendezvous flow — the
    /// prelude is expensive to build and is the thing this transport is for reusing.
    /// </param>
    /// <param name="ownsChannel">
    /// Whether disposing this disposes <paramref name="channel"/>. False when the caller established the
    /// association and still needs it.
    /// </param>
    public HalyardDatagramSessionControlChannel(
        HalyardDatagramControlChannel channel, bool ownsChannel = false)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _ownsChannel = ownsChannel;
    }

    /// <summary>
    /// Open a chunk connection for the next request.
    ///
    /// <para>
    /// <paramref name="endpoint"/> is ignored: the association is already addressed at the console this
    /// channel was built for, and re-pointing it mid-session is not something the transport can express. The
    /// parameter stays because <see cref="IHalyardControlChannel"/> is shared with the TCP channel, where
    /// reconnecting to a named endpoint is exactly what happens between <c>/sess/init</c> and
    /// <c>/sess/ctrl</c>.
    /// </para>
    /// </summary>
    public async Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        // Anything buffered belonged to the connection that just closed.
        _pending.Clear();

        await _channel.EstablishAsync(cancellationToken).ConfigureAwait(false);
        await _channel.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _connected = true;
    }

    public async Task<SessResponse> SendRequestAsync(SessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();

        await _channel.SendBytesAsync(request.Serialize(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (SessResponse.TryParse(_pending.ToArray(), out SessResponse? response, out int consumed)
                && response is not null)
            {
                _pending.RemoveRange(0, consumed);
                return response;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The console closed the control connection before answering the request.");
            }
        }
    }

    public async Task SendCtrlMessageAsync(HalyardCtrlMessage message, CancellationToken cancellationToken)
    {
        EnsureConnected();

        // The heartbeat replies run on their own task while the session reads, so two senders can otherwise
        // interleave a frame. Same reason the TCP channel takes this lock.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.SendBytesAsync(message.Serialize(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<HalyardCtrlMessage?> ReadCtrlMessageAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();

        while (true)
        {
            if (HalyardCtrlMessage.TryParse(_pending.ToArray(), out HalyardCtrlMessage message, out int consumed))
            {
                _pending.RemoveRange(0, consumed);
                return message;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;   // the peer closed the connection
            }
        }
    }

    /// <summary>Returns false when the peer closed the connection instead of sending anything.</summary>
    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        byte[]? delta = await _channel.ReceiveBytesAsync(cancellationToken).ConfigureAwait(false);
        if (delta is null)
        {
            return false;
        }

        _pending.AddRange(delta);
        return true;
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException(
                "Control channel not connected — call ConnectAsync to open a chunk connection first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Tell the console the session is over. On TCP that is what closing the socket does; on datagrams
        // nothing says it implicitly, and a console left believing a session is live refuses to join any
        // further cloud session until it is rebooted.
        if (_connected)
        {
            using var goodbye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _channel.CloseConnectionAsync(goodbye.Token).ConfigureAwait(false);
            _connected = false;
        }

        _sendLock.Dispose();
        if (_ownsChannel)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
