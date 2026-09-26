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
say otherwise. Nothing here has talked to a real console yet. The Mac runs on this engine since 2026-09-26
(Phase 3's relink); Windows still runs its managed engine.

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

`fuzz/` (cargo-fuzz, which needs nightly) arrives with Phase 2. Until then, `demux::tests::random_packets_never_panic` is a stable-Rust sweep of hostile
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

## The connect sequence: where the port follows .NET and where it follows C (2026-09-26)

`connect::Session` is the C client's sequence (`halyard_client.c`) as a sans-IO machine. Where C and
.NET differ, these are the choices.

| Topic | Rust follows | Why |
|---|---|---|
| Control-plane deadline | .NET: 20 s on the LAN and 60 s on rendezvous, over the arm probe, both TCP connects and /sess | C has no overall deadline, cannot cancel the LAN open, and times each /sess response from the start |
| Stream bring-up | .NET: one 35 s box from the Takion handshake to STREAM_INFO's ack | C gives SESSION_REPLY 5 s and STREAM_INFO 10 s, and no box around the handshake |
| PROTOCOL_VERSION_ACK on the stream | C: 5 s, then fall back to the offered version | .NET fails; falling back is harmless and was run on hardware |
| SESSION_ID before Takion on the LAN | C: required, inside a 20 s prompt window | The PS3 and 3DS ports found that a console without it drops every INIT; .NET's claim otherwise is [X] |
| Sign-in | C: a stored passcode first, silence re-submits the same digits, an unknown verdict byte stops, 30 s for SESSION_ID after an accepted passcode on the LAN | Each from a C port's hardware run. .NET re-asks on silence and treats an unknown byte as accepted |
| Accepted passcode, no SESSION_ID, on rendezvous | C: carry on to the A/V leg ([X], one wake-from-rest run) | |
| A late prompt during the media wait | C's handling, [X], but without insisting on SESSION_ID after it | C insists there and not after its own gate, which contradicts itself |
| Peer filtering | .NET: every socket reads only the console's endpoint | C filters on rendezvous only |
| The console hanging up | C: a Takion DISCONNECT (with its reason) or a closed control session ends the session | .NET notices neither, and relies on its watchdog (roadmap) |
| Declared MTU | .NET: the interface MTU less 46, clamped to 530–1454, when the host knows it; otherwise 1454 | C always declares 1454 |
| Declared RTT | .NET: the least of senkusha's version and session round trips, rounded | C truncates, so a sub-millisecond LAN reads as a measured 0. .NET's echo probe is not ported yet |
| RP-StartBitrate and RP-StreamingType | .NET: the configured bitrate, and 0 | C reads both from the pairing record |
| Senkusha | C's two legs, then .NET's DISCONNECT and the socket closed | The echo and MTU probes are not ported yet (roadmap) |
| Keyless senkusha SESSION_REQUEST | C: a 4-byte zero encrypted key | .NET sends it empty, though its negotiator notes the console drops a SESSION_REQUEST without one (roadmap) |
| Incoming control GMAC | C: verified and enforced | .NET never verifies |
| IDR | C: the latch armed at the start and re-asked every 200 ms until a keyframe arrives | C's b141 and b124. CORRUPT_FRAME is not sent yet |
| Input | .NET's writer (state on a stick change or every 200 ms), polled every 4 ms as C polls | |
| Rest | C: only on an explicit disconnect | .NET rests on every teardown when the setting is on (roadmap) |
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
console. None of this has run against hardware.

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
