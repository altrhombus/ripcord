using Ripcord.Presentation.Consoles;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Presentation.Halyard.Consoles;

/// <summary>
/// Translates between a console family and the Halyard platform enum.
///
/// <para>
/// Lives here rather than on <see cref="ConsoleFamily"/> because that type is deliberately free of protocol
/// references — it is the boundary between product names and codenames, and a <c>HalyardConsolePlatform</c>
/// property on it would erode the boundary by convenience. It also lives here rather than inline at the call
/// site: the same two-line ternary was written out in <c>AddConsolePage.PairAsync</c> and again in the protocol
/// lab, which is how a third copy gets a different default.
/// </para>
/// </summary>
public static class ConsoleFamilyPlatformMap
{
    /// <summary>
    /// The Halyard platform for <paramref name="family"/>. Anything that is not PS4 resolves to PS5, matching
    /// <see cref="ConsoleFamily.ForPlatformName"/>'s rule that an unknown value means PS5 — which is what every
    /// record written before families existed is.
    /// </summary>
    public static HalyardConsolePlatform ToHalyardPlatform(this ConsoleFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family == ConsoleFamily.Ps4 ? HalyardConsolePlatform.Ps4 : HalyardConsolePlatform.Ps5;
    }

    /// <summary>The family for a Halyard platform.</summary>
    public static ConsoleFamily ToFamily(this HalyardConsolePlatform platform)
        => platform == HalyardConsolePlatform.Ps4 ? ConsoleFamily.Ps4 : ConsoleFamily.Ps5;
}
