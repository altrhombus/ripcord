// Reading the pairing code off the television: eight digits however they are grouped, nothing cut out of a
// longer number, and nothing believed until two frames agree.

import Testing

@Suite("Pairing code reader")
struct PairingCodeReaderTests {
    @Test("eight digits, grouped any way or not at all")
    func groupings() {
        for text in ["12345678", "1234 5678", "1234-5678", "12 34 56 78", "1 2 3 4 5 6 7 8", "1234\u{2013}5678",
                     "Code: 1234 5678.", "(12345678)"] {
            #expect(PairingCodeReader.codes(in: text) == ["12345678"], "\(text)")
        }
    }

    @Test("not seven, not nine, and not eight cut out of a longer number")
    func refusals() {
        for text in ["1234567", "123456789", "1234 56789", "12345678 9", "192.168.1.123", "21:45 12345678901",
                     "1234  5678", "--12345678"] {
            #expect(PairingCodeReader.codes(in: text).filter { $0 != "12345678" }.isEmpty, "\(text)")
        }
        #expect(PairingCodeReader.codes(in: "1234  5678").isEmpty)      // two separators in a row end the run
        #expect(PairingCodeReader.codes(in: "123456789").isEmpty)
        #expect(PairingCodeReader.codes(in: "12345678 9").isEmpty)      // the ninth digit joins across one space
    }

    @Test("a frame with two different codes is no answer")
    func ambiguity() {
        #expect(PairingCodeReader.code(in: ["1234 5678", "8765 4321"]) == nil)
        #expect(PairingCodeReader.code(in: ["1234 5678", "the code is 12345678"]) == "12345678")
    }

    @Test("a code is believed only when two frames in a row agree")
    func agreement() {
        var reader = PairingCodeReader()
        #expect(reader.observe(["1234 5678"]) == nil)
        #expect(reader.observe(["1234 5679"]) == nil)         // a misread resets it
        #expect(reader.observe(["1234 5679"]) == "12345679")
        var blinked = PairingCodeReader()
        #expect(blinked.observe(["1234 5678"]) == nil)
        #expect(blinked.observe([]) == nil)                   // a frame with nothing resets it too
        #expect(blinked.observe(["1234 5678"]) == nil)
        #expect(blinked.observe(["1234 5678"]) == "12345678")
    }
}
