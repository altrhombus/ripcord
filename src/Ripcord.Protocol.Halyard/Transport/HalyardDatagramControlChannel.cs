using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ripcord.Core.Net.Udp;
using Ripcord.Protocol.Halyard.Common.Control;

namespace Ripcord.Protocol.Halyard.Transport;

/// <summary>
/// The datagrams this channel needs to send and receive, as a seam.
///
/// <para>
/// <see cref="UdpChannel"/> is a concrete class over a real socket, so a test could otherwise only exercise
/// this channel by binding a port and talking to itself.
/// </para>
/// </summary>
public interface IHalyardDatagramTransport : IDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);

    /// <summary>The next datagram from the peer. Should throw on timeout rather than returning empty.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>A real UDP socket, addressed at one console endpoint.</summary>
public sealed class HalyardUdpDatagramTransport : IHalyardDatagramTransport
{
    private readonly UdpChannel _channel;
    private readonly IPEndPoint _remote;

    /// <param name="localPort">
    /// 0 for an ephemeral port. The account route advertises whichever port it binds as its signaling
    /// candidate, so the caller usually chooses one and advertises the same value.
    /// </param>
    public HalyardUdpDatagramTransport(IPEndPoint remote, int localPort = 0)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _channel = new UdpChannel(localPort);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        => await _channel.SendAsync(datagram, _remote, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        => (await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false)).Buffer;

    public void Dispose() => _channel.Dispose();
}

