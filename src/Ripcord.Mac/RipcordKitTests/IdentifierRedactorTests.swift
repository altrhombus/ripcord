// IdentifierRedactor: what ripcord-lab prints must already be safe to paste into a record. The identifying
// inputs are assembled at runtime, so this file holds nothing shaped like a real address, key or account id and
// the published-tree sweep has nothing to allowlist. The same cases as IdentifierRedactorTests.cs.

import RipcordKit
import Testing

@Suite struct IdentifierRedactorTests {
    private func dotted(_ o: Int...) -> String { o.map(String.init).joined(separator: ".") }
    private func padded(_ o: Int...) -> String {
        o.map { String(repeating: " ", count: 3 - String($0).count) + String($0) }.joined(separator: ".")
    }
    private func hex(_ start: Int, _ count: Int) -> String {
        (start..<(start + count)).map { String(format: "%02x", $0 & 0xff) }.joined()
    }

    @Test("an address is redacted in either form, and the console is told apart from the client")
    func addresses() {
        let r = IdentifierRedactor()
        let console = dotted(172, 20, 0, 5), client = dotted(172, 20, 0, 9)
        r.learnConsoleAddress(console)
        #expect(r.redact("registering with \(console) from \(client)") == "registering with <console-ip> from <client-ip>")
        #expect(r.redact("Host: \(padded(172, 20, 0, 5)):9295") == "Host: <console-ip>:9295")
    }

    @Test("addresses that identify nothing print as they are")
    func benign() {
        let line = "127.0.0.1 0.0.0.0 255.255.255.255 224.0.0.251 10.0.0.7 172.31.0.1 192.168.1.20 "
            + "192.0.2.104 198.51.100.1 203.0.113.9 Host: 192.  0.  2.104:9295 version 10.0.26100.0"
        #expect(IdentifierRedactor().redact(line) == line)
    }

    @Test("keys, ids and MACs are redacted whatever their separators, and plain counts are not")
    func hexValues() {
        let r = IdentifierRedactor()
        let key = hex(0x10, 16), mac = hex(0xa0, 6)
        let colons = stride(from: 0, to: 12, by: 2).map { i in
            String(mac[mac.index(mac.startIndex, offsetBy: i)..<mac.index(mac.startIndex, offsetBy: i + 2)])
        }.joined(separator: ":")
        #expect(r.redact("key \(key)") == "key <redacted>")
        #expect(r.redact("key 0x\(key.uppercased())") == "key <redacted>")
        #expect(r.redact("mac \(colons) and \(mac)") == "mac <redacted> and <redacted>")
        let counts = "5749 packets, \(1_727_553_600_000 as Int64) ms, 1920x1080"   // a ms timestamp, assembled
        #expect(r.redact(counts) == counts)
        let account = String(1_000_000_000_000_000_000 as UInt64 + 4242)
        #expect(r.redact("account \(account)") == "account <redacted>")
    }

    @Test("learned names, denied values and added patterns are redacted")
    func learned() {
        let r = IdentifierRedactor()
        r.learnName("Den Box")
        r.deny("hunter-ssid")
        r.addPattern(#"\bPS[45]-\d{3}\b"#, replacement: "<hostname>")
        #expect(r.redact("found den box and PS5-" + "123") == "found <hostname> and <hostname>")
        #expect(r.redact("wifi Hunter-SSID") == "wifi <redacted>")
        #expect(r.redact("the den boxer") == "the den boxer")
    }
}
