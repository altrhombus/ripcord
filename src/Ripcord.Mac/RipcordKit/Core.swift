// What the rest of the Mac app knows about libripcord. The C module is imported internally everywhere
// in RipcordKit, so nothing outside this framework sees a C type; this is the surface they see instead.

internal import CLibripcord

public enum Core {
    /// Whether this build has a working key-agreement backend. Always true on the Mac, where CryptoKit
    /// supplies it; a false here means the Libripcord target lost RC_ECDH_EXTERNAL_BACKEND and linked the
    /// fail-closed stub instead, and no session can be secured.
    public static var keyAgreementAvailable: Bool { rc_ecdh_available() == 1 }

    /// Whether the generated interoperability constants were complete when the core was built. See
    /// libripcord/tools/gen_constants.py: a bundle missing a table builds, and reports it here.
    public static var interopConstantsBundled: Bool {
        halyard_v1_constants_bundled == 1 && halyard_v1_registration_bundled == 1
    }
}
