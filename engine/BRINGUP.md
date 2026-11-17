# engine: measurement record

These are the engine's dated measurement runs: Phase 1, the Phase 2 layers, the first fuzzing and Miri
runs, and the sizes. Each section records what was measured on its date and is not rewritten after the
fact. [`README.md`](README.md) has the current reference.

## Phase 1, measured (2026-09-25, M4 Max, macOS 27)

| What | Result |
|---|---|
| `stream-crypto.kat` | 65 of 65: all 9 `gmac`, 4 `streamkdf`, 12 `packetnonce` and 12 `packettag` lines |
| Differential against `HalyardPacketCrypto` | 80 keystream offsets × 4 checks (CTR, both seal directions, the control AAD rule, tamper), across rotation windows and the 32-bit edge |
| Per packet, one Swift harness for both engines (`ripcord-lab bench`) | C core **7.71–8.11 µs**, Rust **0.53–0.57 µs** |
| Per packet, from .NET | Rust through P/Invoke **0.67–0.71 µs**, managed `HalyardPacketCrypto` 16.1–17.3 µs |
| Per packet, from .NET on Windows x64 (2026-09-30, an x64 desktop) | Rust through P/Invoke **0.92 µs**, managed `HalyardPacketCrypto` 14.29 µs, one run |
| Mac lab | Links through the generated header; the RipcordKit tests still build and pass (Phase 1; the Mac has since moved onto the engine entirely) |
| .NET | Links through the generated bindings: `[UnmanagedCallersOnly]` callbacks, a `GCHandle` user pointer |
| Size | `libripcord.dylib` 386 KB stripped. `ripcord-lab` grows from 1.10 MB to 2.35 MB stripped, which is more than the engine's own size and still to be explained |

All three benchmarks run one workload: `PacketCryptoBenchmark.swift`'s packets, 1,426 bytes each,
spaced so that every packet lands in its own GMAC rotation window. That is the worst case. At wire
spacing Rust takes 0.54 µs (`packet_bench`), because a rotation window then serves many packets.

Most of the gap is hardware AES and PMULL, which RustCrypto detects at run time, against the C core's
portable table implementation. The rest is structure. The CTR cipher borrows a key schedule expanded
once, where the C core expands the key on every packet. The GMAC cache is keyed by rotation window,
where the C core derives the key with a SHA-256 on every packet just to check its cache.

## What the engine costs in size (2026-09-26, M4 Max)

Measured because the app and each extension will link the engine. The shipping library (no
`test-support`), stripped with `strip -x`:

| Build | Stripped `libripcord.dylib` |
|---|---|
| As shipped before this | 719 KB |
| `panic = "abort"` | 653 KB, **not taken** |
| `lto = "fat"` | 719 KB, no change |
| `opt-level = "s"` / `"z"` | 720 KB / 672 KB, **not taken** |
| Without the FEC product table | **653 KB**, taken |

A linker map of the 719 KB build, totalled by crate: `ripcord_proto` 31%, `core` 17% (formatting and generic
instantiations), `std` 8%, and std's backtrace symbolizer (`gimli`, `addr2line`, `rustc_demangle`,
`object`) about 10%. The curve arithmetic (`primeorder`, `p521`, `p256`, `crypto_bigint`) is 6%.

- **The FEC product table** was one static of 64 KB, a table of every GF(2^8) product built at compile
  time. It was a tenth of the library, for work done only when a frame has lost units. `mul_accumulate`
  now builds the one row it needs on the stack from the log and exp tables. A worst-case recovery (k 40,
  m 10, ten 1,400-byte units lost) takes 174 µs where it took 155 µs, against a 16.7 ms frame at 60 fps.
- **`panic = "abort"` is ruled out by the plan's rule 5.** Every export runs inside `catch_unwind` so that a
  panic ends one session rather than the host process, and aborting would make every bug a crash.
- **`opt-level = "z"`** saves 47 KB and costs speed on the per-packet path the README's gate measures.
  Not taken without a benchmark that shows the cost is small.
- **The backtrace symbolizer stays** on stable Rust. It is linked by std's default panic hook, which unwinding
  needs, and removing it takes a nightly `build-std` with `panic_immediate_abort`, which is also an abort.

