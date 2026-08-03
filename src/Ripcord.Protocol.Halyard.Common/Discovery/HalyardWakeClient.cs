using System.Net;
using System.Text;
using Ripcord.Core.Net.Udp;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Discovery;

/// <summary>
/// Wakes a console from rest mode over the LAN, with no cloud involvement. Derived from our own capture
/// <c>cap49</c> (see docs/protocol/ps5-local-discovery.md), taken with the console's internet access blocked
/// at the router — proving the wake is a purely local exchange.
///
/// <para>
/// A <c>WAKEUP * HTTP/1.1</c> datagram is sent to the same UDP port as discovery (<see cref="DiscoveryPort"/>,
/// 9302), NOT the <c>host-request-port</c> the standby reply advertises — the advertised port is not where the
/// wake goes at protocol version <c>00030010</c>. The only per-console value is the credential, which is the
/// registration key we already store (<see cref="HalyardPairingRecord.WakeCredential"/>).
/// </para>
///
/// <para>
/// Fire-and-forget by nature: the console does not acknowledge the WAKEUP itself. Readiness is observed by
/// following up with <see cref="HalyardSearchClient"/> — a sleeping console answers SRCH with
/// <c>620 Server Standby</c> and an awake one with <c>200 Ok</c> — which is the caller's job, not this
/// client's.
/// </para>
/// </summary>
public sealed class HalyardWakeClient
{
    /// <summary>The wake datagram goes to the discovery port, confirmed in cap49.</summary>
    public const int DiscoveryPort = HalyardSearchClient.DiscoveryPort;

    /// <summary>
    /// The source port the vendor sends SRCH and WAKEUP from (cap49). A sleeping console may only honour a
    /// wake that originates here, so we match it. Best-effort: if the port is already bound, we fall back to
    /// an ephemeral one rather than fail the wake.
    /// </summary>
    public const int SourcePort = 9303;

    /// <summary>
    /// Build the exact WAKEUP payload for a console. Kept public and pure so it can be asserted against the
    /// captured bytes in a test without opening a socket.
    ///
    /// <para><b>On-wire fidelity matters here.</b> The fields, their order, and the value tokens are copied
    /// verbatim from cap49, including the lowercase single-letter values (<c>client-type:vr</c>, <c>model:w</c>,
    /// etc.) whose meaning we have not separately derived and therefore do not editorialise. Lines are
    /// <b>LF-terminated, not CRLF</b> — the vendor's WAKEUP uses bare newlines even though its SRCH uses CRLF,
    /// and a console keyed to the exact bytes may ignore a "corrected" version.</para>
    /// </summary>
    public static byte[] BuildWakePayload(string credential)
    {
        // \n deliberately, per the capture. Do not "fix" to \r\n.
        string payload =
            "WAKEUP * HTTP/1.1\n" +
            "client-type:vr\n" +
            "auth-type:R\n" +
            "model:w\n" +
            "app-type:r\n" +
            $"user-credential:{credential}\n" +
            $"device-discovery-protocol-version:{HalyardSearchClient.ProtocolVersion}\n";
        return Encoding.ASCII.GetBytes(payload);
    }

    /// <summary>
    /// Send a single WAKEUP to <paramref name="consoleAddress"/> using the credential from
    /// <paramref name="record"/>. One datagram; the console does not reply to the WAKEUP itself, so this
    /// returns once the send completes. The caller polls discovery to learn when the console is actually up.
    /// </summary>
    public async Task WakeAsync(IPAddress consoleAddress, HalyardPairingRecord record, CancellationToken cancellationToken)
    {
        byte[] payload = BuildWakePayload(record.WakeCredential());
        var endpoint = new IPEndPoint(consoleAddress, DiscoveryPort);

        // Bind the vendor's source port when we can; fall back to ephemeral if it is taken, since a wake from
        // any port is better than none.
        using UdpChannel udp = OpenWakeSocket();
        await udp.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private static UdpChannel OpenWakeSocket()
    {
        try
        {
            return new UdpChannel(SourcePort, enableBroadcast: false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return new UdpChannel(localPort: 0, enableBroadcast: false);
        }
    }
}
