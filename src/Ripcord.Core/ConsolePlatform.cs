namespace Ripcord.Core;

/// <summary>
/// Identifies which remote-play/streaming backend a console uses, so the app shell can key
/// discovery/session/pairing factories per platform without branching on vendor names directly.
/// Values use internal codenames rather than trademarked console names.
/// </summary>
public enum ConsolePlatform
{
    /// <summary>Current-generation console family, primary Phase 1 target (codename Halyard).</summary>
    Halyard,

    /// <summary>Previous-generation console of the same family (older protocol); not yet implemented.</summary>
    HalyardLegacy,

    /// <summary>The other console family's streaming backend; provisional codename Lanyard, not yet implemented.</summary>
    Lanyard,
}
