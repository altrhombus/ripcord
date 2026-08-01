using System.Net;
using System.Net.Sockets;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// The real first-time registration flow (spec §2.0): POST the PIN-authenticated registration request to the
/// console over HTTP/1.1 on TCP 9295 and decrypt the pairing record from the reply. The structural HTTP work
/// is <see cref="HalyardRegistrationMessage"/>; the body crypto is the injected
/// <see cref="IHalyardRegistrationCipher"/> (a single transport key K = f(request-context, passcode) protects
/// both directions — see the cipher's remarks).
///
/// <para>
/// Both directions are complete: the client transmits its per-pairing material inside the request context in
/// wrapped form (so the console recovers it and derives the same field IV), then decrypts the
/// console's reply into the pairing record (registkey + companion). See <see cref="IHalyardRegistrationCipher"/>.
/// </para>
///
/// <para>
/// <b>Precondition — the registration search.</b> The console will not honour a registration POST from a client
/// it has not just heard from: the flow begins with a UDP <c>SRC3</c> probe to :9295 (PS5; <c>SRC2</c> for PS4),
/// to which the console replies <c>RES3</c>/<c>RES2</c>. Skipping this yields a byte-perfect POST that is still
/// rejected with the generic <c>RP-Application-Reason: 80108bff</c> (wire-confirmed: the vendor client always
/// searches first). We mirror the vendor's search → brief settle → POST sequence.
/// </para>
/// </summary>
public sealed class HalyardRegistrationClient(IHalyardRegistrationCipher cipher) : IHalyardRegistration
{
    private readonly IHalyardRegistrationCipher _cipher = cipher;

    public async Task<HalyardRegistrationResult> RegisterAsync(
        HalyardRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_cipher.IsAvailable)
            return Fail("Registration cipher is not available (the dirty-room tables are not injected).");

        // The console must first hear a UDP search probe (SRC3/SRC2) or it rejects the POST as 80108bff
        // (and won't have the control TCP listener armed). Shared with the session connect path.
        try
        {
            await HalyardControlSearch.ProbeAsync(
                request.ConsoleHost, request.Platform == HalyardConsolePlatform.Ps5, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (SocketException) { /* best-effort: the SRC probe arms the console even if the reply is lost */ }

        // Build the request: a random context + random material, with the material transmitted in wrapped
        // form (scattered into the context) so the console recovers it and derives the same field IV.
        // The exchange retains the context + material needed to decrypt the response.
        byte[] fieldPlain = HalyardRegistrationMessage.BuildRequestFieldPlaintext(request);
        HalyardRegistrationExchange exchange = _cipher.BuildRequest(request.Passcode, fieldPlain);

        byte[] response;
        try
        {
            response = await SendAsync(request, exchange.RequestBody, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return Fail($"Could not reach the console for registration: {ex.Message}");
        }

        if (!HalyardRegistrationMessage.TrySplitResponse(response, out int status, out byte[] respBody))
            return Fail($"Registration was rejected by the console (HTTP {status}).");

        byte[] decrypted = _cipher.DecryptResponse(exchange, respBody);
        if (!HalyardRegistrationMessage.TryParsePairingRecord(decrypted, out HalyardPairingRecord? record))
            return Fail("Registration response did not contain a valid pairing record.");

        return new HalyardRegistrationResult(true, null, record);
    }

    private static async Task<byte[]> SendAsync(
        HalyardRegistrationRequest request, byte[] body, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(request.ConsoleHost, HalyardRegistrationMessage.Port, cancellationToken).ConfigureAwait(false);
        // The console-confirmed HOST header is the CLIENT's own LAN address, known only after connect. A
        // dual-stack socket reports it in IPv4-mapped-IPv6 form ("::ffff:1.2.3.4"); the console validates
        // HOST as a plain dotted-quad and 403s otherwise, so normalise to IPv4.
        IPAddress? local = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
        if (local is not null && local.IsIPv4MappedToIPv6)
            local = local.MapToIPv4();
        string clientIp = local?.ToString() ?? IPAddress.Loopback.ToString();

        byte[] requestBytes = HalyardRegistrationMessage.BuildRequest(request, clientIp, body);
        await using var stream = client.GetStream();
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    private static HalyardRegistrationResult Fail(string reason) => new(false, reason, null);
}
