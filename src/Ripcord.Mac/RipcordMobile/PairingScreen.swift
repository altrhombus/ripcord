// Pairing with the code the console shows. Sign-in pairing, which the Mac leads with, comes later: on iPhone
// and iPad it is the same web view in UIKit's presentation, and Apple TV has no WebKit, so it will need another
// route (docs/ios-plan.md).

import SwiftUI
import RipcordKit

struct PairingScreen: View {
    @Environment(MobileModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var host = ""
    @State private var pin = ""
    @State private var accountID = UserDefaults.standard.string(forKey: "pairingAccountID") ?? ""
    @State private var working = false
    @State private var problem: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Console address", text: $host, prompt: Text("192.168.1.20"))
                        #if os(iOS)
                        .keyboardType(.decimalPad)
                        #endif
                    TextField("Code", text: $pin, prompt: Text("8 digits"))
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                    TextField("Account ID", text: $accountID, prompt: Text("numeric account id"))
                } footer: {
                    Text("Open Settings → System → Remote Play → Link Device. The console shows an 8-digit code.")
                }
                if let problem {
                    Text(problem).foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Pair a Console")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Pair", action: pair)
                        .disabled(working || host.isEmpty || pin.filter(\.isNumber).count != 8 || accountID.isEmpty)
                }
            }
            .task { prefillFromTheNetwork() }
        }
    }

    /// The first console the network watch has heard that is not paired yet, if there is one.
    private func prefillFromTheNetwork() {
        guard host.isEmpty, let found = model.network.found.values.first(where: { found in
            !model.consoles.contains { $0.consoleID == found.hostID || $0.host == found.address }
        }) else { return }
        host = found.address
    }

    private func pair() {
        working = true
        problem = nil
        let (host, pin, account) = (host.trimmingCharacters(in: .whitespaces), pin, accountID.trimmingCharacters(in: .whitespaces))
        let found = model.network.found.values.first { $0.address == host }
        Task {
            let result = await Task.detached { () -> Result<PairedConsole, PairingError> in
                Result { () throws(PairingError) -> PairedConsole in
                    try Pairing.register(host: host, family: found?.family ?? .ps5, accountID: account, pin: pin,
                                         name: found?.name ?? "", consoleID: found?.hostID ?? "")
                }
            }.value
            working = false
            switch result {
            case .success(let paired):
                UserDefaults.standard.set(account, forKey: "pairingAccountID")
                do {
                    try model.save(paired)
                    dismiss()
                } catch {
                    problem = String(describing: error)
                }
            case .failure(let error):
                problem = error.description
            }
        }
    }
}
