// Moving a selection around the library's grid, for the arrow keys and the pad alike.

import CoreGraphics

enum GridNavigation {
    /// How many columns `GridItem.adaptive(minimum:)` lays out in `width`, using its own arithmetic, so the pad's
    /// up and down land on the tile drawn above or below.
    static func columns(width: CGFloat, minimum: CGFloat, spacing: CGFloat, padding: CGFloat) -> Int {
        let usable = width - 2 * padding
        return max(1, Int((usable + spacing) / (minimum + spacing)))
    }

    /// The index a move lands on, or nil when it would leave the grid: a move off an edge stays put rather
    /// than wrapping, as the Finder's does.
    static func next(from index: Int, count: Int, columns: Int, move: PadNavigator.Move) -> Int? {
        let target: Int
        switch move {
        case .left: target = index - 1
        case .right: target = index + 1
        case .up: target = index - columns
        case .down: target = index + columns
        case .activate: return nil
        }
        return (0..<count).contains(target) ? target : nil
    }
}
