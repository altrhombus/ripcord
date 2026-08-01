using System.Net;
using System.Security.Cryptography;
using Ripcord.Core.Net.Udp;

namespace Ripcord.Protocol.Halyard.Takion;

/// <summary>
/// Drives the Takion connection handshake over a UDP socket: sends INIT, waits for INIT_ACK, sends
/// COOKIE_ECHO, and reaches ESTABLISHED on COOKIE_ACK (spec §8). Each phase retransmits its packet on a
/// per-attempt timeout - the minimal reliability the handshake needs; the full DATA/SACK reliable-delivery
/// loop for control messages is built on top of an established connection (still to come).
///
/// The caller owns the <see cref="UdpChannel"/>; this type only sends to / filters on the console endpoint.
/// </summary>
public sealed class TakionConnection
{
    private readonly UdpChannel _channel;
    private readonly IPEndPoint _remote;
    private readonly TakionHandshake _handshake = new();

    public TakionConnection(UdpChannel channel, IPEndPoint remote)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    }

    public uint LocalTag => _handshake.LocalTag;
    public uint RemoteTag => _handshake.RemoteTag;
    public bool IsEstablished => _handshake.State == TakionHandshake.HandshakeState.Established;

    /// <summary>
    /// Perform the active-open handshake. Throws <see cref="TimeoutException"/> if a phase is not answered
    /// within <paramref name="maxAttempts"/> retransmits of <paramref name="perAttemptTimeout"/>.
    /// </summary>
    public async Task ConnectAsync(
        TimeSpan perAttemptTimeout,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        byte[] init = _handshake.BuildInit(RandomNonZeroTag());

        byte[]? cookie = null;
        bool gotInitAck = await ExchangeAsync(init, packet =>
        {
            if (_handshake.TryHandleInitAck(packet, out byte[] c))
            {
                cookie = c;
                return true;
            }
            return false;
        }, perAttemptTimeout, maxAttempts, cancellationToken).ConfigureAwait(false);

        if (!gotInitAck || cookie is null)
        {
            throw new TimeoutException("Takion handshake: no INIT_ACK from the console.");
        }

        byte[] echo = _handshake.BuildCookieEcho(cookie);
        bool established = await ExchangeAsync(echo,
            packet => _handshake.TryComplete(packet),
            perAttemptTimeout, maxAttempts, cancellationToken).ConfigureAwait(false);

        if (!established)
        {
            throw new TimeoutException("Takion handshake: connection not established after COOKIE_ECHO.");
        }
    }

    /// <summary>
    /// Send <paramref name="outgoing"/>, then read datagrams from the console until <paramref name="onResponse"/>
    /// returns true. On a per-attempt timeout, retransmit (up to <paramref name="maxAttempts"/> sends).
    /// </summary>
    private async Task<bool> ExchangeAsync(
        byte[] outgoing,
        Func<byte[], bool> onResponse,
        TimeSpan perAttemptTimeout,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await _channel.SendAsync(outgoing, _remote, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(perAttemptTimeout);
            try
            {
                while (true)
                {
                    var result = await _channel.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (!result.RemoteEndPoint.Equals(_remote))
                    {
                        continue; // ignore datagrams from other peers
                    }

                    if (onResponse(result.Buffer))
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-attempt timeout elapsed: retransmit.
            }
        }

        return false;
    }

    private static uint RandomNonZeroTag()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint tag;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        while (tag == 0);
        return tag;
    }
}
