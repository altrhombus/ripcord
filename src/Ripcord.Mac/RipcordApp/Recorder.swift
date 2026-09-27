// Recording a stream to a movie file (DESIGN.md, "Picture in Picture and recording").

import Foundation

enum Recorder {
    /// Where recordings go: the Movies folder.
    static var folder: URL { URL.moviesDirectory }
}
