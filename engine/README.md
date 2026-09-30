# engine — the Rust protocol engine

The protocol engine the first-class clients are moving to. Why it exists, what it will cover and in what
order are in [`docs/engine-plan.md`](../docs/engine-plan.md). This file covers the tree, the build and
where Phase 1 stands. What is still open is in [`ROADMAP.md`](../ROADMAP.md).

**Status: Phase 2, through the connect sequence.** In `ripcord-proto`:
- the stream plane (framing, the stream key schedule, packet crypto, FEC, the demuxer);
- the crypto and Halyard derivations;
- key agreement behind the `Ecdh` trait, with RustCrypto and CryptoKit backends;
- Takion: framing, the control protobuf codec, the sealer, the session negotiator and a sans-IO
  connection;
- discovery (SRCH), wake and the arm probe;
- the /sess control plane: registration on both routes and the pairing record, /sess/init and /sess/ctrl
  with their encrypted fields, the launch spec, and the binary control channel as a sans-IO session;
- STUN, the candidates, the 9303 association and the rendezvous control plane over it;
- controller input;
- **the connect sequence and the running session** (`connect::Session`), LAN and rendezvous, from the
  arm probe to video, with scripted consoles for both routes.

`ripcord-net` runs that session on `std::net` sockets on the caller's thread, and `ripcord-ffi` exports it
as the client ABI (`ripcord_client_*`), which Swift and .NET both drive end to end. Ported from `libripcord/`
and cross-checked against the .NET reference, which wins where the two differ unless the reasons below
say otherwise. It first ran against a console on 2026-09-26 (docs/journal.md, "The Rust engine's first session on
hardware"). The Mac runs on this engine since that day (Phase 3's relink); the dotnet client still runs its
managed engine.

## Layout

| Path | What it is | `unsafe` |
|---|---|---|
| `ripcord-proto/` | The protocol as sans-IO state machines. No sockets, no clock, no threads | forbidden |
| `ripcord-ffi/` | The C ABI: every export. Its `build.rs` generates `ripcord.h` (cbindgen) and `NativeMethods.g.cs` (csbindgen) into `target/include/` | the only crate that uses it |
| `ripcord-kat/` | Runs the `.kat` files `ProtocolLab vectors` generates, unchanged | forbidden |
| `ripcord-net/` | The I/O driver: runs `connect::Session` on `std::net` sockets (plus `socket2` for the receive buffer), on the caller's thread, with the C client's threading contract | forbidden |
| `ripcord-diff/` | Differential tests: builds the C core from `libripcord/` with the `cc` crate and runs it beside the Rust engine on generated inputs | in its wrappers only: test tooling, never linked into a host |
| `hosts/dotnet/` | The .NET harness: the engine through its generated C# bindings, a differential run against the managed engine, and the benchmark on both | — |
| `deny.toml` | The dependency policy `cargo deny check` enforces | — |

`ripcord-ffi`'s `test-support` feature adds `ripcord_kat_run` (a vector file through any `Ecdh` backend) and
the scripted console, for host test suites. `ripcord-proto`'s `scripted-console` feature exposes the
console to other crates. Neither belongs in a shipping engine. A host supplies its platform's key agreement
through `RipcordEcdhBackend`; the Mac's is `src/Ripcord.Mac/RipcordKit/Engine/CryptoKitEngineECDH.swift`.

`fuzz/` holds the cargo-fuzz targets, one per surface as `libripcord/tests/fuzz` has them, plus `connect`,
which drives the whole connect machine with arbitrary host events (the C core cannot be fuzzed that way,
since it owns its sockets), and `derive`, which feeds the crypto and Halyard derivations arbitrary peer
points, registration contexts, selectors and counters, and checks that each inverse pair round-trips. It is its own workspace and needs nightly:

```sh
cargo +nightly fuzz run connect -- -max_total_time=60      # from engine/fuzz
RUSTFLAGS='--cfg sha2_backend="soft" --cfg aes_backend="soft"' cargo +nightly miri test -p ripcord-ffi --lib
```

`.github/workflows/engine-nightly.yml` runs both daily, each target for five minutes with a corpus kept
between runs. Miri runs against RustCrypto's portable backends: its aliasing model rejects the pointer use
inside their hardware intrinsics, which is theirs; the engine's own `unsafe` is all in `ripcord-ffi`, and
passes. `demux::tests::random_packets_never_panic` stays as a stable-Rust sweep in the ordinary tests.

**The interop constants are generated, never copied.** `ripcord-proto/build.rs` reads the one committed
bundle and writes a Rust module into `OUT_DIR`, with `gen_constants.py`'s checks. `ripcord-diff` builds
the C core's copy the way the C core does, with `gen_constants.py` itself. `Client-Type` is read the same
way, from `HalyardRegistrationMessage.ClientTypeHex` at build time, so the engine is not a third home for
it; `BundledInteropConstantsTests.ClientType_RustEngineDerivesItFromTheReference` checks both that and
that no file under `engine/` carries the value.

**An engine without them is one feature.** `interop-constants`, on by default in `ripcord-proto`,
`ripcord-net` and `ripcord-ffi`, is what reads the bundle. Built with `--no-default-features`, `build.rs`
does not open it and every table is `None`. The derivations then report their family absent, as they
already did for a bundle without PS4. A connect ends before reaching the console, with a log line saying
why, and `ripcord_interop_constants_bundled()` returns false. This is the counterpart of .NET's
`-p:BundleInteropConstants=false` (`NOTICE`), and the Mac build's `RIPCORD_BUNDLE_INTEROP_CONSTANTS = NO`
selects it. On 2026-09-27 the release library built both ways was searched for the tables' bytes: all four
were in the default build and none in the other. The test that proves it runs by name, since every other
test needs the constants: `cargo test -p ripcord-proto --no-default-features --lib bundle_tests`.

The generated bindings are never committed. Nobody edits them: change `ripcord-ffi/src/lib.rs`, and
the next `cargo build` rewrites both.

## Building and testing

A stable Rust toolchain (`rustup`), and the .NET 10 SDK for the vectors and the harness. From the
repository root:

```sh
# The known-answer vectors, generated from the .NET reference (never committed)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

cd engine
cargo test --workspace --release      # unit tests, every vector file, and the differential runs against C
cargo clippy --workspace --all-targets -- -D warnings
cargo deny check
cargo run --release --example packet_bench -p ripcord-proto

# The engine from .NET: layout check, differential against HalyardPacketCrypto, demux callbacks, bench
dotnet run -c Release --project hosts/dotnet/Ripcord.Engine.Harness
```

`ripcord-diff` needs Python 3 and a C compiler as well. Without the vectors, each vector test prints `SKIP` and passes, like the .NET suite's capture-backed
tests. `cargo run -p ripcord-kat` fails instead, because running it without vectors is a mistake.

The Mac lab (`src/Ripcord.Mac`) builds this engine itself: RipcordKit's "Build the Rust engine" phase
runs cargo, and `ripcord-lab bench` times both engines from the same Swift workload.

### On Windows, for the figure the gate still needs

Phase 1 wants the per-packet cost on Windows x64 and ARM64, where the C core was never measured. On
each machine, with `rustup` and the .NET 10 SDK installed:

```sh
dotnet run --project tools/Ripcord.ProtocolLab -c Release -- vectors
cd engine && cargo test --workspace --release
dotnet run -c Release --project hosts/dotnet/Ripcord.Engine.Harness
```

The harness prints the Rust engine and the managed engine side by side. The managed engine is what the
Windows client ships today, so on Windows that pair is the comparison that matters.

## Phase 1, measured (2026-09-25, M4 Max, macOS 27)

| What | Result |
|---|---|
| `stream-crypto.kat` | 65 of 65: all 9 `gmac`, 4 `streamkdf`, 12 `packetnonce` and 12 `packettag` lines |
| Differential against `HalyardPacketCrypto` | 80 key positions × 4 checks (CTR, both seal directions, the control AAD rule, tamper), across rotation windows and the 32-bit edge |
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
- **`opt-level = "z"`** saves 47 KB and costs speed on the per-packet path this README's gate measures.
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
at the same time, and `fec_reed_solomon_decode` keeps its matrices in `static` buffers, as its source
warns. `ripcord-diff` now holds one lock around every call into C. The Rust decoder's scratch belongs to
its caller, so it has no such limit.

## Takion: where the port follows .NET and where it follows C (2026-09-26)

The C core is a port of the .NET reference, and the two differ in places. A side-by-side read of both
found 27 differences. For each one the Rust port follows .NET, unless C is strictly safer or .NET is
wrong:

| Topic | Rust follows | Why |
|---|---|---|
| Local verification tag | .NET: the host's CSPRNG | C uses a clock tick |
| INIT_ACK checks | .NET: our tag, base type 0, length at least 52 | C checks neither tag nor base type |
| DATA that proves establishment | C: processed | .NET discards it and waits for the peer to retransmit |
| Tag check and GMAC verification after the handshake | C | Defences .NET lacks. Verification starts in counting mode |
| Incoming SACK length | .NET: at least 16 | |
| SACK clearing across a TSN wrap | C: a full serial-number scan | .NET's sorted-key scan leaves pre-wrap chunks unacknowledged forever (roadmap) |
| RTT | .NET: seeded, then EWMA alpha 0.125, Karn's rule | The stats need it; C has none |
| Retransmission | C: each chunk once it is 300 ms old | .NET resends everything on every tick |
| In-flight chunks and message size | .NET: unbounded, except a 64 KB message cap | C's 32 and 2048 are console memory limits |
| Reassembly | .NET: per channel | C's single slot rests on a premise its own header contradicts |
| Curve for an unvalidated version | .NET: an error | C falls back to P-256, which .NET removed as a guess |
| SESSION_REPLY checks | C: version, required fields, signature length, curve | .NET does not check `versionAccepted` (roadmap) |
| Session key | .NET: the caller's, with the observed literal as the default | |

Sequencing that belongs to the connect sequence, such as version negotiation and its timeouts, is decided
when that layer lands.

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

## Discovery, wake and /sess: where the port follows .NET and where it follows C (2026-09-26)

The same method as for Takion. A side-by-side read found 26 differences; for each, the Rust port follows
.NET unless C is strictly safer or .NET is wrong.

| Topic | Rust follows | Why |
|---|---|---|
| Wake credential sign | C: signed 32-bit decimal | C cites a PS4 capture with a negative credential; .NET writes it unsigned (roadmap) |
| Wake credential input | .NET's tolerance, refusing instead of throwing | |
| SRCH awake test | an exact `200` token with the CR stripped | .NET misses a bare `200\r`; C prefix-matches `2000` |
| SRCH fields | .NET: host-request-port read, no truncation | Non-ASCII host names are kept as lossy UTF-8 |
| /sess/init Host and version header | .NET's padded Host and `Rp-Version`, with C's LAN forms as options | The console accepted either form (journal, "Two red herrings") |
| A bad or missing RP-Nonce | C: a hard failure | .NET carries on unauthenticated and would send the launch spec in plaintext (roadmap) |
| /sess response parsing | C's digit rules; .NET's last-wins for repeated headers | .NET waits forever on a malformed status and throws on a negative length |
| RP-OSType and device id | the caller's | The engine runs on any OS; a non-Windows host should send 10.0 |
| Pairing record | .NET: trimmed fields, last wins, a non-hex key taken as ASCII | C had two bugs here, now fixed |
| Np-AccountId | .NET: whitespace and `+` allowed, overflow falls back to UTF-8 | An empty id is refused, as in C |
| Login passcode | C: at most 32 digits, refused rather than thrown | |
| Control-frame length | C: overflow guarded, and the resynchronisation C's hardware runs needed | .NET's int cast can stall or throw |
| Control frame types | .NET's fuller list | |

Launch-spec MTU and RTT clamping, and the RP-Nonce and launch-spec order of the connect sequence, belong to
that layer when it lands.

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

## STUN, 9303 and the rendezvous route: where the port follows .NET and where it follows C (2026-09-26)

The 9303 association matches rule for rule in the two references, and `dgram-transport.kat` holds both
to it byte for byte, so the association has no row here. The differences are in what surrounds it.

| Topic | Rust follows | Why |
|---|---|---|
| STUN parser | `StunMessage`, which C ports: XOR-MAPPED (both codes) before MAPPED, IPv4 and IPv6, keeping what was found before an overrun | .NET's production path, `StunReflexiveAddress`, reads XOR-mapped IPv4 only. The lenient parser is a superset of it, and an IPv6 mapping is never offered (`Address::ipv4`) |
| STUN timing | 3 attempts of 500 ms per server, a fresh id each time, as `StunClient` and C do | `StunReflexiveAddress` makes one 3 s attempt per server; a host that wants that passes it |
| STUN socket | the host's choice: the gatherer never names a socket | C asks on the socket that will carry the traffic, .NET on a throwaway socket it binds again later |
| RFC 3489 16-byte ids | not carried | C keeps them for a port; no Halyard route uses them |
| Candidate addresses | C: strict dotted-quad IPv4 | .NET's `IPAddress.TryParse` accepts IPv6, short and octal forms, and then maps the address to IPv4 |
| Candidate fallback | C: the choice is always an index, and the caller applies consoleHost:9303 when it does not parse | .NET's transport and its ACCEPT can pick different candidates when none parses (roadmap) |
| Inbound bound | C: 16 KB, and a chunk that would overflow it is left unacked so the peer resends | .NET buffers without limit |
| An oversized payload or cookie echo | C: a status or an `Unhandled` event | .NET throws out of `Send` or `OnDatagram` |
| A close arriving with data | C: latched | .NET's `ReceiveBytesAsync` loses it |
| /sess response wait | .NET: each stage's own 30 s | C waits 5 s from the start of each response. The overall deadline belongs to the connect sequence |
| Keep-alive after 30 s of silence | C: the channel keeps running | .NET's loop ends silently when a read times out (roadmap) |
| PROBE_REPORT slots | .NET: the measured MTU and RTT, RTT clamped to 0–1000 ms | C sends a fixed 1454 and the version exchange's RTT. Which slot the console reads is `[X]`: .NET found real values, all 500 and all 0 indistinguishable over three sessions |
| Opener's request word | both: 0x40, [X] | In no capture; the vendor client sends a small growing counter. The two references and the vectors change together |

Sign-in policy (re-prompting on silence, a stored passcode, what an accepted passcode without a
SESSION_ID means) and peer filtering belong to the connect sequence, not to this layer.

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

## The connect sequence: where the port follows .NET and where it follows C (2026-09-26)

`connect::Session` is the C client's sequence (`halyard_client.c`) as a sans-IO machine. Where C and
.NET differ, these are the choices.

| Topic | Rust follows | Why |
|---|---|---|
| Control-plane deadline | .NET: 20 s on the LAN and 60 s on rendezvous, over the arm probe, both TCP connects and /sess | C has no overall deadline, cannot cancel the LAN open, and times each /sess response from the start |
| Stream bring-up | .NET: one 35 s box from the Takion handshake to STREAM_INFO's ack | C gives SESSION_REPLY 5 s and STREAM_INFO 10 s, and no box around the handshake |
| PROTOCOL_VERSION_ACK on the stream | C: 5 s, then fall back to the offered version | .NET fails. C's path has run on hardware, but no console has been seen to withhold the version, so the fallback itself is `[X]` |
| SESSION_ID before Takion on the LAN | C: required, inside a 20 s prompt window | The PS3 and 3DS ports found that a console without it drops every INIT; .NET's claim otherwise is [X] |
| Sign-in | C: a stored passcode first, silence re-submits the same digits, an unknown verdict byte stops, 30 s for SESSION_ID after an accepted passcode on the LAN | Each from a C port's hardware run. .NET re-asks on silence and treats an unknown byte as accepted |
| Accepted passcode, no SESSION_ID, on rendezvous | C: carry on to the A/V leg ([X], one wake-from-rest run) | |
| A late prompt during the media wait | C's handling, [X], but without insisting on SESSION_ID after it | C insists there and not after its own gate, which contradicts itself |
| Peer filtering | .NET: every socket reads only the console's endpoint | C filters on rendezvous only |
| The console hanging up | C: a Takion DISCONNECT (with its reason) or a closed control session ends the session | .NET does the same since 2026-09-26 |
| Declared MTU | .NET: the MTU the senkusha probe confirmed in both directions, else the interface MTU less 46, clamped to 530–1454, else 1454 | C always declares 1454 |
| Declared RTT | .NET: the echo probe's least round trip when a majority of its ten pings came back, else the least of senkusha's version and session round trips, rounded | C truncates, so a sub-millisecond LAN reads as a measured 0, and has no echo probe |
| RP-StartBitrate and RP-StreamingType | .NET: the configured bitrate, and 0 | C reads both from the pairing record |
| Senkusha | .NET: the two legs, the echo probe, the MTU probe down then up (the upstream test always closed), then DISCONNECT and the socket closed, all inside the 8 s box | C runs the two legs only |
| Keyless senkusha SESSION_REQUEST | .NET: the encrypted key present and empty | The vendor sends it so (`22 00` in cap53 and cap54, `[W]`). C sent four zero bytes until 2026-09-26, when both changed to match |
| Incoming control GMAC | C: verified and enforced | .NET never verifies |
| IDR | C: the latch armed at the start and re-asked every 200 ms until a keyframe arrives | C's b141 and b124 |
| Loss | .NET: CORRUPT_FRAME for each lost range, then the keyframe request | C sends none |
| Adaptive bitrate | .NET's ladder (bitrate cuts, 720p, 540p, half frame rate), stepped down on 2% loss and up after 12 s clean, capped by a low battery or throttling | C has none. The target reaches the console only in CONNECTION_QUALITY, opt-in because its unit is [X] |
| Control echo probe | .NET's, opt-in: after senkusha on the LAN, after sign-in on rendezvous | A diagnostic of the control crypto; C has none |
| Input | .NET's writer (state on a stick change or every 200 ms), polled every 4 ms as C polls | |
| Input HISTORY window | .NET: four earlier events repeated besides this poll's | C repeats four events in all. `ripcord-diff`'s `input_diff` checks C's window is cut from the same polls |
| Order of a poll's simultaneous events | .NET: L3 and R3 before Options, Create and PS | C puts L3 and R3 after PS. Each event stands alone on the wire, so the order should not matter to the console; `[X]` until a capture has two of those in one packet. Found by `input_diff` |
| Rest | C: only on an explicit disconnect | .NET does the same since 2026-09-26 |
| Teardown on rendezvous | C: the polite 9303 close before any socket closes | A console never sent the Close keeps the session live |
| fps, HDR | C: 30 or 60; HDR only with HEVC | A third rate's answer is [X]; HDR is an HEVC profile |

## The connect sequence, measured (2026-09-26)

| What | Result |
|---|---|
| LAN, scripted | The arm probe, /sess/init and /sess/ctrl over TCP, senkusha, Takion, key agreement on the real crypto, STREAM_INFO, the IDR latch, heartbeats, congestion, input, sealed video, and the goodbye (`connect::tests`) |
| LAN, failures | A refused and then accepted passcode, a stored passcode, a cancelled one, no SESSION_ID, a console refusing the stream with a reason, a console hanging up mid-stream, a stray datagram, the declared MTU |
| Rendezvous, scripted | Prepare, begin, /sess over the 9303 association, the A/V leg's prelude, SESSION_ID, senkusha and the stream on that one socket, PROBE_REPORT and STREAM_READY, a stray dropped, and the polite Close |
| Real sockets | `ripcord-net`'s loopback test runs the LAN sequence over `std::net` on 127.0.0.1 to five video frames and the goodbye, in about 0.35 s |
| Mutation check | Encrypting the launch spec at counter 1 fails seven of the nine connect tests; one sign-in attempt instead of five fails the refusal test |

The scripted LAN console (`testing::scripted_lan_console`) computes the console's side of every
derivation from what the client sent, so a client that gets a key wrong fails there as it would on a
console. These tests are scripted; the hardware runs are in the journal from 2026-09-26.

## The client ABI (2026-09-26)

`ripcord_client_*` in `ripcord-ffi/src/client.rs` is `halyard_client.h`'s contract, so the Mac can move
engines by relinking: one host thread calls connect, then pump until the session ends; every callback runs
on that thread; buffers are borrowed for the call; the pad, the passcode, the media answer and the
commands are pulled. The differences:

| `halyard_client.h` | The Rust ABI | Why |
|---|---|---|
| Storage the host sizes and places (`struct_size`, `init`) | An opaque handle from `ripcord_client_new`, freed with `ripcord_client_free` | No hot-path allocation was a console-port constraint; the first-class hosts have a heap |
| The core's own CSPRNG and ECDH | `RipcordRandom` (required) and `RipcordEcdhBackend` (null: RustCrypto) | The engine reads no entropy source of its own, and key agreement is the platform's |
| `halyard_client_fds` and a deadline out | `ripcord_client_pump(client, max_wait_ms, &alive)` waits on the sockets itself | `std` has no portable readiness API, and a host only ever slept or polled on them |
| A `halyard_pairing_record` | Its fields in `RipcordClientConfig` | The engine does not parse pairing files |
| A process-wide log sink | The session's own `log` callback | engine-plan.md rule 9 |
| Results and stats | The same facts, plus the stream's SACK round trip in the stats | .NET reports it; C has none |

Around the session, `ripcord-ffi/src/pairing.rs` exports the rest of what the Mac takes from the C core:
the SRCH probe and reply parser, the wake payload (the host owns those sockets), the account-id
normaliser (ported from C, which alone has one, and differentially checked against it on 20,000
generated inputs), the account key material and seed recovery, PIN registration over TCP 9295
(`ripcord-net::pairing`, with C's arm probe and read-until-close), and account registration on a
rendezvous client's control leg. A registration returns a `RipcordPairingRecord`; saving it is the host's.
A CSPRNG that refuses a draw fails the registration rather than supplying zeroes.

`RipcordClientConfig`'s port fields and `no_arm_broadcast` exist for tests. The `test-support` feature adds
`ripcord_loopback_console_*`, the scripted LAN console on loopback sockets, so each host's suite runs a real
session.

| What | Result |
|---|---|
| Rust, through the ABI | `ripcord-ffi/tests/client_abi.rs`: a LAN session with a passcode, video, stats, input and the goodbye; bad configs refused; layouts reported |
| .NET | The harness hosts a session through `[UnmanagedCallersOnly]` callbacks and a GCHandle, as Phase 4 will, and checks 16 struct layouts |
| Swift | `EngineClientTests`: the same session from `@convention(c)` callbacks, with CryptoKit doing the key agreement and `SecRandomCopyBytes` the randomness |
| Pairing | `pairing_abi.rs`: discovery, wake, the account id, the seed, and PIN registration against the loopback console, whose `/sess/rgst` answer is the console's real side of the derivation |

## The .NET behaviours the connect sequence gained (2026-09-26)

| What | Result |
|---|---|
| Senkusha probes | Scripted-console tests confirm the capture's order (echo on, echo off, the MTU command, the client MTU on and off), ten echoed pings and the echoed MTU packet, the confirmed MTU, and that a console ignoring every probe costs time and never the session. The loopback test through the C ABI measures both over real sockets. On the rendezvous route they run on the A/V leg |
| A bug the tests found | The first ping went out before the echo-on command it depended on, because the command waited in the association's queue; every probe datagram now follows whatever the association has queued |
| CORRUPT_FRAME | A gap in the frame index sends the lost range, and the IDR latch asks for the repair |
| Adaptive ladder | Unit tests hold the ladder, the cooldown, the clean streak, a tiny window's silence and the power caps to .NET's. CONNECTION_QUALITY goes out only when enabled: the first at once, then on changes and the 2 s refresh |
| ABI | Version 5: the two switches in the config, the ladder's target in the stats, the probes' results in the outcome, and `ripcord_client_set_power` |

## Fuzzing and Miri, first runs (2026-09-26)

| What | Result |
|---|---|
| Nine targets, 60 s each on the M4 Max (2026-09-26) | 0.4 to 5 million runs per target. One finding: the SRCH reply parser sliced its first line at byte 8 as a string, and a multi-byte character straddling that byte panicked. Any datagram on the discovery port could do it. Fixed, with the fuzzer's input as a regression test, and the target then ran clean for 4.7 million runs |
| `derive`, 60 s on the M4 Max (2026-09-26) | 716,176 runs, clean, with every round-trip assertion holding. It generates its two key pairs once rather than per input, which took it from 22,000 runs a minute to that |
| Miri over `ripcord-ffi`'s unit tests | Clean on the portable crypto backends, all seven tests. On the hardware backends Miri stops inside `sha2`'s ARMv8 intrinsics, which is not the engine's code |

## Key agreement on Windows (2026-09-26)

The Windows host is .NET, so its backend is .NET's `ECDiffieHellman` (CNG, BCrypt, on Windows) behind the
engine's backend table: `hosts/dotnet/Ripcord.Engine.Harness/PlatformEcdh.cs`, which Phase 4 moves into
`Ripcord.Protocol.Halyard.Native` unchanged. The harness runs `session-crypto.kat` through the engine with it
and compares the result with RustCrypto's. It passes on macOS on Apple's implementation (34 checks), and on
CNG itself on Windows x64 (34 checks, 2026-09-30).
