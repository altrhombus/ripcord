using System.Net;
using Ripcord.Cloud.Halyard;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>
/// Which of the console's offered candidates to talk to, decided once for everything that needs to know.
///
/// <para>
/// The same choice has to reach two places: the transport that opens the association, and the ACCEPT that names
/// the candidate back to the console. Until 2026-09-26 each had its own rule, and so did the pairing route, a
/// third. They agreed while a candidate parsed and parted company when none did: the transport fell back to the
/// console's known host on 9303 while the ACCEPT named the first candidate offered, and the media leg's
/// <c>IPAddress.Parse</c> on an unparseable one threw (engine comparisons). The C core and the Rust engine make
/// one choice and leave the fallback to the caller, which applies it to both; this is that, for .NET.
/// </para>
/// </summary>
public static class HalyardConsoleCandidates
{
    /// <summary>
    /// The first candidate on a subnet one of our interfaces shares, so a same-network session stays on the LAN;
    /// else the first that parses, since off-network the console's local address is someone else's private
    /// range and the reflexive one is the only way in. Null when none parses.
    /// </summary>
    public static HalyardSignalingCandidate? Choose(
        IReadOnlyList<HalyardSignalingCandidate> candidates, Func<IPAddress, bool>? sharesSubnet = null)
    {
        sharesSubnet ??= HalyardDatagramRegistrationTransport.SharesSubnetWithLocalInterface;
        HalyardSignalingCandidate? firstParsed = null;
        foreach (HalyardSignalingCandidate candidate in candidates)
        {
            if (!TryParse(candidate, out IPAddress? address))
            {
                continue;
            }

            if (sharesSubnet(address))
            {
                return candidate;
            }

            firstParsed ??= candidate;
        }

        return firstParsed;
    }

    /// <summary>
    /// The control association's endpoint and the candidate the ACCEPT names, as one decision: the chosen
    /// candidate, or, when none parses, the console's known host on <paramref name="fallbackPort"/> for both.
    /// Null when there is neither, which the caller reports rather than throwing on a parse.
    /// </summary>
    /// <remarks>
    /// When the fallback is named back it keeps the type of an offered candidate at that address, if there is
    /// one, and is otherwise called <c>LOCAL</c>. That the console accepts a candidate it did not offer is
    /// <b>[X]</b>: no capture has an offer with no parseable candidate in it.
    /// </remarks>
    public static (IPEndPoint Endpoint, HalyardSignalingCandidate Named)? Resolve(
        IReadOnlyList<HalyardSignalingCandidate> candidates,
        string consoleHost,
        int fallbackPort,
        Func<IPAddress, bool>? sharesSubnet = null)
    {
        if (Choose(candidates, sharesSubnet) is { } chosen && TryParse(chosen, out IPAddress? address))
        {
            return (new IPEndPoint(address, chosen.Port), chosen);
        }

        if (!IPAddress.TryParse(consoleHost, out IPAddress? host) || host.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        string type = candidates.FirstOrDefault(c => string.Equals(c.Address, consoleHost, StringComparison.Ordinal))?.Type
                      ?? "LOCAL";
        return (new IPEndPoint(host, fallbackPort), new HalyardSignalingCandidate(type, consoleHost, fallbackPort));
    }

    private static bool TryParse(HalyardSignalingCandidate candidate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        // IPv4 only, as the console offers: IPAddress.TryParse also takes a bare integer ("10") as an address.
        if (IPAddress.TryParse(candidate.Address, out address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && candidate.Address.Count(c => c == '.') == 3
            && candidate.Port is > 0 and <= 65_535)
        {
            return true;
        }

        address = null;
        return false;
    }
}
