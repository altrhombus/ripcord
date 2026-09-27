// Pairing (DESIGN.md, "Pairing"): a sheet on the library. Sign-in leads, and the code route is one
// unweighted press away (docs/design.md, amended 2026-09-22). The route is a mechanism, and the player has
// no stake in it, so when the console is on the signed-in account it simply pairs.

import SwiftUI
import RipcordKit

struct PairingSheet: View {
    let request: PairingRequest
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow

    private enum Step: Equatable {
        case choose
        case route
        case code
        case working(String)
        case failed(String)
        case paired(String)
    }

    @State private var step: Step = .choose
    @State private var target: Target?
    @State private var address = ""
    @State private var pin = ""
    @State private var accountID = ""
    @State private var pairedKey: String?
    @State private var window: NSWindow?

    /// The console being paired: from the network, or an address typed in.
    private struct Target: Equatable {
        var host: String
        var name: String
        var consoleID: String
        var family: ConsoleFamily
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            content
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(24)
        }
        .frame(width: 480)
        .background(WindowReader { window = $0 })
        .onAppear(perform: begin)
    }

    @ViewBuilder
    private var content: some View {
        switch step {
        case .choose: chooseStep
        case .route: routeStep
        case .code: codeStep
        case .working(let line):
            HStack(spacing: 12) {
                ProgressView().controlSize(.small)
                Text(line)
            }
            .frame(maxWidth: .infinity, minHeight: 120)
        case .failed(let why): failedStep(why)
        case .paired(let name):
            PairedCelebration(consoleName: name, playNow: {
                dismiss()
                if let pairedKey { openWindow(id: "stream", value: StreamTarget(consoleKey: pairedKey)) }
            }, done: { dismiss() })
            .frame(maxWidth: .infinity)
        }
    }

    // MARK: Which console

    private var nearby: [DiscoveredConsole] {
        model.network.found.values.filter { $0.family != nil }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private var chooseStep: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Pair a Console").font(.title2.weight(.semibold))
            Text("Choose the console to play from. It needs to be on and on this network.")
                .foregroundStyle(.secondary)
            if nearby.isEmpty {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Looking on this network…").foregroundStyle(.secondary)
                }
                .padding(.vertical, 8)
            } else {
                VStack(spacing: 0) {
                    ForEach(nearby, id: \.hostID) { found in
                        Button { choose(found) } label: {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text(found.name).font(.headline)
                                    Text("\(found.hostType) · \(found.address)").font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if isPaired(found) { Text("Paired").font(.caption).foregroundStyle(.secondary) }
                                Image(systemName: "chevron.right").foregroundStyle(.tertiary)
                            }
                            .padding(.vertical, 8)
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        Divider()
                    }
                }
            }
            DisclosureGroup("Enter an address") {
                HStack {
                    TextField("192.168.1.20", text: $address)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(chooseAddress)
                    Button("Continue", action: chooseAddress)
                        .disabled(address.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                .padding(.top, 6)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .task {
            // A round now, rather than waiting for the next scheduled one.
            await model.network.refresh(model.consoles)
        }
    }

    // MARK: The route

    private var accountConsole: CloudConsole? {
        target.flatMap { model.account.cloudConsole(named: $0.name) }
    }

    private var routeStep: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Pair \(target?.name ?? "")").font(.title2.weight(.semibold))
            if model.account.busy {
                HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Signing in…") }
            } else {
                Text("Sign in to PlayStation Network and consoles on your account pair without a code.")
                    .foregroundStyle(.secondary)
                if let error = model.account.lastError {
                    Text(error).font(.callout).foregroundStyle(.secondary)
                }
            }
            HStack {
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Use a Pairing Code Instead") { step = .code }
                Button("Sign In to PlayStation Network…") {
                    Task {
                        await model.account.signIn(from: window)
                        await model.account.refreshConsoles()
                        routeAfterSignIn()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(model.account.busy)
            }
        }
    }

    // MARK: The code

    private var codeStep: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Pair \(target?.name ?? "") with a code").font(.title2.weight(.semibold))
            Text("Open Settings → System → Remote Play → Link Device. The console shows an 8-digit code.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Form {
                TextField("Code", text: $pin, prompt: Text("8 digits"))
                    .font(.title3.monospacedDigit())
                if !model.account.isSignedIn {
                    TextField("Account ID", text: $accountID, prompt: Text("numeric account id"))
                }
            }
            .formStyle(.columns)
            HStack {
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                if model.account.isAvailable && !model.account.isSignedIn {
                    Button("Sign In Instead") { step = .route }
                }
                Button("Pair", action: pairWithCode)
                    .keyboardShortcut(.defaultAction)
                    .disabled(pin.filter(\.isNumber).count != 8
                              || (!model.account.isSignedIn && accountID.trimmingCharacters(in: .whitespaces).isEmpty))
            }
        }
        .onAppear { if accountID.isEmpty { accountID = model.account.rememberedAccountID } }
    }

    private func failedStep(_ why: String) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("That didn’t pair").font(.title2.weight(.semibold))
            Text(why).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Try Again") { step = accountConsole != nil ? .route : .code }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }

    // MARK: Flow

    private func begin() {
        accountID = model.account.rememberedAccountID
        if let found = request.preselected { choose(found) }
        Task { await model.account.refreshConsoles() }
    }

    private func isPaired(_ found: DiscoveredConsole) -> Bool {
        model.consoles.contains { $0.consoleID == found.hostID || $0.host == found.address }
    }

    private func choose(_ found: DiscoveredConsole) {
        target = Target(host: found.address, name: found.name, consoleID: found.hostID, family: found.family ?? .ps5)
        routeForTarget()
    }

    private func chooseAddress() {
        let host = address.trimmingCharacters(in: .whitespaces)
        guard !host.isEmpty else { return }
        step = .working(String(localized: "Looking for a console at \(host)…"))
        Task {
            let heard = await Task.detached {
                (try? LANDiscovery.search(hosts: [host], timeout: .seconds(3))) ?? []
            }.value
            if let found = heard.first {
                choose(found)
            } else {
                step = .failed(String(localized: "Nothing answered at \(host). Check the address, and that the console is on."))
            }
        }
    }

    /// Signed in with the console on the account: pair, with no question. Not signed in: sign-in leads.
    /// The reduced edition has no account, so the code form is all there is.
    private func routeForTarget() {
        if accountConsole != nil { pairThroughAccount() }
        else if model.account.isSignedIn || !model.account.isAvailable { step = .code }
        else { step = .route }
    }

    private func routeAfterSignIn() {
        guard model.account.isSignedIn else { return }   // cancelled or failed: stay, the error is shown
        if accountConsole != nil { pairThroughAccount() } else { step = .code }
    }

    private func pairThroughAccount() {
        guard let target, let cloud = accountConsole, let gateway = model.account.gateway else { return }
        step = .working(String(localized: "Pairing with \(target.name) through your account…"))
        Task {
            do {
                var paired = try await AccountPairing.pair(cloud, gateway: gateway, host: target.host)
                if paired.consoleID.isEmpty { paired.consoleID = target.consoleID }
                finish(paired)
            } catch {
                step = .failed(String(describing: error))
            }
        }
    }

    private func pairWithCode() {
        guard let target else { return }
        let account = model.account.account?.accountID ?? accountID.trimmingCharacters(in: .whitespaces)
        let code = pin
        step = .working(String(localized: "Pairing with \(target.name)…"))
        Task {
            let result = await Task.detached { () -> Result<PairedConsole, PairingError> in
                Result { () throws(PairingError) -> PairedConsole in
                    try Pairing.register(host: target.host, family: target.family, accountID: account,
                                         pin: code, name: target.name, consoleID: target.consoleID)
                }
            }.value
            switch result {
            case .success(let paired):
                if !model.account.isSignedIn { model.account.rememberedAccountID = account }
                finish(paired)
            case .failure(let error):
                step = .failed(error.description)
            }
        }
    }

    private func finish(_ paired: PairedConsole) {
        do {
            try model.save(paired)
            pairedKey = model.key(of: paired)
            model.selection = pairedKey
            pin = ""
            step = .paired(paired.name.isEmpty ? paired.host : paired.name)
        } catch {
            step = .failed(String(describing: error))
        }
    }
}

/// The NSWindow a view is in, for the few AppKit calls that need one (a sheet's parent, full screen).
struct WindowReader: NSViewRepresentable {
    var found: (NSWindow?) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { found(view.window) }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { found(nsView.window) }
    }
}
