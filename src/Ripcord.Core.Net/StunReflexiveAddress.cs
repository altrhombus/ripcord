using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Ripcord.Core.Net;

/// <summary>
/// Discovers this machine's reflexive (public) address for a given local UDP port, by asking a STUN server.
///
/// <para>
/// A peer behind NAT cannot be reached at its own address: what the far side must be told is the address the
/// NAT presents on its behalf, which only something outside the NAT can observe. That is all this does —
/// one RFC 5389 Binding Request, one <c>XOR-MAPPED-ADDRESS</c> back.
/// </para>
///
/// <para>
/// <b>The port matters more than the address.</b> A NAT maps per source port, so the answer is only useful
/// for the port it was asked about — which is why this binds the caller's port rather than picking its own.
/// The mapping is what the peer will send to, so the same port must then carry the real traffic.
/// </para>
///
/// <para>
/// Which server is asked is an implementation choice and nothing to do with any console protocol: these are
/// ordinary public STUN servers, tried in order because any one of them can be down. There is no
/// authentication and nothing session-specific is sent — a Binding Request carries a random transaction id
/// and nothing else.
/// </para>
/// </summary>
/// <summary>
/// A reflexive address, and what the NAT's behaviour was measured to be.
/// </summary>
/// <param name="Reflexive">The address a peer must be told about.</param>
/// <param name="EndpointIndependent">
/// True when two different destinations saw the same mapping, so the address is usable by anyone. False when
/// they did not — a per-destination (symmetric) NAT, where no advertised address can work. Null when only one
/// server answered and there was nothing to compare.
/// </param>
public sealed record StunMapping(IPEndPoint Reflexive, bool? EndpointIndependent);

public static class StunReflexiveAddress
{
    private const int BindingRequest = 0x0001;
    private const int BindingResponse = 0x0101;
    private const uint MagicCookie = 0x2112A442;
    private const int AttrXorMappedAddress = 0x0020;
    private const int HeaderLength = 20;

    /// <summary>
    /// Public STUN servers. <b>Deliberately on different operators</b>, because the classification below turns
    /// on asking two genuinely different addresses: two names belonging to the same provider can resolve to
    /// the same host, and a NAT that maps per destination would then look endpoint-independent when it is not.
    /// </summary>
    private static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
        ("stun1.l.google.com", 19302),
    ];

    /// <summary>
    /// The reflexive endpoint for <paramref name="localPort"/>, or null when no server answered — which is a
    /// normal outcome (a blocked network, a down server) and never an exception, because a caller that cannot
    /// learn its reflexive address can still try everything else.
    /// </summary>
    public static async Task<StunMapping?> DiscoverAsync(
        int localPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));

        IPEndPoint? first = null;
        foreach ((string host, int port) in Servers)
        {
            IPEndPoint? mapped = await TryServerAsync(socket, host, port, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (mapped is null)
            {
                continue;
            }

            if (first is null)
            {
                first = mapped;
                continue;
            }

            // Two answers from two different destinations, on one source port. If the NAT presents the same
            // address and port to both, its mapping does not depend on who is being addressed, and the address
            // we advertise will also be the one a console sees. If they differ, the NAT allocates per
            // destination -- so the mapping a STUN server reports is worthless to anyone else, and no
            // advertised candidate can be reached however correct it looked when it was measured.
            return new StunMapping(first, EndpointIndependent: first.Equals(mapped));
        }

        // One answer, or none. One is still worth having; we just cannot say how the NAT behaves.
        return first is null ? null : new StunMapping(first, EndpointIndependent: null);
    }

    private static async Task<IPEndPoint?> TryServerAsync(
        UdpClient socket, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);
            IPAddress? server = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            return server is null
                ? null
                : await QueryAsync(socket, new IPEndPoint(server, port), timeout, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Try the next server; none of these failures says anything about the others.
            return null;
        }
    }

    private static async Task<IPEndPoint?> QueryAsync(
        UdpClient socket, IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] transactionId = RandomNumberGenerator.GetBytes(12);
        var request = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(request, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // no attributes
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transactionId.CopyTo(request, 8);

        await socket.SendAsync(request, server, cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult result = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (TryParseMapped(result.Buffer, transactionId) is { } mapped)
                {
                    return mapped;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    internal static IPEndPoint? TryParseMapped(ReadOnlySpan<byte> message, ReadOnlySpan<byte> transactionId)
    {
        if (message.Length < HeaderLength
            || BinaryPrimitives.ReadUInt16BigEndian(message) != BindingResponse
            || BinaryPrimitives.ReadUInt32BigEndian(message[4..]) != MagicCookie
            || !message.Slice(8, 12).SequenceEqual(transactionId))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        ReadOnlySpan<byte> attributes = message[HeaderLength..];
        if (length > attributes.Length)
        {
            return null;
        }

        attributes = attributes[..length];
        while (attributes.Length >= 4)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(attributes);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(attributes[2..]);

            // Attributes are padded to a 4-byte boundary; the padding is not counted in the length.
            int padded = (valueLength + 3) & ~3;
            if (attributes.Length < 4 + padded)
            {
                return null;
            }

            ReadOnlySpan<byte> value = attributes.Slice(4, valueLength);
            if (type == AttrXorMappedAddress && valueLength >= 8 && value[1] == 0x01)
            {
                // Both halves are obscured with the magic cookie, so that a NAT rewriting payloads by pattern
                // does not silently corrupt the very address being reported.
                ushort port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (MagicCookie >> 16));
                uint address = BinaryPrimitives.ReadUInt32BigEndian(value[4..]) ^ MagicCookie;

                Span<byte> octets = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(octets, address);
                return new IPEndPoint(new IPAddress(octets), port);
            }

            attributes = attributes[(4 + padded)..];
        }

        return null;
    }
}
