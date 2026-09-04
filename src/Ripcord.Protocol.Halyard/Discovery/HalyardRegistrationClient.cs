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
/// <param name="transport">
/// How the request reaches the console. Defaults to the PIN route's TCP 9295; the account route supplies the
/// UDP 9303 transport instead. Everything else in this class is identical either way — see
/// <see cref="IHalyardRegistrationTransport"/>.
/// </param>
public sealed class HalyardRegistrationClient(
    IHalyardRegistrationCipher cipher,
    IHalyardRegistrationTransport? transport = null) : IHalyardRegistration
{
    private readonly IHalyardRegistrationCipher _cipher = cipher;
    private readonly IHalyardRegistrationTransport _transport = transport ?? new HalyardTcpRegistrationTransport();

    public Task PrepareAsync(CancellationToken cancellationToken)
        => _transport.PrepareAsync(cancellationToken);

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
        HalyardRegistrationExchange exchange = request.AccountSeed.Length == 16
            ? _cipher.BuildAccountRequest(request.AccountSeed.Span, fieldPlain)   // account ("web") route
            : _cipher.BuildRequest(request.Passcode, fieldPlain);                 // PIN route

        byte[] response;
        try
        {
            response = await _transport
                .ExchangeAsync(request, exchange.RequestBody, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return Fail($"Could not reach the console for registration: {ex.Message}");
        }

        if (!HalyardRegistrationMessage.TrySplitResponse(
                response, out int status, out byte[] respBody, out string? reason))
        {
            // The console's own application reason when it gives one — see TrySplitResponse. Without it every
            // rejection reads identically, which is how a wrong-transport 403 spent a session looking like a
            // wrong-key failure.
            return Fail(reason is null
                ? $"Registration was rejected by the console (HTTP {status})."
                : $"Registration was rejected by the console (HTTP {status}, RP-Application-Reason {reason}).");
        }

        byte[] decrypted = _cipher.DecryptResponse(exchange, respBody);
        if (!HalyardRegistrationMessage.TryParsePairingRecord(decrypted, out HalyardPairingRecord? record))
            return Fail("Registration response did not contain a valid pairing record.");

        return new HalyardRegistrationResult(true, null, record);
    }


    private static HalyardRegistrationResult Fail(string reason) => new(false, reason, null);
}