What an executable carries, and why the lab was so much larger than the library (2026-09-27): **dead-code
stripping was off.** Nothing in the Mac project set `DEAD_CODE_STRIPPING`, and Xcode's default for a project
whose settings all live in xcconfig files is off, so the linker kept every object it pulled from the engine's
static archive whole. `std` alone was 440 KB of the lab. With it on, from `Config/Project.xcconfig`:

| Binary, stripped | Before | After |
|---|---|---|
| `ripcord-lab` | 2.86 MB | **1.72 MB** |
| The app's own executable | 3.71 MB | **2.64 MB** |

A link map of the stripped lab: RipcordKit's own Swift is the largest part at 641 KB (48% of its code), then
`ripcord_proto` 240 KB, `std` 181 KB, `core` 35 KB and the crypto curves under 20 KB. The engine's crates total
576 KB. So the engine is now well under half of the lab, and the next saving is not in the engine.

## Phase 2, the first layer, measured (2026-09-26)

| What | Result |
|---|---|
| `control-crypto.kat` | 172 of 172: KDF (both families), context keys, field IVs, CFB and OFB, the field and streaminfo chains |
| `registration-crypto.kat` | 97 of 97: transport key, wrap, unwrap and scatter/gather on both families |
| `account-pairing.kat` | 81 of 81 for `seed` and `accountwrap`. The 8 `accountrgst` lines are deferred, and each run lists them, until the `/sess/rgst` message layer lands |
| `session-crypto.kat` | 34 of 34 on the RustCrypto backend: public keys, shared secrets and stream keys on P-256 and P-521, and the key signature |
| Differential against the C core | Packet crypto, FEC, the control plane, registration and seed decoding, 800 to 5,000 generated cases each. The demuxer runs 300 hostile streams, half with real crypto, and agrees event for event and counter for counter: 7,300 frames, 3,000 loss reports, 4,900 authentication failures |
| Mutation check | Changing one KDF constant fails 54 control lines, and one account-wrap constant fails 16 account lines |

The first differential run failed, and the cause was the harness. Test threads decoded FEC in the C core
at the same time, and `fec_reed_solomon_recover_source` keeps its matrices in `static` buffers, as its source
warns. `ripcord-diff` now holds one lock around every call into C. The Rust recovery's scratch belongs to
its caller, so it has no such limit.

## Phase 2, through Takion, measured (2026-09-26)

| What | Result |
|---|---|
| `control-proto.kat` | 60 of 60 for `sessionreq` and `sessionreply`. The 5 `launchspec` lines wait for the `/sess` layer |
| Captured vectors | The INIT, three DATA packets, both SACKs and the PROTOCOL_VERSION_REQUEST and echo-command payloads all rebuild byte for byte |
| Differential against the C core | 20,000 generated cases each for the protobuf codec, the chunk parsers and the 9303 wire; 500 sealer sequences; and 200 scripted-console sessions of 60 datagrams against `fake_dgram_console.h` |
| CryptoKit | `session-crypto.kat` through the engine on CryptoKit gives results identical to RustCrypto (`EngineKeyAgreementTests`) |
| Takion connection | Scripted-peer tests cover the handshake with retries and failure, DATA in place of COOKIE_ACK, fragmentation, per-channel reassembly, re-SACK on reordering, verbatim retransmission, Karn, the TSN wrap, and a full sealed session with key agreement |

The differential runs found two C bugs, both fixed in `libripcord`. The C scripted console read two bytes
past a short HELLO_ECHO chunk. `takion_control_parse_protocol_version_ack` accepted a field-0 tag that every
other C parser, and Google.Protobuf, refuse.

## Discovery, wake and /sess, measured (2026-09-26)

| What | Result |
|---|---|
| `rendezvous-control.kat` | 4 of 4: both cases' /sess/init and /sess/ctrl byte for byte with the .NET session's own requests |
| `account-pairing.kat` | 121 of 121, now with all 8 `accountrgst` exchanges: body, transport key and the opened pairing record |
| `control-proto.kat` | 65 of 65, now with the 5 launch specs |
| Deferred vector lines | none |
| Differential against the C core | Discovery replies, wake credentials and payloads, the field plaintexts, control frames and /sess responses agree across 5,000 to 20,000 generated cases each, with the chosen differences excluded |
| Mutation check | Changing RP-SupportCmd fails the rendezvous vectors; changing one launch-spec literal fails the launch-spec vectors |

