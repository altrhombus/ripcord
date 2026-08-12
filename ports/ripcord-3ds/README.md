# ripcord-3ds

A PS5 Remote Play client for modded Nintendo 3DS hardware, in C, sharing Ripcord's protocol
specification but none of its code.

**Status: negotiates real stream keys with a real PS5. Nothing decodes yet.** As of 2026-08-12
`ripcord-3ds-connect.3dsx` runs the whole connect flow against real hardware — discovery, `/sess/init` →
`/sess/ctrl`, the binary control channel, senkusha, the Takion handshake on UDP 9296, and the
`SESSION_REQUEST`/`SESSION_REPLY` ECDH exchange — and derives the per-direction AES keys and IVs the A/V
stream is encrypted with. Everything from the control-plane crypto down to the key agreement is now
confirmed against a console rather than only against our own .NET implementation.

What is missing is the media pipeline: no MVD decode, no Y2R, no Opus, no input, and no GMAC sealing or
STREAM_INFO ack yet. The stream framing/FEC/demux layer is implemented and passes 3,242 host known-answer
cases but has never seen a real packet, because nothing has asked the console to start sending one. There is no video, no audio and no input. See
[Where this stands](#where-this-stands).

## Why this exists

Two reasons, and the second matters more than the first.

The obvious one: a PS5 streaming to a handheld you already own is a good time.

The real one: **this is a completeness test for `docs/protocol/`.** Ripcord's independence claim rests on
that spec having been derived from this project's own captures and analysis. A spec is only as good as its
ability to produce a working implementation, and so far it has produced exactly one — by the people who
wrote it, in the language they wrote it alongside. Building a second client, in a different language,
against a different OS, on a CPU with a different word order, is the cheapest honest way to find out which
parts of that document are load-bearing and which parts only *look* complete because the author already
knew the answer.

Every place this port had to guess is a defect in the spec. That is the output worth having, and it is why
the port lives in this repository rather than beside it: a "the spec didn't say" finding belongs in the
same tracker as the spec.

## Can the hardware actually do this?

**New 3DS only.** The original 3DS has no video decode hardware and is not a target.

| | Budget | Assessment |
|---|---|---|
| CPU | ARM11 MPCore @ 804 MHz, no crypto extensions | **Measured: ~25 µs per control-field encryption** (HMAC-SHA256 + one AES-128 block) — the control plane is free; the A/V path's AES-128-CTR is a real cost, extrapolated affordable at low bitrate but not yet measured directly |
| Video | MVD hardware H.264 decoder (New 3DS only) | H.264 only — HEVC must be refused at negotiation |
| Colour | Y2R hardware YUV→RGB | The established homebrew path: MVD → Y2R → PICA200 texture |
| Screen | 400×240 | Everything downscales; quality loss is free |
| Wi-Fi | 802.11b/g, 2.4 GHz | **Measured (UDP receive, idle): 2.16% loss at 2 Mbps, 4.33% at 3 Mbps** — not the blocker it was assumed to be, at the bottom rung's actual target |
| Audio | Opus in software + DSP | Affordable |

Wi-Fi was expected to be the binding constraint and is not, on an idle core. An `ftpd` transfer (TCP,
upload) sustained around 10 Mbps, dipping to 8 and peaking near 13; the UDP link test in
[`SETUP.md`](SETUP.md) Phase 2 confirmed the same picture in the actual direction and protocol the A/V
path uses, reaching ~10.4 Mbps goodput at the top of a 1→12 Mbps ramp with modest loss well past the
bottom rung's 1.5–3 Mbps target.

**Toggling a simulated CPU load changes the picture.** At the same 2–3 Mbps target, loss roughly
quadruples (9.68% / 17.93%), and above ~5 Mbps target the busy-CPU goodput flatlines around 3–3.4 Mbps
regardless of how much more the sender pushes. The load used is a crude synthetic proxy, not real MVD/Y2R
decode timing (which doesn't exist yet), so the direction — CPU contention costs real UDP throughput — is
the finding to keep; the specific busy ceiling is not. See Phase 2 in [`SETUP.md`](SETUP.md) for the full
numbers and that caveat.

Controls map more gracefully than expected in places and not at all in others: the touchscreen is a real
DualSense touchpad and the gyro is a real gyro, but the C-stick is a poor right stick, ZL/ZR are digital
so there are no analog triggers, and there is no L3/R3 at all.

### Pairing happens on a PC, not here

Registration needs PSN OAuth and TLS, which is a lot of weight for a handheld and buys nothing: pair with
desktop Ripcord, then copy the pairing record across. `PairedConsole` is deliberately a plain credential
holder rather than a Halyard type, which makes this close to free. It also means the registration
constants never need to exist in this binary — and `tools/gen_constants.py` deliberately does not emit
them.

## Layout

```
source/crypto/      AES-128, SHA-256, HMAC, cipher modes   <- mirrors Ripcord.Core.Net.Crypto
source/halyard/     control KDF, field IV, field ciphers   <- mirrors Protocol.Halyard.Common/Crypto/V1
source/net/         SOC service lifecycle + a minimal TCP client (rc_tcp.c, Phase 4)
source/util/        shared seams: SD-card logging, base64/hex, trim/header-parsing, program-dir resolution
source/app/         on-device crypto smoke test (ripcord-3ds.3dsx)
source/linktest/    Phase 2 UDP link test (ripcord-3ds-linktest.3dsx)
source/discovery/   Phase 3 LAN discovery: SRCH probe/parse + on-device app (ripcord-3ds-discovery.3dsx)
source/session/     Phase 4 /sess/init -> /sess/ctrl exchange + on-device app (ripcord-3ds-session.3dsx)
source/takion/      Phase 5 Takion transport: handshake, DATA/SACK, reassembly + on-device app
                    (ripcord-3ds-takion.3dsx)
source/stream/      stream framing, FEC, demux: A/V header, GF(2^8)/Cauchy Reed-Solomon, packet crypto
                    (GMAC + KDF), frame reassembly - no on-device app yet (see SETUP.md for why)
source/connect/     Phase 6b THE CONNECT FLOW: control -> senkusha -> Takion -> stream keys, the only
                    program that reaches the stream plane (ripcord-3ds-connect.3dsx)
source/crypto/rc_ecdh.*          Phase 6a ECDH seam over mbedtls - the one primitive not implemented here
source/takion/takion_control_proto.*      SESSION_REQUEST/REPLY protobuf (hand-rolled, two messages)
source/takion/takion_session_negotiator.* the stream key agreement (socket-free; driven by source/connect)
source/session/halyard_control_session.*  the control plane as a reusable, pollable object
source/session/halyard_launch_spec.*      the launchSpec JSON - carries handshakeKey to the console
source/util/rc_random.*                   libctru CSPRNG seam; no host implementation, deliberately
tests/              host-side known-answer runner, discovery + session + takion + stream self-tests
tools/              constants generator, UDP link-test sender (host-side)
```

The dependency direction is the same one the .NET side enforces: `halyard/` depends on `crypto/`, never
the reverse, and `crypto/` knows nothing about PlayStation. `net/`, `linktest/` and `discovery/` are a
separate, parallel branch of that graph — they answer network questions, not protocol ones, and depend on
neither `crypto/` nor `halyard/`. `session/` is where the two branches finally meet: it is the first code
in this port to use `halyard/`'s control-field cipher against something an actual console sent back
(`/sess/init`'s `RP-Nonce`), which is the whole reason Phase 4 exists. Within `discovery/` and `session/`,
the wire-format parsers (`halyard_discovery.c`, `halyard_sess_request.c`, `halyard_ctrl_message.c`,
`halyard_sess_fields.c`, `halyard_control_arm.c`) have no socket dependency of their own — see their
headers for why — so they are checked on the host in `tests/discovery_test.c` / `tests/session_test.c`
without any of `net/`'s hardware seam involved. `rc_text.c` (trim / case-insensitive header-name match) is
shared by both `halyard_discovery.c` and `halyard_sess_request.c`, factored out once the second module
needed the exact logic the first already had as private statics. `takion/` follows the same split one
level further: `takion_message.c`/`takion_handshake.c`/`takion_data_chunk.c`/`takion_sack_chunk.c`/
`takion_reassembler.c` are pure (host-tested in `tests/takion_test.c`, no vector file needed — see that
file's header for where each known-answer packet came from), while `takion_reliable_channel.c` is the one
file in this port's whole protocol layer that both owns a socket *and* isn't crypto — it drives the pure
chunk codecs over UDP with retransmit/SACK timing, and like `rc_soc.c`/`rc_tcp.c` has no host test of its
own, only the on-device app.

## Building

### Host tests — no 3DS, no devkitPro, just a C compiler

This is the part that verifies the crypto, and it should be the part you run.

```sh
# 1. Generate the vectors from the .NET implementation (once, from the repo root)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

# 2. Build and run the C against them
make -C ports/ripcord-3ds/tests
```

### The 3DS build

Requires [devkitPro](https://devkitpro.org/wiki/Getting_Started) with devkitARM and libctru. On Windows,
run it from the devkitPro MSYS2 shell — that is where `make` and the ARM toolchain live.

```sh
make -C ports/ripcord-3ds
```

Produces five Homebrew Launcher binaries: `ripcord-3ds.3dsx`, the on-device smoke test in
`source/app/main.c` (round-trips the ciphers on real hardware and reports how long a field encryption
actually costs on an ARM11); `ripcord-3ds-linktest.3dsx`, the Phase 2 UDP link test in
`source/linktest/main.c` (see [`SETUP.md`](SETUP.md) for how to run it against
`tools/udp_link_test_sender.py`); `ripcord-3ds-discovery.3dsx`, the Phase 3 LAN discovery probe in
`source/discovery/main.c` — broadcasts the SRCH probe for both console families and lists every distinct
console that answers within a four-second window; `ripcord-3ds-session.3dsx`, the Phase 4 session probe
in `source/session/main.c` — arms the console's control listener, runs `/sess/init` -> `/sess/ctrl`
against a provisional `pairing.txt` on the SD card (see that file's own header comment for the format;
this is not the real pairing-import feature, which remains unstarted), and answers heartbeats on the
binary control channel that follows; and `ripcord-3ds-takion.3dsx`, the Phase 5 Takion transport probe in
`source/takion/main.c` — drives the SCTP handshake against a `takion.txt`-configured host:port and
exchanges a synthetic message over the reliable channel once established. `make -C ports/ripcord-3ds
linktest`/`discovery`/`session`/`takion` builds just one of the five.

All five (`source/util/rc_log.c`) write everything they print to the top screen into a log file next to
whichever copy of the `.3dsx` produced it — `smoke-test.log` / `linktest.log` / `discovery.log` /
`session.log` / `takion.log` on the SD card — so a run's results can be copied off the card afterward
instead of retyped from a photo of the screen.

All five have been built against a real devkitPro installation and produce valid `.3dsx` files. The
crypto smoke test and the link test have both been booted on real hardware (see "Where this stands"); the
discovery, session and Takion probes have not yet.

### Not part of `Ripcord.slnx`, deliberately

`dotnet build` cannot build this and never will. Sharing a repository with Ripcord buys shared specs,
shared constants and shared test vectors; it does not mean sharing a build system with a cross-compiler
targeting a different CPU and a different OS.

## The interop constants are generated, never copied

`src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` is committed under a specific, narrow
argument in the repository `NOTICE`. That argument is made once, about one file.

This port keeps **no copy**. `tools/gen_constants.py` reads that one committed file at build time and
generates a C translation unit into `build/`, which is gitignored. A second checked-in copy of those bytes
would quietly turn one bounded exception into two, and the second would carry no argument at all.

The same reasoning applies to the vector files: everything under `tests/vectors/` (`control-crypto.kat`,
`stream-crypto.kat`, `session-crypto.kat`, `control-proto.kat`) is generated, gitignored, and regenerated
with one command.

## Verification, and its limits

The vector file is the contract between the two implementations. The .NET side computes the answers, this
side recomputes them, and a disagreement means one of the two misread `docs/protocol/ps5-session-crypto.md`.

171 control-plane vectors currently pass, covering:

- **KDF** — all 32 entries of both lookup tables swept, for both console families, plus cases where the two
  table indices deliberately differ (a port that reuses one index for both agrees on most inputs otherwise)
- **Context key selection** — including the high-band codec cases that win over the version selector
- **Field IV** — counters spanning the 32-bit edge, because a little-endian or 32-bit counter write passes
  every short session and then fails a long one
- **CFB / OFB** — at lengths either side of the block boundary, where CFB's partial-final-block rule bites
- **End-to-end** — nonce and companion in, base64 header value out

The stream plane adds its own files on the same contract — `stream-crypto.kat` (GMAC, the stream KDF,
per-packet nonce/tag) and, since Phase 6a, `session-crypto.kat` and `control-proto.kat`:

- **ECDH** — fixed private scalar to public point, and to shared secret, on both P-256 and P-521. The
  scalars are fixed rather than generated because a key agreement has no known-answer vector otherwise;
  `rc_ecdh_keypair_from_private()` exists in the header for exactly this reason rather than as a
  test-only back door. What is being checked is not mbedtls's curve arithmetic but the code around it —
  curve selection, point layout, and whether the shared X is written at the curve's full width. P-521
  makes that last one hard to get away with: its X is 521 bits in a 66-byte field, so a leading zero byte
  lands about half the time.
- **Protobuf** — `SESSION_REQUEST` encodings and `SESSION_REPLY` decodings against Google.Protobuf's own
  output, including a payload long enough to need a two-byte length varint and a reply carrying a nested
  field this port does not model.

Plus the FIPS-197 AES vector and the FEC ground truth (the console's own dumped inverse table) — the only
two checks here independent of Ripcord entirely. Everything else tests *agreement* between two
implementations, and two implementations can agree and both be wrong.

**What is verified now, and what still is not:** the host build compiles clean under `-Werror -Wconversion
-Wsign-conversion` and passes all 3,240 cases across ten runners, `make crosscheck` confirms the modules
with no `.3dsx` of their own still compile for ARM11, and `source/app/main.c` has run the control-plane
logic on a real New 3DS — see "Where this stands" below for the on-device numbers. What the vector files
still cannot do is validate against anything *other* than agreement between the two implementations, short
of the FIPS-197 and FEC-table checks; a shared misreading of the spec would pass both sides. **No part of
the protocol above Phase 2 has yet been spoken to a real console**, which is a different and larger gap
than the one this section is about.

## Where this stands

Done:

- [x] AES-128, SHA-256, HMAC-SHA-256, CFB128, OFB, CTR
- [x] Control-session KDF (PS5 and PS4 variants)
- [x] Per-field IV derivation and context-key selection
- [x] Field cipher and streaminfo cipher
- [x] Constants generation from the committed bundle
- [x] Host-side known-answer runner
- [x] On-device smoke test, booted on real New 3DS hardware — all self-consistency checks passed
- [x] First green build — host KAT runner (171/171) and the 3DS cross-compile both pass
- [x] On-device ARM11 timing: ~25 µs per control-field encryption (1000 in 25 ms) — the control plane's
      total crypto cost per connect is on the order of 125 µs; see SETUP.md Phase 1 for what this does and
      does not settle
- [x] SOC service lifecycle (`source/net/rc_soc.c`) — the 0x1000-aligned 0x100000 buffer, owned in one place
- [x] UDP link test run over a real Wi-Fi link, idle and under simulated CPU load — idle reaches ~10.4
      Mbps goodput at the top of a 1→12 Mbps ramp with modest loss at the bottom rung's 2–3 Mbps target
      (2.16% / 4.33%); toggling the load roughly quadruples loss at the same rates (9.68% / 17.93%) and
      flattens busy goodput around 3–3.4 Mbps above ~5 Mbps target — see SETUP.md Phase 2 for the full
      numbers and the caveat on what the synthetic CPU load does and does not model
- [x] Dual console/SD-card logging (`source/util/rc_log.c`) — all four on-device programs write
      everything they print to a `.log` file next to their own `.3dsx`
- [x] LAN discovery: the SRCH probe/parse (`source/discovery/halyard_discovery.c`) and an on-device app
      (`ripcord-3ds-discovery.3dsx`) that broadcasts it for both console families and lists what answers.
      Parser checked against 24 hand-transcribed cases from `docs/protocol/ps5-local-discovery.md` in
      `tests/discovery_test.c`; compiles and links clean, not yet run against a real console
- [x] The `/sess/init` -> `/sess/ctrl` exchange: the control-listener arm probe
      (`source/session/halyard_control_arm.c`), the HTTP-like request/response builder and parser
      (`halyard_sess_request.c`), the five encrypted field plaintexts (`halyard_sess_fields.c`), and the
      persistent binary control-channel frame (`halyard_ctrl_message.c` — corrected against a stale "RPCS
      magic" doc, see SETUP.md Phase 4). 53 cases in `tests/session_test.c`, including one end-to-end
      chain through the already-verified control-field cipher. The on-device app
      (`ripcord-3ds-session.3dsx`) compiles and links clean; not yet run against a real console
- [x] Takion transport: the SCTP 4-way handshake, DATA/SACK chunks with fragmentation/reassembly, and a
      minimal in-order-accept + fixed-interval-retransmit reliable channel (`source/takion/`). The pure
      codecs are checked in `tests/takion_test.c` (61 cases) against known-answer packets lifted from the
      .NET side's own captured-vector tests — including a real doc/implementation mismatch this phase
      caught: `docs/protocol/ps5-session-transport.md`'s "RPCS" description does not apply to Takion at
      all (it documents an unrelated, older transport), and Takion's own reliable channel
      (`takion_reliable_channel.c`) has no host test, same as `rc_soc.c`/`rc_tcp.c` — the on-device app
      (`ripcord-3ds-takion.3dsx`) compiles and links clean; not yet run against a real console. GMAC
      sealing, FEC and the actual stream-key/SESSION_REQUEST exchange are deliberately out of scope here
      — see SETUP.md Phase 5 for exactly where the line was drawn and why
- [x] Stream framing, demux, FEC (`source/stream/`): the 18-byte A/V packet header
      (`stream_header.c` — video's 11/11/10-bit unit packing vs. audio's byte-wide fields), GF(2^8) field
      arithmetic and systematic Cauchy Reed-Solomon erasure coding (`fec_galois.c`/`fec_reed_solomon.c` —
      checked against the console's own dumped inverse table, not just a self-consistency round-trip), the
      stream-plane packet crypto (`rc_gcm.c`'s GMAC, `stream_key_schedule.c`'s KDF,
      `stream_packet_crypto.c`'s per-packet nonce/key rotation — all cross-checked against the .NET
      reference via a new `tools/Ripcord.ProtocolLab -- vectors` output, the same methodology Phase 0
      established), and the demuxer (`stream_demux.c`) that ties it together: frame reassembly by
      frame-index, FEC recovery of dropped source units, IDR/keyframe detection with SPS/PPS
      re-prepending, HEVC vs. H.264 classification, and audio redundant-unit stripping. 2,808 cases across
      four host test binaries (`stream_crypto_test` 65, `fec_test` 2,654 — the field-law sweep over all
      256 GF values dominates that count, `stream_header_test` 58, `stream_demux_test` 31), the last of
      which exercises the whole pipeline end to end (framing → crypto seam → FEC recovery → assembly)
      against synthetic packets via the passthrough crypto stub. No on-device app yet — deliberately:
      there is no real ECDH/SESSION_REQUEST exchange to derive actual stream keys from, so there is nothing
      a real console would send that this could usefully decrypt yet; see SETUP.md for the exact scope line

- [x] The stream key agreement (`source/crypto/rc_ecdh.*`, `source/takion/takion_control_proto.*`,
      `source/takion/takion_session_negotiator.*`): ephemeral ECDH on P-256 and P-521, the
      `SESSION_REQUEST`/`SESSION_REPLY` protobuf envelope, `ecdhSignature` verification, and the
      derivation of all four per-direction key/IV values that `stream_packet_crypto` has been waiting on
      since Phase 5.5. **This is the one primitive the port does not implement itself** — elliptic-curve
      arithmetic is delegated to mbedtls (`dkp-pacman -S 3ds-mbedtls`) behind the `-DRC_CRYPTO_MBEDTLS`
      seam `rc_crypto.h` always anticipated, matching the .NET side's own decision to delegate EC entirely
      rather than hand-roll it; built without that define the seam fails cleanly instead of faking a key
      agreement, so "builds on any machine with a C compiler" survives. 115 new host cases (`ecdh_test`
      44, `control_proto_test` 71) — the ECDH ones cross-checked against a new
      `tools/Ripcord.ProtocolLab -- vectors` output using **fixed** private scalars (a key agreement has no
      known-answer vector otherwise), the protobuf ones against Google.Protobuf's own encoding of
      `docs/protocol/stream_control.proto`. No on-device app yet, same as Phase 5.5

Not started — roughly in dependency order. [`SETUP.md`](SETUP.md) has this as a phased plan with the
toolchain steps:

- [x] **Ran the probes against a real PS5 (2026-08-12)** — run sheet and triage in
      [`HARDWARE-PROBES.md`](HARDWARE-PROBES.md). Discovery found the console **awake and resting**, with
      every field parsing. The session probe reached **`/sess/init -> 200`, `/sess/ctrl -> 200`, session
      ready (17-byte session id)** on the first attempt, and rest mode round-tripped — so the Phase 0
      control-plane crypto (KDF, per-field IV, CFB field cipher, running counter, and the hardcoded codec
      selector 2) is now confirmed against real hardware rather than only against our own .NET
      implementation. Four control-channel message types arrived that neither implementation models
      (`0x0016`/2B, `0x0017`/9B, `0x0003`/4B, plus a 1-byte `0x0005`); the framing skipped all of them
      cleanly. **Two real bugs, both ours, both invisible to host vectors** — see SETUP.md Phases 3 and 5:
      a `bind()` on port 0 that SOC rejects with `EINVAL`, and a 49.6 KB stack local that data-aborted in
      its own prologue against libctru's 32 KB stack. The 3DS build now enforces
      `-Wframe-larger-than=8192`, which immediately caught a second oversized frame in the FEC decoder

- [x] **The combined connect flow** (`source/connect/`, `ripcord-3ds-connect.3dsx`) — the only program
      that carries a real control session through to derived stream keys, and therefore the only way to
      test Takion or the ECDH agreement against a console at all. Control plane -> sign-in gate ->
      session-ready -> senkusha on 9297 -> Takion on 9296 -> SESSION_REQUEST/REPLY -> per-direction keys,
      single-threaded with the control channel's heartbeats serviced throughout (including from inside
      the Takion handshake's wait loops, via a new tick callback). Adds `rc_random` (libctru's CSPRNG),
      the launch-spec builder — **verified byte-for-byte against .NET across 5 cases** — and a shared
      `pairing.txt` loader. Built, not yet run

- [x] **Ran the connect probe against a real PS5 (2026-08-12) — `STREAM KEYS DERIVED`.** Confirmed in one
      run: the Takion transport (twice over, on two independent associations), senkusha bring-up, the
      stream port being 9296, the fragmented SESSION_REQUEST encoding, the launch spec byte for byte, the
      streaminfo cipher **at counter 0** (never pinned by any vector on either side until now — written
      back into the .NET call site), that omitting `adaptiveStreamMode` is harmless, P-521 for client
      version 17, and `rc_random` on device. The run before it found a real bug: continuation fragments
      were going out on channel 0 — the console's own channel — because the module had documented the
      channel field as "reserved, unconfirmed". First message this port had ever fragmented

- [ ] Re-run the standalone Takion probe only against a peer we control — the transport was never actually exercised,
      so the handshake remains the one implemented phase with no hardware evidence either way (and see the
      standing caveat below on why a failure there would still prove little)
- [ ] The launch-spec build (`launchSpecJson`) and wiring the negotiator into a real connect flow — the
      key agreement above is complete as *logic* but has no socket attached, deliberately; what it still
      needs is the session configuration that travels in the launch spec (and carries `handshakeKey` to
      the console) plus an on-device app to drive it
- [ ] MVD H.264 decode → Y2R → PICA200 present
- [ ] Opus audio
- [ ] Input mapping, including touchscreen → touchpad and gyro → gyro
- [ ] Pairing-record import from a desktop Ripcord install
- [ ] **For later discussion:** on-device PIN-based pairing — distinct from record-import above, this
      would be *performing* pairing on this port rather than importing an existing record. The PIN route
      (spec §2.0) rides the same plain-TCP 9295 connection Phase 4 already built `rc_tcp` for, and its KDF
      is a 32-entry table XOR-folded with the on-screen PIN, sealed with the same AES-128-CFB/HMAC-SHA256
      primitives already ported — nothing OAuth/TLS-shaped, unlike the account-based/no-PIN route. Two open
      questions before this is buildable, not just researched: (a) the registration table isn't a bundled
      constant yet (dirty-room only) — bundling it needs the same deliberate NOTICE/CLAUDE.md amendment
      already made twice, a project-owner call; (b) the spec's request-body plaintext includes
      `Np-AccountId` even on the PIN route, meaning the client may need to already know its own PSN account
      ID from somewhere — unresolved

Both hardware questions Phases 1 and 2 existed to answer are now settled: the control plane's crypto costs
~25 µs per field, and the link sustains the bottom rung's target with modest loss on an idle core (2.16%
at 2 Mbps, 4.33% at 3 Mbps) — with the caveat that a simulated CPU load roughly quadruples that loss at
the same rates, a real signal about contention even though the synthetic load itself isn't a stand-in for
actual decode cost. Phases 3, 4 and 5 are all implemented and passing their own (hardware-free) parser
tests — SRCH discovery, the `/sess/init` -> `/sess/ctrl` exchange (the first end-to-end use of the
Phase 0 crypto against real console output), and now the Takion transport underneath where the A/V stream
will eventually ride. Note the pairing record `ripcord-3ds-session.3dsx` reads is a provisional
`pairing.txt` for exercising Phase 4, not the real "Pairing-record import from a desktop Ripcord install"
item still sitting unstarted above — those are two different things with similar-sounding names, and only
one of them is done. Stream framing, FEC and the demuxer are now also implemented and fully self-tested
(2,808 host cases) against the passthrough crypto stub, and Phase 6a has since closed the gap that kept
them stubbed: the ECDH key agreement and `SESSION_REQUEST`/`SESSION_REPLY` exchange now derive real
per-direction stream keys, cross-checked against the .NET side on both curves. Neither of those two phases
has an on-device app yet — 5.5 because it had nothing real to decrypt, 6a because the key agreement has no
socket attached by design and still needs a launch spec and a connect flow around it.

**The next step is the one that has been deferred twice and cannot be a third time: run the
discovery/session/Takion probes against a real console**, awake and resting. Everything from Phase 3
onward is verified only against transcribed spec examples, captured vectors, and agreement with Ripcord's
own .NET implementation — which is a genuinely strong check on *internal consistency* and no check at all
on whether a real PS5 accepts any of it. Three phases of unvalidated wire format is the largest risk this
port currently carries, and it grows with each phase added on top. After that, the launch spec and connect
flow, then Phase 6's media pipeline.
