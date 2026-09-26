# engine — the Rust protocol engine

The protocol engine the first-class clients are moving to. Why it exists, what it will cover and in what
order are in [`docs/engine-plan.md`](../docs/engine-plan.md). This file covers the tree, the build and
where Phase 1 stands. What is still open is in [`ROADMAP.md`](../ROADMAP.md).

**Status: Phase 2, through discovery, wake and /sess.** In `ripcord-proto`:
- the stream plane (framing, the stream key schedule, packet crypto, FEC, the demuxer);
- the crypto and Halyard derivations;
- key agreement behind the `Ecdh` trait, with RustCrypto and CryptoKit backends;
- Takion: framing, the control protobuf codec, the sealer, the session negotiator and a sans-IO
  connection;
- the 9303 datagram wire codec, and the scripted console the plan asks for;
- discovery (SRCH), wake and the arm probe;
- the /sess control plane: registration on both routes and the pairing record, /sess/init and /sess/ctrl
  with their encrypted fields, the launch spec, and the binary control channel as a sans-IO session.

Ported from `libripcord/` and cross-checked against the .NET reference, which wins where the two differ
unless the reasons below say otherwise. Nothing here talks to a console yet. The Mac and Windows clients
still run their existing engines; the Mac links this one alongside the C core, for the benchmark and the
CryptoKit vector test.

## Layout

| Path | What it is | `unsafe` |
|---|---|---|
| `ripcord-proto/` | The protocol as sans-IO state machines. No sockets, no clock, no threads | forbidden |
| `ripcord-ffi/` | The C ABI: every export. Its `build.rs` generates `ripcord.h` (cbindgen) and `NativeMethods.g.cs` (csbindgen) into `target/include/` | the only crate that uses it |
| `ripcord-kat/` | Runs the `.kat` files `ProtocolLab vectors` generates, unchanged | forbidden |
| `ripcord-diff/` | Differential tests: builds the C core from `libripcord/` with the `cc` crate and runs it beside the Rust engine on generated inputs | in its wrappers only: test tooling, never linked into a host |

`ripcord-ffi`'s `test-support` feature adds `ripcord_kat_run` (a vector file through any `Ecdh` backend) and
the scripted console, for host test suites. `ripcord-proto`'s `scripted-console` feature exposes the
console to other crates. Neither belongs in a shipping engine. A host supplies its platform's key agreement
through `RipcordEcdhBackend`; the Mac's is `src/Ripcord.Mac/RipcordKit/Engine/CryptoKitEngineECDH.swift`.
| `hosts/dotnet/` | The .NET harness: the engine through its generated C# bindings, a differential run against the managed engine, and the benchmark on both | — |
| `deny.toml` | The dependency policy `cargo deny check` enforces | — |

`ripcord-net` (sockets and the pump loop) and `fuzz/` (cargo-fuzz, which needs nightly) arrive with
Phase 2. Until then, `demux::tests::random_packets_never_panic` is a stable-Rust sweep of hostile
packets.

**The interop constants are generated, never copied.** `ripcord-proto/build.rs` reads the one committed
bundle and writes a Rust module into `OUT_DIR`, with `gen_constants.py`'s checks. `ripcord-diff` builds
the C core's copy the way the C core does, with `gen_constants.py` itself. `Client-Type` is read the same
way, from `HalyardRegistrationMessage.ClientTypeHex` at build time, so the engine is not a third home for
it; `BundledInteropConstantsTests.ClientType_RustEngineDerivesItFromTheReference` checks both that and
that no file under `engine/` carries the value.

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
| Mac lab | Links through the generated header; the RipcordKit tests and EcdhKat still build and pass |
| .NET | Links through the generated bindings: `[UnmanagedCallersOnly]` callbacks, a `GCHandle` user pointer |
| Size | `libripcord.dylib` 386 KB stripped. `ripcord-lab` grows from 1.10 MB to 2.35 MB stripped, which is more than the engine's own size and still to be explained |

All three benchmarks run one workload: `PacketCryptoBenchmark.swift`'s packets, 1,426 bytes each,
spaced so that every packet lands in its own GMAC rotation window. That is the worst case. At wire
spacing Rust takes 0.54 µs (`packet_bench`), because a rotation window then serves many packets.

Most of the gap is hardware AES and PMULL, which RustCrypto detects at run time, against the C core's
portable table implementation. The rest is structure. The CTR cipher borrows a key schedule expanded
once, where the C core expands the key on every packet. The GMAC cache is keyed by rotation window,
where the C core derives the key with a SHA-256 on every packet just to check its cache.

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
| /sess/init Host and version header | .NET's padded Host and `Rp-Version`, with C's LAN forms as options | Both are proven on hardware |
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
| PROBE_REPORT slots | .NET: the measured MTU and RTT, RTT clamped to 0–1000 ms | C sends a fixed 1454 and the version exchange's RTT. .NET notes the console ignores both |
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
