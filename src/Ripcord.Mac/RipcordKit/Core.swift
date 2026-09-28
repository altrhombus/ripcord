// What the rest of the Mac app knows about the protocol engine. The engine's C module is imported
// internally everywhere in RipcordKit, so nothing outside this framework sees a C type; this is the surface
// they see instead.

internal import CRipcordEngine

public enum Core {
    /// Whether the engine this build links matches the header RipcordKit was compiled against: the ABI
    /// version and every crossing struct's size (docs/engine-plan.md, rule 4). False means a stale library.
    public static var engineMatches: Bool { RipcordEngineLayout.matches() }

    /// Whether this build has a working key-agreement backend: CryptoKit, through the engine's table.
    public static var keyAgreementAvailable: Bool { true }

    /// The interoperability constants are generated into the engine at build time from the one committed
    /// bundle, and the engine does not build without them.
    public static var interopConstantsBundled: Bool { true }
}
