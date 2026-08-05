using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Discovery;

/// <summary>
/// The console-family-specific LAN discovery/wake parameters. PS5 and PS4 speak the <em>same</em> SRCH/WAKEUP
/// protocol, but on a different UDP port and with a different version token — wire-confirmed for PS5 in cap49
/// and for PS4 across cap53–cap57 (see docs/protocol/ps5-local-discovery.md, PS4-family section). The
/// control-listener-arming probe (SRC3/SRC2) is handled separately by <c>HalyardControlSearch</c>.
/// </summary>
/// <param name="DiscoveryPort">UDP port SRCH and WAKEUP are sent to (PS5 = 9302, PS4 = 987).</param>
/// <param name="ProtocolVersion">The <c>device-discovery-protocol-version</c> token (PS5 = 00030010, PS4 =
/// 00020020), echoed in both SRCH and WAKEUP.</param>
/// <param name="WakeSourcePort">Source port for the WAKEUP datagram — a sleeping console honours the wake from
/// this port (PS5 = 9303, PS4 = 987).</param>
/// <param name="WakeSearchSourcePort">Source port for the SRCH poll <em>during</em> a wake; 0 = ephemeral. PS5
/// matches the vendor's 9303; PS4 polls from an ephemeral port (cap56). The always-on status probe uses
/// ephemeral for every console regardless, so this only affects the wake flow.</param>
/// <param name="HostType">
/// The <c>host-type</c> token a console of this family reports in its SRCH reply ("PS5"/"PS4"). A required
/// on-wire value, not a display string: it is how a reply is attributed to a family when both families are
/// being searched for at once.
/// </param>
public sealed record HalyardDiscoveryProfile(
    int DiscoveryPort,
    string ProtocolVersion,
    int WakeSourcePort,
    int WakeSearchSourcePort,
    string HostType = "PS5")
{
    /// <summary>PS5: SRCH/WAKEUP on UDP 9302, version 00030010, both from source port 9303 (cap49).</summary>
    public static readonly HalyardDiscoveryProfile Ps5 = new(9302, "00030010", 9303, 9303, "PS5");

    /// <summary>PS4: SRCH/WAKEUP on UDP 987, version 00020020; WAKEUP from 987, SRCH ephemeral (cap53–cap57).</summary>
    public static readonly HalyardDiscoveryProfile Ps4 = new(987, "00020020", 987, 0, "PS4");

    /// <summary>Both families, for a search that does not yet know what it is looking for.</summary>
    public static readonly IReadOnlyList<HalyardDiscoveryProfile> All = [Ps5, Ps4];

    public static HalyardDiscoveryProfile For(HalyardConsolePlatform platform)
        => platform == HalyardConsolePlatform.Ps4 ? Ps4 : Ps5;

    /// <summary>Select by the persisted platform name (the <see cref="HalyardConsolePlatform"/> enum's
    /// <c>ToString</c>, e.g. "Ps4"/"Ps5"); an unknown or absent value defaults to PS5.</summary>
    public static HalyardDiscoveryProfile ForPlatformName(string? platform)
        => string.Equals(platform, nameof(HalyardConsolePlatform.Ps4), StringComparison.OrdinalIgnoreCase)
            ? Ps4 : Ps5;
}
