// What the library says about each console: silence is not knowledge, so Not found takes two silent rounds,
// and a console that moved address is followed by its id.

import Foundation
import RipcordKit
import Synchronization
import Testing

@MainActor
@Suite("Network watch")
struct NetworkWatchTests {
    static let paired = PairedConsole(host: "192.0.2.7", name: "PS5", consoleID: "cid-1", accountID: "1", family: .ps5,
                                      registrationKey: [], companion: [])

    static func answer(address: String = "192.0.2.7", id: String = "cid-1", awake: Bool = true) -> DiscoveredConsole {
        DiscoveredConsole(hostID: id, family: .ps5, hostType: "PS5", name: "PS5", systemVersion: "1",
                          address: address, isAwake: awake)
    }

    /// A network that answers with whatever the test last put in the box.
    final class Script: Sendable {
        let next = Mutex<[DiscoveredConsole]>([])
        var round: NetworkWatch.Round { { [self] _ in next.withLock { $0 } } }
    }

    @Test("before any round the state is unknown, not Not found")
    func unknownFirst() {
        let watch = NetworkWatch(round: Script().round)
        #expect(watch.reachability(of: Self.paired) == .unknown)
    }

    @Test("an answer is Ready or Resting")
    func answered() async {
        let script = Script()
        let watch = NetworkWatch(round: script.round)
        script.next.withLock { $0 = [Self.answer(awake: false)] }
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .resting)
        script.next.withLock { $0 = [Self.answer(awake: true)] }
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .ready)
    }

    @Test("Not found only after two silent rounds, and one answer clears it")
    func twoMisses() async {
        let script = Script()
        let watch = NetworkWatch(round: script.round)
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .unknown)     // one datagram lost is not knowledge
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .notFound)
        script.next.withLock { $0 = [Self.answer()] }
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .ready)
        script.next.withLock { $0 = [] }
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .unknown)     // the count starts again
    }

    @Test("a console answering from a new address is matched by its id, and the move is reported")
    func followsAMove() async {
        let script = Script()
        let watch = NetworkWatch(round: script.round)
        var moved: (String, String)?
        watch.onAddressChange = { console, address in moved = (console.consoleID, address) }
        script.next.withLock { $0 = [Self.answer(address: "192.0.2.99")] }
        await watch.refresh([Self.paired])
        #expect(watch.reachability(of: Self.paired) == .ready)
        #expect(moved?.0 == "cid-1")
        #expect(moved?.1 == "192.0.2.99")
    }
}
