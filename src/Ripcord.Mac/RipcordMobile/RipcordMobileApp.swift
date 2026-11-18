// Ripcord for iPhone, iPad and Apple TV (docs/ios-plan.md). One target for all three, over the
// same RipcordKit and Rust engine as the Mac, and the window-free code in RipcordAppLogic.
//
// What is here is the core loop, deliberately plain: the paired consoles, pairing with a code, and a
// stream with picture, sound and a controller. There is no sign-in, internet play or touch control.

import SwiftUI

@main
struct RipcordMobileApp: App {
    @State private var model = MobileModel()

    var body: some Scene {
        WindowGroup {
            LibraryScreen()
                .environment(model)
        }
    }
}
