using System.Net;
using System.Net.Sockets;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// How a registration request reaches the console.
///
/// <para>
/// Two of these exist because the two pairing routes reach the console differently, and only differently: the
/// PIN route opens TCP 9295 to a console it can already see, while the account route arrives through the cloud
/// rendezvous and the console serves the identical HTTP over UDP 9303 instead. Everything above this seam —
/// the request layout, the body crypto, the response parse — is shared, which is the whole reason it is a seam
/// rather than a second registration client.
/// </para>
///
/// <para>
/// The transport builds the request rather than being handed it, because the <c>HOST</c> header carries the
/// <em>client's own</em> address and only the transport knows what that is. The console validates it as a
/// plain dotted quad and refuses otherwise, which is a 403 that looks exactly like a crypto failure.
/// </para>
/// </summary>
public interface IHalyardRegistrationTransport
{
    /// <summary>
    /// Get the transport ready before the caller finishes negotiating, if it needs to be.
    ///
    /// <para>
    /// Exists for one reason, and it is a protocol fact rather than an optimisation: on the account route the
    /// side that opens the association is the side that may open a connection on it, and the console starts
    /// its own the instant our ACCEPT reaches it. A client that waits until it wants to send a request has
    /// already lost that race. The TCP route has nothing to prepare.
    /// </para>
    /// </summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deliver <paramref name="encryptedBody"/> as a registration request and return the raw HTTP response.
    /// </summary>
    Task<byte[]> ExchangeAsync(
        HalyardRegistrationRequest request, byte[] encryptedBody, CancellationToken cancellationToken);
}

/// <summary>
/// The PIN route's transport: plain HTTP/1.1 over TCP 9295 to a directly-reachable console.
/// </summary>
public sealed class HalyardTcpRegistrationTransport : IHalyardRegistrationTransport
{
    public async Task<byte[]> ExchangeAsync(
        HalyardRegistrationRequest request, byte[] encryptedBody, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(request.ConsoleHost, HalyardRegistrationMessage.Port, cancellationToken)
            .ConfigureAwait(false);

        // The console-confirmed HOST header is the CLIENT's own LAN address, known only after connect. A
        // dual-stack socket reports it in IPv4-mapped-IPv6 form ("::ffff:1.2.3.4"); the console validates
        // HOST as a plain dotted-quad and 403s otherwise, so normalise to IPv4.
        IPAddress? local = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
        if (local is not null && local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        string clientIp = local?.ToString() ?? IPAddress.Loopback.ToString();
        byte[] requestBytes = HalyardRegistrationMessage.BuildRequest(request, clientIp, encryptedBody);

        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
