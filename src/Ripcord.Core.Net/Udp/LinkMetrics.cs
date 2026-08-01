using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ripcord.Core.Net.Udp;

/// <summary>
/// Link measurements the console is told about in the launchSpec: round-trip time and MTU.
///
/// <para>
/// The arithmetic is separated from the OS queries so it can be tested. Both values were previously hardcoded —
/// <c>rtt: 0</c> and <c>mtu: 1454</c> — which is a claim rather than a measurement, and the console uses what we
/// declare.
/// </para>
/// </summary>
public static class LinkMetrics
{
    /// <summary>
    /// The MTU the vendor client declares on an ordinary 1500-byte Ethernet path, and the ceiling we keep.
    ///
    /// <para>
    /// Never advertise more than this even on a jumbo-frame link: it is the value a real client is known to send,
    /// and the console's behaviour above it is untested. Lower is measured; higher would be a guess.
    /// </para>
    /// </summary>
    public const int VendorMtu = 1454;

    /// <summary>
    /// Bytes of per-datagram overhead between the interface MTU and the MTU declared to the console.
    ///
    /// <para>
    /// 46 is not arbitrary: 1500 - 46 = 1454, which reproduces the wire-confirmed vendor value exactly, so an
    /// ordinary Ethernet path yields the same number the vendor sends. That agreement is the evidence for it
    /// (IPv4 header 20 + UDP 8 leaves 18 for the Takion framing).
    /// </para>
    /// </summary>
    public const int MtuOverheadBytes = 46;

    /// <summary>
    /// Smallest MTU worth declaring. Below this the link cannot carry video usefully anyway, and a nonsense value
    /// read from an unusual adapter (a tunnel reporting something tiny) should not be forwarded to the console.
    /// </summary>
    public const int MinimumMtu = 576 - MtuOverheadBytes;

    /// <summary>
    /// The MTU to declare, given an interface MTU. Falls back to the vendor value when the interface reports
    /// nothing usable, so behaviour is unchanged from the previous hardcoded constant in that case.
    /// </summary>
    public static int MtuToDeclare(int? interfaceMtu)
    {
        if (interfaceMtu is not int mtu || mtu <= 0)
        {
            return VendorMtu;
        }

        int usable = mtu - MtuOverheadBytes;

        // Clamped at the vendor value: a jumbo-frame LAN would otherwise have us advertise ~8950, which no real
        // client does. A VPN or PPPoE path reporting less than 1500 is exactly the case worth honouring.
        return Math.Clamp(usable, MinimumMtu, VendorMtu);
    }

    /// <summary>
    /// The round-trip time to declare from a set of samples: the MINIMUM, not the mean.
    ///
    /// <para>
    /// Each sample is network delay plus however long the console took to answer that particular request, and
    /// those processing costs differ by message — a session set-up reply does real work, a version handshake does
    /// not. The minimum is the sample least contaminated by the console's own latency, which is what "round-trip
    /// time" is meant to describe. An empty set yields null rather than 0, because 0 is a measurement and "we did
    /// not measure" is not.
    /// </para>
    /// </summary>
    public static double? RoundTripToDeclare(IReadOnlyCollection<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return null;
        }

        double min = double.MaxValue;
        foreach (double sample in samplesMs)
        {
            // Ignore non-finite and negative values rather than letting one poison the result.
            if (double.IsFinite(sample) && sample >= 0 && sample < min)
            {
                min = sample;
            }
        }

        return min is double.MaxValue ? null : min;
    }

    /// <summary>
    /// MTU of the interface that would be used to reach <paramref name="destination"/>, or null if it cannot be
    /// determined.
    ///
    /// <para>
    /// Found by asking the OS routing table which local address it would use (connecting a UDP socket performs no
    /// I/O and sends nothing) and then matching that address to an adapter. This is the interface MTU rather than a
    /// true path MTU: discovering the latter means sending don't-fragment probes and interpreting ICMP, which on a
    /// LAN or Wi-Fi link — where the first hop is the constraint — buys nothing for the risk of firing unparseable
    /// datagrams at a port the console is mid-handshake on.
    /// </para>
    /// </summary>
    public static int? InterfaceMtuTowards(IPAddress destination)
    {
        try
        {
            using var probe = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(destination, 9295); // no traffic: Connect on a UDP socket only sets the route
            if (probe.LocalEndPoint is not IPEndPoint local)
            {
                return null;
            }

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties = nic.GetIPProperties();
                bool matches = properties.UnicastAddresses.Any(a => a.Address.Equals(local.Address));
                if (!matches)
                {
                    continue;
                }

                int mtu = destination.AddressFamily == AddressFamily.InterNetworkV6
                    ? properties.GetIPv6Properties().Mtu
                    : properties.GetIPv4Properties().Mtu;

                return mtu > 0 ? mtu : null;
            }

            return null;
        }
        catch (Exception)
        {
            // Any failure here means "unknown", which the caller turns into the vendor default. This runs during
            // connect, so it must never be the reason a session fails to start.
            return null;
        }
    }
}
