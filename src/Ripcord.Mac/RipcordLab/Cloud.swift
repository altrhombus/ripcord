// The lab's account commands: the cloud tier from a terminal, the counterpart of ProtocolLab's signin and
// friends. The refresh token goes where the app's will, the Keychain, under the app's service name, so a
// sign-in here is a sign-in there.
//
// Sign-in from a terminal works the way the .NET lab's does: the URL is printed, the person signs in in any
// browser, and pastes back the address the browser ends on (Sony's redirect page, whose URL carries the
// code). The app does the same inside its own web view (RipcordKit/Cloud/WebSignIn.swift).

import Dispatch
import Foundation
import RipcordKit
import Synchronization

let cloudUsage = """
      cloud                 where the OAuth credential comes from, and whether a session is stored
      signin                sign in to PlayStation Network (prints a URL; paste back where it lands)
      signout               forget the stored session
      cloud-consoles        the account's consoles, from the cloud
      cloud-wake <duid>     wake a console through the cloud (from rest mode, on any network)

    """

/// The committed credential file, read where it lives: the lab runs from the repository root (the shared
/// scheme's working directory) and has no bundle resources of its own. The app reads its bundled copy.
func labCredentialURL() -> URL? {
    if let bundled = ClientConfigLoader.bundledCredentialURL() { return bundled }
    let inTree = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        .appending(path: "src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json")
    return FileManager.default.fileExists(atPath: inTree.path) ? inTree : nil
}

func runCloud(_ arguments: [String]) -> Never {
    let (config, source) = ClientConfigLoader.load(bundledCredential: labCredentialURL())
    let store = KeychainAccountTokenStore()

    if arguments.first == "cloud" {
        switch source {
        case .environment: say("credential:  from RIPCORD_CLIENT_ID / RIPCORD_CLIENT_SECRET")
        case .configFile(let url): say("credential:  from \(url.path)\(config.isConfigured ? "" : " (unusable)")")
        case .bundled(let url): say("credential:  bundled (\(url.path))")
        case .none: say("credential:  none; sign-in is unavailable in this build")
        }
        say("session:     \(store.load() != nil ? "stored in the Keychain" : "none stored")")
        exit(config.isConfigured ? 0 : 1)
    }
    if arguments.first == "signout" {
        store.clear()
        say("signed out")
        exit(0)
    }
    guard config.isConfigured else { fail("\(arguments[0]): \(CloudError.notConfigured)") }

    let gateway: AccountGateway
    do {
        gateway = try AccountGateway(config: config, store: store, deviceID: DeviceIdentity.stable())
    } catch {
        fail("\(arguments[0]): \(error)")
    }

    do {
        switch arguments.first {
        case "signin":
            let url = try gateway.beginSignIn()
            say("Open this in a browser and sign in:")
            print("\n\(url.absoluteString)\n")   // raw: a URL to open, not a record
            say("Then paste the address the browser ends on (it starts \(ClientConfig.defaultRedirectURI)):")
            guard let line = readLine(), let redirect = URL(string: line.trimmingCharacters(in: .whitespaces)) else {
                fail("signin: no address")
            }
            guard gateway.redirectMatcher.isCompletion(redirect) else { fail("signin: that is not the redirect with the code") }
            let account = try blocking { try await gateway.completeSignIn(redirect: redirect) }
            say("signed in as \(account.onlineID); the session is stored in the Keychain")

        case "cloud-consoles":
            try blocking { try await requireSession(gateway) }
            let consoles = try blocking { try await gateway.cloud.listConsoles() }
            if consoles.isEmpty { say("the account has no consoles") }
            for c in consoles {
                labRedactor.learnName(c.device.name)
                say("\(c.platform)  \(c.device.name)  remote play \(c.remotePlayEnabled ? "on" : "off")  "
                      + "wake \(c.canWake ? "yes" : "no")  duid \(c.duid)")
            }

        case "cloud-wake":
            guard arguments.count == 2 else { fail(usage, code: 2) }
            try blocking { try await requireSession(gateway) }
            try blocking { try await SessionCoordinator(cloud: gateway.cloud).wake(consoleDUID: arguments[1]) }
            say("wake command accepted by the account service (that is not the console being awake yet)")

        default:
            fail(usage, code: 2)
        }
    } catch {
        fail("\(arguments[0]): \(error)")
    }
    exit(0)
}

func requireSession(_ gateway: AccountGateway) async throws {
    guard await gateway.ensureSignedIn() else { throw CloudError.notSignedIn }
}

/// Runs async work from the lab's synchronous top level, as `connect` waits on its session.
@discardableResult
func blocking<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) throws -> T {
    let outcome = Mutex<Result<T, any Error>?>(nil)
    let done = DispatchSemaphore(value: 0)
    Task {
        let result: Result<T, any Error>
        do { result = .success(try await operation()) } catch { result = .failure(error) }
        outcome.withLock { $0 = result }
        done.signal()
    }
    done.wait()
    return try outcome.withLock { $0! }.get()
}
