// Which of the mark's dashes a connect has filled.

import RipcordKit

/// The three dashes, as connect progress (DESIGN.md, "The launch": three real stages).
enum ConnectProgress {
    static func dashes(for stage: SessionStage) -> Int {
        switch stage {
        case .streamReady, .streaming: 3
        case .sessionReady, .senkushaUp, .takionUp, .streamKeys: 2
        case .controlOpen, .signedIn: 1
        case .idle, .ended: 0
        }
    }
}
