// The PlayStation Network account, as the app shows it: whether sign-in is possible in this build, who is
// signed in, and the sign-in itself in the web view (RipcordKit/Cloud/WebSignIn.swift says why it is not
// ASWebAuthenticationSession).
//
// The refresh token lives in the Keychain under the app's service name, where ripcord-lab's sign-in also
// puts it, so signing in from either signs in both.

import AppKit
import Observation
import RipcordKit

@MainActor
@Observable
final class AccountModel {
    /// Nil in the reduced edition built without the OAuth credential, and wherever the credential cannot be
    /// read. Every account surface is absent then, not disabled (DESIGN.md, "Settings").
    let gateway: AccountGateway?
    private(set) var account: CloudAccount?
    private(set) var busy = false
    private(set) var lastError: String?
    /// The account's consoles, fetched when a surface asks.
    private(set) var cloudConsoles: [CloudConsole] = []

    /// The account id a person typed into the code form, remembered so it is never asked for twice.
    var rememberedAccountID: String {
        get { account?.accountID ?? UserDefaults.standard.string(forKey: "pairingAccountID") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "pairingAccountID") }
    }

    var isAvailable: Bool { gateway != nil }
    var isSignedIn: Bool { account != nil }

    init() {
        let (config, _) = ClientConfigLoader.load()
        if config.isConfigured, let device = try? DeviceIdentity.stable() {
            gateway = try? AccountGateway(config: config, store: KeychainAccountTokenStore(), deviceID: device)
        } else {
            gateway = nil
        }
    }

    /// Restores a stored session at launch. A refresh that fails leaves the person signed out, quietly: a
    /// LAN-only player never sees an account error.
    func restore() async {
        guard let gateway, gateway.hasStoredSession else { return }
        account = try? await gateway.restore()
    }

    func signIn(from window: NSWindow?) async {
        guard let gateway, !busy else { return }
        busy = true
        lastError = nil
        defer { busy = false }
        do {
            let url = try gateway.beginSignIn()
            let controller = WebSignInController(authorizeURL: url, matcher: gateway.redirectMatcher)
            let redirect = try await controller.presentInWindow(parent: window)
            account = try await gateway.completeSignIn(redirect: redirect)
            await refreshConsoles()
        } catch WebSignInError.cancelled {
            // Closing the sign-in window is a choice, not a failure.
        } catch {
            lastError = String(describing: error)
        }
    }

    func signOut() async {
        await gateway?.signOut()
        account = nil
        cloudConsoles = []
    }

    func refreshConsoles() async {
        guard let gateway, account != nil else { cloudConsoles = []; return }
        do {
            cloudConsoles = try await AccountPairing.pairableConsoles(gateway)
        } catch {
            lastError = String(describing: error)
        }
    }

    /// The account console that a console on the network is, by name, the only link the two share.
    func cloudConsole(named name: String) -> CloudConsole? {
        cloudConsoles.first { $0.device.name.caseInsensitiveCompare(name) == .orderedSame }
    }
}