/// <summary>Tunables for one account-route control exchange.</summary>
public sealed class HalyardDatagramControlOptions
{
    /// <summary>How long to wait for any single datagram before re-sending or giving up.</summary>
    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to keep trying at each stage. The prelude is lossy by nature — on a WAN path it doubles as the
    /// hole-punch and early probes are expected to be dropped.
    /// </summary>
    public TimeSpan StageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to listen before opening the prelude ourselves. <b>Zero by default: we initiate.</b>
    ///
    /// <para>
    /// A same-LAN capture of the vendor settles this — its client sends the opening Init and the console
    /// answers, and the client then opens the chunk connection. An earlier version of this waited two seconds
    /// first, on the reasoning that the console appeared to initiate on a LAN; it only appeared to because we
    /// opened our socket <em>after</em> sending the ACCEPT the console reacts to. Losing that race put us in
    /// the responder role, and the console then never opened a chunk connection at all.
    /// </para>
    ///
    /// <para>
    /// Kept as a knob rather than deleted because the association handles either role, and a path where the
    /// peer genuinely gets there first is one this transport should survive.
    /// </para>
    /// </summary>
    public TimeSpan ListenBeforeOpening { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// How the hello we send addresses the console. Defaults to the shape every capture carries.
    ///
    /// <para>
    /// A knob because the alternative is a live experiment, not a preference: the console has ignored every
    /// <see cref="HalyardControlAddressing.PortPair"/> hello Ripcord has sent, in silence, which is what its
    /// library does on a port-lookup miss. <see cref="HalyardControlAddressing.PeerAddressOnly"/> is matched
    /// on the peer address instead and so reaches a different lookup.
    /// </para>
    /// </summary>
    public HalyardControlAddressing HelloAddressing { get; init; } = HalyardControlAddressing.PortPair;

    /// <summary>Optional progress sink, for the harness.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Drives a <see cref="HalyardControlAssociation"/> over a real socket.
///
/// <para>
/// <b>This class holds no protocol knowledge.</b> It pumps datagrams in, puts whatever the association returns
/// on the wire, and waits for the events a caller asked for. Every decision about what the protocol does next
/// lives in the association, which has no I/O and can therefore be driven from a capture in either role.
/// </para>
///
/// <para>
/// That split is the point. The first version of this transport interleaved protocol decisions with socket
/// awaits, and each new observation about the wire — that the console may open the prelude, that both sides
/// echo — meant restructuring its control flow and another run against hardware to find the next one.
/// </para>
/// </summary>
public sealed class HalyardDatagramControlChannel : IAsyncDisposable
{
    private readonly IHalyardDatagramTransport _transport;
    private readonly HalyardControlAssociation _association;
    private readonly HalyardDatagramControlOptions _options;

    /// <param name="peer">
    /// The console endpoint this channel sends to. Needed as <em>data</em>, not read back off the socket: the
    /// prelude's echo reflects the peer's own address and port to it, and a channel that cannot name them
    /// sends an echo the console cannot validate the path from.
    /// </param>
    public HalyardDatagramControlChannel(
        IHalyardDatagramTransport transport,
        IPEndPoint peer,
        ReadOnlyMemory<byte> localHashedId,
        ReadOnlyMemory<byte> consoleHashedId,
        HalyardDatagramControlOptions? options = null,
        Func<int, byte[]>? random = null)
    {
        ArgumentNullException.ThrowIfNull(peer);

        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new HalyardDatagramControlOptions();
        _association = new HalyardControlAssociation(
            localHashedId,
            consoleHashedId,
            peer.Address.MapToIPv4().GetAddressBytes(),
            (ushort)peer.Port,
            random ?? RandomNumberGenerator.GetBytes);
    }

    /// <summary>How far the association has got — for a caller that wants to report progress.</summary>
    public HalyardControlPhase Phase => _association.Phase;

    /// <summary>
    /// Establish the association, open a connection, send <paramref name="httpRequest"/> and return the reply.
    /// </summary>
    public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> httpRequest, CancellationToken cancellationToken)
    {
        await EstablishAsync(cancellationToken).ConfigureAwait(false);
        await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ApplyAsync(_association.Send(httpRequest), cancellationToken).ConfigureAwait(false);
        Log($"request sent ({httpRequest.Length} bytes of HTTP)");

        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Put our opening Init on the wire and return, without waiting for an answer.
    ///
    /// <para>
    /// Separate from <see cref="EstablishAsync"/> because of an ordering the protocol forces: the console
    /// cannot answer a prelude until signaling has told it our candidate, and it opens an association of its
    /// own the moment our ACCEPT arrives. So the opening Init has to go out <em>between</em> those two — after
    /// our OFFER, before our ACCEPT — and blocking there would stall the very signaling that makes an answer
    /// possible.
    /// </para>
    /// </summary>
    public Task BeginAsync(CancellationToken cancellationToken)
    {
        return ApplyAsync(_association.Open(), cancellationToken);
    }

    /// <summary>
    /// Complete the prelude, in whichever role the peer leaves us.
    ///
    /// <para>
    /// We open it ourselves <em>and</em> answer anything the peer opens, because either may happen and which
    /// one does depends on the path: on a WAN path the client must punch out first, and on a shared LAN the
    /// console opens it as soon as signaling has handed it our candidate.
    /// </para>
    /// </summary>
    public async Task EstablishAsync(CancellationToken cancellationToken)
    {
        if (_options.ListenBeforeOpening <= TimeSpan.Zero
            || !await ListenForPeerAsync(cancellationToken).ConfigureAwait(false))
        {
            await ApplyAsync(_association.Open(), cancellationToken).ConfigureAwait(false);
        }

        await PumpUntilAsync(
            () => _association.Phase is not (HalyardControlPhase.Idle or HalyardControlPhase.Handshaking),
            onQuiet: () => _association.Retry(),
            "The console did not complete the control prelude. On a LAN this normally means 9303 is "
            + "unreachable; the account route also requires that the candidate exchange has completed, since "
            + "the prelude names both peers by their signaling id.",
            cancellationToken).ConfigureAwait(false);

        Log("prelude established");
    }

    /// <summary>
    /// Wait briefly for the peer to open the association. Returns true when something arrived and was fed to
    /// the state machine — which, for an Init, makes us the responder.
    /// </summary>
    private async Task<bool> ListenForPeerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            listen.CancelAfter(_options.ListenBeforeOpening);

            ReadOnlyMemory<byte> datagram = await _transport.ReceiveAsync(listen.Token).ConfigureAwait(false);
            Log("the console opened the prelude; answering");
            await ApplyAsync(_association.OnDatagram(datagram.Span), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>Open a chunk-layer connection — or accept the one the peer opens.</summary>
    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(
            _association.OpenConnection(_options.HelloAddressing), cancellationToken).ConfigureAwait(false);
        Log($"hello sent, addressed {_options.HelloAddressing}");

        // Re-sent while we wait. In the captured LAN pairing the client's first hello goes unanswered and its
        // second, about a second later, is the one the console replies to — so a single attempt is not a
        // question the console has declined to answer, it is one it never heard.
        await PumpUntilAsync(
            () => _association.Phase is not HalyardControlPhase.Established,
            onQuiet: () => _association.ReopenConnection(),
            "The console did not open a control connection.",
            cancellationToken).ConfigureAwait(false);

        if (_association.Phase == HalyardControlPhase.Closed)
        {
            throw new InvalidDataException("The console closed the association before a connection was open.");
        }

        Log("connection open");
    }

    private async Task<byte[]> ReadResponseAsync(CancellationToken cancellationToken)
    {
        byte[]? complete = null;

        await PumpUntilAsync(
            () => complete is not null || _association.Phase == HalyardControlPhase.Closed,
            onQuiet: null,
            "The console did not answer the request.",
            cancellationToken,
            raised =>
            {
                if (raised is HalyardControlEvent.DataReceived data && IsCompleteHttpMessage(data.Payload))
                {
                    complete = data.Payload;
                }
            }).ConfigureAwait(false);

        if (complete is null)
        {
            throw new InvalidDataException("The console closed the connection before answering.");
        }

        Log($"response complete ({complete.Length} bytes)");
        _association.ClearInbound();
        return complete;
    }

    /// <summary>
    /// Pump datagrams until <paramref name="done"/>, re-sending via <paramref name="onQuiet"/> when nothing
    /// arrives within one receive timeout.
    /// </summary>
    private async Task PumpUntilAsync(
        Func<bool> done,
        Func<HalyardControlAction>? onQuiet,
        string timeoutMessage,
        CancellationToken cancellationToken,
        Action<HalyardControlEvent>? observe = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StageTimeout);

        while (!done())
        {
            if (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(timeoutMessage);
            }

            ReadOnlyMemory<byte> datagram;
            try
            {
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                receive.CancelAfter(_options.ReceiveTimeout);
                datagram = await _transport.ReceiveAsync(receive.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Quiet for one receive window. Normal during a hole-punch, so re-send and keep waiting until
                // the stage deadline says otherwise.
                if (onQuiet is not null)
                {
                    await ApplyAsync(onQuiet(), cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            HalyardControlAction action = _association.OnDatagram(datagram.Span);

            foreach (HalyardControlEvent raised in action.Events)
            {
                // Named, always. A silent stall on this transport is what cost the first live runs their
                // diagnosis; an association that reports what it ignored turns one into evidence.
                Log(raised is HalyardControlEvent.Unhandled unhandled
                    ? $"ignored a datagram: {unhandled.Reason} ({unhandled.Datagram.Length} bytes)"
                    : raised.GetType().Name);

                observe?.Invoke(raised);
            }

            await ApplyAsync(action, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(HalyardControlAction action, CancellationToken cancellationToken)
    {
        foreach (byte[] datagram in action.Send)
        {
            await _transport.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether an HTTP message is complete, judged by <c>Content-Length</c> against what follows the header
    /// terminator — a datagram boundary says nothing about a message boundary.
    /// </summary>
    private static bool IsCompleteHttpMessage(ReadOnlySpan<byte> message)
    {
        string text = Encoding.ASCII.GetString(message);
        int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return false;
        }

        int bodyLength = message.Length - (headerEnd + 4);
        foreach (string line in text[..headerEnd].Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0
                && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(colon + 1)..].Trim(), out int declared))
            {
                return bodyLength >= declared;
            }
        }

        return true;
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
