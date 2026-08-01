using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// The RP "search" probe that arms the console's control TCP listener on port 9295. The PS5 does not keep
/// 9295 open continuously: it opens it (briefly) in response to a UDP <c>SRC3</c> probe (PS4 uses
/// <c>SRC2</c>), replying <c>RES3</c>/<c>RES2</c>. Both registration and a session connect require this first
/// — connecting cold gets a TCP RST ("connection actively refused"). Wire-confirmed: in a full vendor session
/// one SRC3 armed the listener for the registration POST and the subsequent <c>/sess/init</c> alike.
///
/// <para>Sending the probe is what arms the console, so a lost reply is non-fatal; callers proceed to the TCP
/// connect after a short settle regardless.</para>
/// </summary>
public static class HalyardControlSearch
{
    /// <summary>The control/registration port (probe is UDP here; the session/registration HTTP is TCP here).</summary>
    public const int Port = 9295;

    private const int ReplyTimeoutMs = 2000;
    private const int SettleMs = 200;

    /// <summary>
    /// Send the search probe to <paramref name="host"/> (unicast + limited broadcast), wait briefly for the
    /// console's reply, then settle. <paramref name="ps5"/> selects SRC3/RES3 (PS5) vs SRC2/RES2 (PS4).
    /// Best-effort: never throws for a missing reply; only network-setup faults surface.
    /// </summary>
    public static async Task ProbeAsync(string host, bool ps5, CancellationToken cancellationToken)
    {
        byte[] probe = Encoding.ASCII.GetBytes(ps5 ? "SRC3" : "SRC2");
        byte[] expect = Encoding.ASCII.GetBytes(ps5 ? "RES3" : "RES2");

        IPAddress consoleAddr;
        if (!IPAddress.TryParse(host, out consoleAddr!))
        {
            IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            consoleAddr = Array.Find(resolved, a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? (resolved.Length > 0 ? resolved[0] : IPAddress.Broadcast);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        await udp.SendAsync(probe, new IPEndPoint(consoleAddr, Port), cancellationToken).ConfigureAwait(false);
        try
        {
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, Port), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException) { /* broadcast may be blocked; the unicast probe is enough */ }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyTimeoutMs);
        try
        {
            while (true)
            {
                UdpReceiveResult r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (r.Buffer.Length >= expect.Length && r.Buffer.AsSpan(0, expect.Length).SequenceEqual(expect))
                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No reply within the window; continue — the probe already reached the console.
        }

        await Task.Delay(SettleMs, cancellationToken).ConfigureAwait(false);
    }
}
