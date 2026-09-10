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

    /// <summary>
    /// Previous-generation console of the same family. Implemented and live-verified: discovery, wake,
    /// registration from scratch, control-field crypto, stream handshake and key schedule. It differs from
    /// <see cref="Halyard"/> in the control KDF variant and its context key, in discovery and wake landing
    /// on a different UDP port with an older protocol version, and in being H.264-only; the stream key
    /// schedule is the same machinery unchanged. See docs/journal.md, "Phase 2 — PS4 support".
    /// </summary>
    HalyardLegacy,

    /// <summary>The other console family's streaming backend; provisional codename Lanyard, not yet implemented.</summary>
    Lanyard,
}
