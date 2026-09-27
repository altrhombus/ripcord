// Ripcord for iPhone, iPad and Apple TV: scaffolding (docs/ios-plan.md). One target for all three, over the
// same RipcordKit and Rust engine as the Mac, and the window-free code in RipcordAppLogic.
//
// What is here is the core loop, deliberately plain, as the Mac's step 4 was: the paired consoles, pairing
// with a code, and a stream with picture, sound and a controller. The design pass, sign-in, internet play,
// touch controls and everything else in the plan come after, on this frame.

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