The comparison found two more C bugs, both fixed. `halyard_regist_parse_record` checked `rc_hex_decode`
for 0 when failure is `(size_t)-1`, accepting a malformed key with a length of `SIZE_MAX`.
`halyard_regist_split_response` never read an `RP-Application-Reason` sent as the last header.

## STUN, 9303 and the rendezvous route, measured (2026-09-26)

| What | Result |
|---|---|
| `dgram-transport.kat` | 177 of 177: the initiator, responder and wrap transcripts, every send and event byte for byte |
| Differential against the C core | 20,000 generated STUN messages (over 3,000 carrying an address), 10,000 candidate choices with their address parses, and 300 generated operation sequences of 80 steps on both associations, sharing the transcripts' counting random source |
| Control plane | Scripted-console tests run /sess/init, the reopened connection, /sess/ctrl with RP-ConPath 3 and the padded Host, a heartbeat answered on the kept connection, and the console's close. A missing nonce stops before /sess/ctrl |
| Mutation check | Changing the opener's request word fails both the transcripts and the association differential |

The route's order, which the connect sequence will drive: STUN on the control leg, then signaling (the
host's), then `Channel::begin` between our OFFER and our ACCEPT, an optional `Exchange` carrying /sess/rgst,
then `ControlPlane`. The A/V leg repeats STUN and signaling for the media OFFER, and `Channel::establish`
on the media socket is the hole punch before the probe, the PROBE_REPORT (`ctrl::probe_report_plaintext`,
sent through `ControlSession::send_field`), the STREAM_READY wait and Takion.

## The connect sequence, measured (2026-09-26)

| What | Result |
|---|---|
| LAN, scripted | The arm probe, /sess/init and /sess/ctrl over TCP, senkusha, Takion, key agreement on the real crypto, STREAM_INFO, the IDR latch, the console's heartbeats answered, loss reports, input, sealed video, and the goodbye (`connect::tests`) |
| LAN, failures | A refused and then accepted passcode, a stored passcode, a cancelled one, no SESSION_ID, a console refusing the stream with a reason, a console hanging up mid-stream, a stray datagram, the declared MTU |
| Rendezvous, scripted | Prepare, begin, /sess over the 9303 association, the A/V leg's prelude, SESSION_ID, senkusha and the stream on that one socket, PROBE_REPORT and STREAM_READY, a stray dropped, and the polite Close |
| Real sockets | `ripcord-net`'s loopback test runs the LAN sequence over `std::net` on 127.0.0.1 to five video frames and the goodbye, in about 0.35 s |
| Mutation check | Encrypting the launch spec at counter 1 fails seven of the nine connect tests; one sign-in attempt instead of five fails the refusal test |

The scripted LAN console (`testing::scripted_lan_console`) computes the console's side of every
derivation from what the client sent, so a client that gets a key wrong fails there as it would on a
console. These tests are scripted; the hardware runs are in the journal from 2026-09-26.

## Fuzzing and Miri, first runs (2026-09-26)

| What | Result |
|---|---|
| Nine targets, 60 s each on the M4 Max (2026-09-26) | 0.4 to 5 million runs per target. One finding: the SRCH reply parser sliced its first line at byte 8 as a string, and a multi-byte character straddling that byte panicked. Any datagram on the discovery port could do it. Fixed, with the fuzzer's input as a regression test, and the target then ran clean for 4.7 million runs |
| `derive`, 60 s on the M4 Max (2026-09-26) | 716,176 runs, clean, with every round-trip assertion holding. It generates its two key pairs once rather than per input, which took it from 22,000 runs a minute to that |
| Miri over `ripcord-ffi`'s unit tests | Clean on the portable crypto backends, all seven tests. On the hardware backends Miri stops inside `sha2`'s ARMv8 intrinsics, which is not the engine's code |
