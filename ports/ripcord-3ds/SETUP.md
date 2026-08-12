# ripcord-3ds — setting up a Linux build box

The 3DS toolchain is happiest on Linux and the .NET side is cross-platform, so a Linux box can drive the
whole loop: generate vectors, verify the C on the host, and cross-compile the `.3dsx`. This is what that
takes, in order.

## 1. Getting the tree across

This is a direct copy of the working directory, not a clone, so the gitignored material comes along and
there is nothing to reassemble. Two things are still worth doing deliberately.

**Decide about the dirty room.** A wholesale copy brings `docs/protocol/captures/` — real captures, session
keys and account identifiers — onto another machine by default. That is the opposite of the usual risk, and
it is a decision rather than an oversight. None of this work needs it: the tests that read it
(`LiveControlVectorTests` and friends) self-skip when it is absent, and `ports/ripcord-3ds` never touches
it. Exclude it unless you have a specific reason:

```sh
rsync -av --exclude 'captures/' --exclude 'bin/' --exclude 'obj/' \
    /path/to/ripcord/ user@linuxbox:~/ripcord/
```

**Drop `bin/` and `obj/`.** They carry Windows-built artifacts and baked-in probing paths, and a stale
`obj/` is what lets a build appear to succeed while silently reusing generated files instead of running
codegen. If you have already copied them:

```sh
find ~/ripcord -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

<details>
<summary>If a future setup ever starts from <code>git clone</code> instead</summary>

A clone does **not** produce a buildable tree. `docs/` is gitignored in full, and
`Ripcord.Protocol.Halyard.Takion` compiles `docs/protocol/*.proto` at build time via Grpc.Tools. Without
them you get:

```
error CS0246: The type or namespace name 'Avstream' could not be found
error CS0246: The type or namespace name 'ControlMessage' could not be found
```

which names *types*, not missing files, several projects downstream of the cause. You would also be
missing the spec itself and `CLAUDE.md` / `ROADMAP.md`. Copy them out of band — and never `git add -f`
them through a branch, since those ignore rules are the publication-review gate.

</details>

## 2. Toolchain

Install instructions drift; check <https://devkitpro.org/wiki/Getting_Started> against what follows.

### devkitPro (Debian / Ubuntu)

```sh
wget https://apt.devkitpro.org/install-devkitpro-pacman
chmod +x install-devkitpro-pacman
sudo ./install-devkitpro-pacman

sudo dkp-pacman -Syu
sudo dkp-pacman -S 3ds-dev        # devkitARM, libctru, 3dsxtool, picasso, tex3ds
```

Then pick up the environment (or just log out and back in):

```sh
source /etc/profile.d/devkit-env.sh
echo "$DEVKITPRO $DEVKITARM"      # expect /opt/devkitpro /opt/devkitpro/devkitARM
```

`ports/ripcord-3ds/Makefile` hard-errors if either variable is unset, so a missed `source` is a clear
message rather than a confusing compiler failure.

Optional, and only when you want the faster on-device crypto backend later:

```sh
sudo dkp-pacman -S 3ds-mbedtls
```

### .NET SDK

Needed only to generate the vectors and run the managed test suites. There is no `global.json`, so any
10.0 SDK works.

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"   # add to your shell profile
dotnet --version                     # expect 10.0.x
```

The `NUGET_PACKAGES` / `NuGetPackageRoot` dance documented for the Windows box does **not** apply here —
that is an artifact of a redirected `USERPROFILE` in a particular sandbox, not of this repository.

### Host C toolchain

```sh
sudo apt install build-essential python3   # gcc, make, and the constants generator
```

## 3. Verify the setup

Run these in order. Each one is a real gate; do not skip ahead when one fails.

```sh
# 1. The .NET side builds and the vector emitter runs.
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#    expect: wrote ports/ripcord-3ds/tests/vectors/control-crypto.kat
#            kdf=80 ctxkey=21 iv=13 mode=33 field=14  (ps4 tables present)

# 2. The C compiles and agrees with it - both the crypto vectors and the discovery parser self-test.
make -C ports/ripcord-3ds/tests
#    expect: self-test: AES-128 matches FIPS-197 C.1
#            171 passed, 0 failed, 0 skipped
#            24 passed, 0 failed

# 3. The cross-compile produces three homebrew binaries.
make -C ports/ripcord-3ds
#    expect: ripcord-3ds.3dsx, ripcord-3ds-linktest.3dsx, ripcord-3ds-discovery.3dsx

# 4. Optional sanity on the managed suites.
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
#    expect: all pass, with skips where the dirty room is absent
```

**Steps 2 and 3 are now a known-good configuration**, not just a starting point: both have been run against
a real devkitPro install. Fixes made getting there, in case a from-scratch checkout hits the same class of
issue again:
- A naming collision between the SHA-256 context typedef and the one-shot hash function in
  `source/crypto/rc_crypto.h`/`rc_sha256.c` (illegal in C; the function is now `rc_sha256_hash`).
- libctru's own headers needing `-isystem` rather than `-I` so this project's `-Werror -Wconversion
  -Wsign-conversion` policy doesn't get applied to code it doesn't own.
- Adding a second host-side test target (`discovery_test`, alongside `vector_runner`) tripped a genuine
  GNU Make gotcha: two build products both using an order-only prerequisite on the same directory target
  makes Make see a dependency cycle, and it silently drops the directory-creation side effect rather than
  erroring - the link step then fails with "No such file or directory" for a directory that was never
  created. Both Makefiles now have each recipe run `@mkdir -p $(@D)` directly instead of depending on a
  separate `build:` target, which has no such graph to get confused about.

## 4. The daily loop

```sh
make -C ports/ripcord-3ds/tests      # after any change under source/ — fast, no hardware
make -C ports/ripcord-3ds            # when you want a .3dsx to try on hardware
```

Regenerate vectors only when the .NET crypto changes:

```sh
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
```

The `.kat` file is deterministic, so an unexpected diff there means the crypto changed — treat it as a
signal, not noise.

## 5. The plan from here

Wi-Fi has been measured and is **not** the blocker it was assumed to be: `ftpd` sustained ~10 Mbps,
dipping to 8 and peaking near 13. The console's bottom rung is 640×360 @ 60, which wants roughly 1.5–3
Mbps. That is comfortable headroom, so the transport work is worth doing.

**Phase 0 — first green build. Done.** Step 2 (host KAT runner, 171/171) and step 3 (ARM11 cross-compile)
both pass. This proves the control-plane crypto is correct in C, and everything else depends on it.

**Phase 1 — first boot. Done.** `ripcord-3ds.3dsx` booted on a real New 3DS: all self-consistency checks
passed, and `source/app/main.c` measured **1000 field encryptions in 25 ms — ~25 µs each** (ARM11 @ 804
MHz, `osSetSpeedupEnable(true)` in effect). One field encryption is one HMAC-SHA256 (IV derivation) plus
one AES-128 block, and a session uses about five of these total (RP-Auth, RP-Did, RP-OSType,
RP-StartBitrate, RP-StreamingType) — so the control plane's total crypto cost per connect is on the order
of 125 µs. That settles the question this phase existed to ask: the control plane is free, full stop, and
whatever the A/V path costs is where the real budget goes. This number is *not* a direct measurement of
the A/V stream cipher (AES-128-CTR, not CFB, and per-packet rather than per-field) — a real per-packet
decrypt benchmark against Phase 2/6 traffic sizes is still owed before treating software AES on the A/V
path as settled, but a rough extrapolation from this number lands comfortably under the bottom rung's
budget.

**Phase 2 — confirm the link properly. Done**, with one caveat about what "busy" measures.
`source/linktest/main.c` (build with `make -C ports/ripcord-3ds linktest`) brings up libctru's SOC
service and listens for 1426-byte UDP payloads with sequence numbers, printing goodput, sequence-gap
loss, and p99 inter-arrival jitter per stage, paired with `tools/udp_link_test_sender.py <3ds-ip>` ramping
1→12 Mbps in five-second stages.

The *first* real run (2026-08-12) surfaced a bug in the harness before it could answer the hardware
question: p99 landed within a few hundred microseconds of either ~16,850 µs or ~33,400 µs on every stage,
idle and busy alike — one and two vblank periods at 60 Hz, because the receive drain lived in the same
loop as `gspWaitForVBlank()`. Fixed by decoupling the drain loop from vblank entirely — it now polls
continuously and redraws the HUD on its own ~10 Hz clock. See the commit history for the full writeup;
what follows is the *second* run, against the fixed harness.

**The second run (2026-08-12) is the real answer.** Idle goodput now scales properly with target rate,
reaching ~10.4 Mbps at the 12 Mbps stage — consistent with the `ftpd` measurement above, and confirming
the ~2 Mbps ceiling in the first run really was the vblank bug. At the bottom rung's actual target
(1.5–3 Mbps), idle loss is modest: 2.16% at 2 Mbps, 4.33% at 3 Mbps. **Toggling Y (simulated CPU load)
at the same target rates roughly quadruples loss — 9.68% at 2 Mbps, 17.93% at 3 Mbps** — and above ~5
Mbps busy goodput flatlines around 3–3.4 Mbps regardless of target, with loss past 70% at the top of the
ramp, while idle keeps climbing toward the ~10 Mbps ceiling. This is the first real evidence that CPU
contention on this core costs UDP receive throughput.

**What this does and does not settle.** `spin_busy_work()` burns a flat ~8 ms once per outer-loop pass —
a crude proxy for contention, not a model of real MVD H.264 decode + Y2R timing, which does not exist yet
(Phase 6). The *direction* (busy costs real throughput) is a genuine finding; the specific ~3.3 Mbps busy
ceiling is shaped as much by this synthetic load's duty cycle as by any hardware limit, and should not be
read as a prediction of what real decode will cost. A smaller nuance for reading busy-stage p99 numbers:
under high loss, a dropped run of packets shows up as one large gap between survivors, so jitter and loss
are correlated in those stages rather than independent signals. Neither of these is a reason to distrust
the idle numbers or the qualitative busy finding — both are worth carrying forward — but the specific busy
ceiling should be revisited once Phase 6 has a real decode-cost number to substitute for the synthetic one.

Both of the *other* traps below are and were handled already — `rc_soc_init()` (`source/net/rc_soc.c`)
owns the 0x1000-aligned 0x100000 SOC buffer, and `main()` calls `osSetSpeedupEnable(true)` before anything
else — noted again in the gotcha list because the next piece of code that opens a raw socket outside this
harness will not get them for free.

**Phase 3 — sockets and discovery. Implemented, not yet run against a real console.** The SOC bring-up
needed for Phase 2 carries over (`source/net/rc_soc.h`/`.c`); what this phase adds is the SRCH
broadcast/response that finds a console on the local network, ahead of the `/sess/ctrl` exchange in
Phase 4.

`source/discovery/halyard_discovery.h`/`.c` re-derives the wire format from
`docs/protocol/ps5-local-discovery.md` — a plain-ASCII, HTTP-status-line-*like* exchange, not real HTTP:
a `SRCH * HTTP/1.1` CRLF-terminated broadcast on UDP 9302 (PS5) or 987 (PS4), answered `HTTP/1.1 200 Ok`
(awake) or `HTTP/1.1 620 Server Standby` (resting) with `host-id`/`host-type`/`host-name`/`system-version`
header-like fields. `host-request-port` is deliberately not parsed — the spec calls it "a red herring":
the LAN wake exchange (not implemented on this port yet) goes to the discovery port itself, never to that
advertised value, on both console families.

The parser has no socket knowledge of its own (see its header for why), so `tests/discovery_test.c`
checks it on the host, with no vector file and no .NET codegen step: the wire format is plain text
transcribed straight from the spec's own request/response/standby examples, so the test's literal strings
*are* the vectors. 24 cases pass — the spec's worked examples, a PS4-profile response, missing/defaulted
fields, mixed-case header names, and a datagram truncated before its trailing blank line.

`source/discovery/main.c` (build with `make -C ports/ripcord-3ds discovery`) is the on-device half:
broadcasts SRCH for both console families and lists every distinct console — deduplicated by host-id —
that answers within a four-second window, the same generous window the .NET pairing UI settled on since a
resting console answers slowly. Builds and links clean, but has never been run against a real console —
that is the next thing this phase needs, the same way Phases 1 and 2 each needed a first real run before
their numbers meant anything.

**Phase 4 — `/sess/init` -> `/sess/ctrl`. Implemented, not yet run against a real console.** The first
exchange that talks to a real console, and the first end-to-end use of the crypto from Phase 0. Ported
from `Ripcord.Protocol.Halyard.Common.Control` (`SessProtocol`, `HalyardSessCtrlFields`,
`HalyardCtrlMessage`) and `HalyardControlSearch`/`HalyardTcpControlChannel` - the same project's own
reference implementation, so there is no clean-room boundary to keep here the way there is against the
protocol's other, unrelated implementations (CLAUDE.md's clean-room section says so explicitly).

**A real doc correction surfaced during this phase.** `docs/protocol/ps5-session-transport.md` (and every
doc file that repeats it) describes the persistent binary control channel as `RPCS`-magic framing. That
was superseded on 2026-08-03 (`docs/protocol-research-log.md`) and `HalyardCtrlMessage.cs`'s own doc
comment says so directly: no `RPCS` magic appears on the wire. The real, tested format is an 8-byte
header - `u32` BE payload length, `u16` BE type, `u16` BE reserved(0) - with no magic at all.
`source/session/halyard_ctrl_message.c` implements the corrected format and cites both sources; treat
`HalyardCtrlMessage.cs` and the research log as authoritative over the stale doc file if the two ever
seem to disagree again.

**What's implemented**, all with host-side tests in `tests/session_test.c` (53 cases) that need no
hardware and no .NET vector file:

- **The control-listener arm probe** (`source/session/halyard_control_arm.c`) — the console does not keep
  its TCP control port (9295) open continuously; a 4-byte UDP `SRC3`/`SRC2` probe opens it briefly, and
  one probe covers both `/sess/init` and the `/sess/ctrl` reconnect that follows.
- **The `/sess/init`/`/sess/ctrl` HTTP-like request builder and response parser**
  (`source/session/halyard_sess_request.c`) — GET, header-only, hand-built rather than real HTTP (no
  headers section beyond the trailing blank line). `/sess/init` presents `RP-Registkey` as plaintext hex
  and gets `RP-Nonce` back; `/sess/ctrl` is served `Connection: keep-alive` and becomes the persistent
  binary channel below.
- **The five encrypted `/sess/ctrl` fields** (`source/session/halyard_sess_fields.c`) — `RP-Auth`,
  `RP-Did`, `RP-OSType`, `RP-StartBitrate`, `RP-StreamingType`, at counters 0-4 of the *same running
  per-connection counter* the control-field cipher tracks (a later login-passcode submission on the
  binary channel continues at counter 5 - never restart it per message type, or an IV gets reused).
  `RP-Auth`/`RP-Did`/`RP-OSType`'s plaintext shapes are wire-confirmed `[V]`; `RP-StartBitrate`/
  `RP-StreamingType`'s 4-byte-little-endian encoding is `[X]` - it appears in neither this port's nor the
  .NET side's verified vectors, only in shipped (untested) behaviour.
- **The binary control-channel frame** (`source/session/halyard_ctrl_message.c`) — see the doc-correction
  note above. Test vectors transcribed byte-for-byte from `HalyardCtrlMessageTests.cs`.
- **The on-device orchestrator** (`source/session/main.c`, build with `make -C ports/ripcord-3ds session`)
  — arm, connect, `/sess/init`, derive the control key via `halyard_control_field_init` (codec selector 2,
  matching the .NET side's own comment that the full `RP-KeyType` -> selector mapping is still a tracked
  refinement, not something this port resolves), reconnect, `/sess/ctrl`, then answer `HEARTBEAT_REQ` on
  the binary channel - missing that is what makes a real console RST the session ~15-30s in. Reads a
  provisional `pairing.txt` (key=value text, documented in the file's own header comment) since this port
  does not pair; **this is not the "Pairing-record import" backlog item**, which is a separate, still
  unstarted piece of work.
- Compiles and links clean as a fourth `.3dsx`. The `/sess/init`/`/sess/ctrl` sockets are non-blocking
  from the moment they connect (`wait_for_sess_response()` polls with a 5-second bound, and
  `rc_tcp_send_all()` retries `EAGAIN`/`EWOULDBLOCK` the same way) - a console that never answers gets a
  bounded failure, not a hung program.

**Phase 5 — Takion. Implemented, not yet run against a real console.** Handshake, reliable delivery,
reassembly. Ported from `Ripcord.Protocol.Halyard.Takion` (`TakionMessageHeader`, `TakionHandshake`,
`TakionConnection`, `TakionDataChunk`, `TakionSackChunk`, `TakionMessageReassembler`,
`TakionReliableChannel`) - the same project's own reference implementation (see the note on Phase 4 above
about why that needs no clean-room distance).

**A real doc mismatch surfaced here too, of a different shape than Phase 4's.**
`docs/protocol/ps5-session-transport.md` is not a stale *description* of Takion - it documents a
*completely different transport* (an older/other-generation RUDP framing with a `24 4F 24 4F` magic and
`RPCS` frames, superseded by the Takion findings in `docs/protocol/ps5-remoteplay-v1-spec.md` secs 3 and
8, which are what this phase actually implements). Ignore that doc file entirely for Takion wire format;
it answers a question about a transport this port does not use.

Takion is "essentially SCTP over UDP" (spec sec8, wire-validated against
`captures/rudp_control_setup.pcapng`): the 13-byte message header (base type, verification tag, GMAC tag,
key position), the SCTP 4-way handshake (INIT/INIT_ACK/COOKIE_ECHO/COOKIE_ACK, chunk types straight from
RFC 4960), and DATA/SACK reliable delivery are all confirmed byte-for-byte against known-answer packets
lifted from the .NET side's own test suite (`TakionTests.cs`, `TakionDataChunkTests.cs`,
`TakionReliabilityTests.cs`) - transcribed as literal hex strings in `tests/takion_test.c` (61 cases), no
vector file and no .NET codegen step needed, the same substitution `tests/discovery_test.c` already
established as legitimate for a plain-text/plain-bytes wire format. INIT_ACK/COOKIE_ECHO/COOKIE_ACK and
the DATA continuation-fragment shape have no captured vector available and are checked by round-trip
instead - see each module's header comment for exactly what confidence level applies where.

**What's in scope, and what's deliberately deferred**, per the spec's own explicit "sufficient for a
clean-LAN first picture" framing (sec8.2) and `docs/protocol/IMPLEMENTATION.md`'s "what does NOT block a
first picture" list:
- In scope: the handshake (active-open only - this port never answers an INIT), DATA chunk build/parse
  with fragmentation, SACK build/parse (cumulative-only - gap/dup blocks were never observed on a
  clean-LAN capture, though parsing tolerates a peer sending them), a single-in-flight-message reassembler
  (`source/takion/takion_reassembler.c`; the .NET reference uses a per-channel dictionary, but a
  continuation fragment carries no channel id at all, so at most one message can be in flight
  unambiguously - see that file's header for why one slot is not a simplification of the real design, it
  models what the wire format can even distinguish), and a minimal in-order-accept +
  fixed-interval-retransmit + cumulative-SACK reliable channel.
- Deferred, explicitly not needed to stream: FEC (lives entirely in the A/V demux/reassembly layer, not
  Takion's SCTP sublayer - "with no packet loss the decoder never runs. Skip"), RTT estimation/Karn's
  algorithm/congestion-window tuning (wire format is `[V]`, only timing is unspecified - a fixed 300ms
  retransmit interval is fine for a first pass), the keyless senkusha probe (`HalyardSenkusha`,
  non-fatal even in the .NET reference), the `PROTOCOL_VERSION_REQUEST`/IPv6-token exchange (explicitly
  skippable - assume a version), and GMAC sealing (needs the stream cipher - a separate KDF from the
  control-plane one, and a separate later phase).
- `source/takion/takion_reliable_channel.c` is the one file in this whole protocol layer, other than
  crypto, that owns a socket - like `rc_soc.c`/`rc_tcp.c` it has no host-side test, only the on-device app
  (`source/takion/main.c`, build with `make -C ports/ripcord-3ds takion`). That app is explicitly
  exploratory: it drives the handshake and a synthetic test message against a `takion.txt`-configured
  host:port, without the session-ready gating or senkusha probe a real connection sequence needs first
  (see the file's own header comment) - it exists to test the transport in isolation, not to be the real
  connect flow.

**Phase 5.5 — stream framing, demux, FEC. Implemented and fully host-tested; no on-device app yet.** Ported
from `Ripcord.Protocol.Halyard.Common.Streaming` (`HalyardStreamHeader`, `HalyardStreamDemuxer`,
`Fec/GaloisField256`, `Fec/CauchyReedSolomon`) and the stream half of `Crypto/V1`
(`HalyardStreamKeySchedule`, `HalyardPacketCrypto`'s GMAC/nonce/key-rotation math) — the same project's own
reference implementation, so no clean-room distance is needed here either (see the note on Phase 4 above).

**What's implemented**, in `source/stream/`:

- **The 18-byte A/V packet header** (`stream_header.c`) — bit-exact per spec sec6.1: video packs
  `unit_index`/`total_units-1`/`parity_units` as 11/11/10-bit fields, audio packs byte-wide fields instead
  (parsing audio with the video layout yields nonsense unit counts, a bug that bit the .NET side before it
  was caught there). `PayloadOffset` accounts for the type-specific prefix (+3 video, +2 audio/other) and
  the optional extended-header flag. No captured A/V vector exists in the dirty room (no capture pairs A/V
  traffic with usable stream keys — the one session with keys never reached streaming) and the .NET
  reference has no direct unit test for this type either, so `tests/stream_header_test.c` (58 cases) is a
  build/parse round-trip self-test, the same substitution already used for Takion's uncaptured message
  shapes.
- **GF(2^8) field arithmetic and Cauchy Reed-Solomon** (`fec_galois.c`/`fec_reed_solomon.c`) — exp/log
  tables over primitive polynomial `0x11d`, the `matrix[i][j] = 1/(i XOR (m+j))` coding matrix, and
  Gauss-Jordan-based encode/decode. Unlike the header above, this one has genuine ground truth:
  `tests/fec_test.c`'s `test_galois_matches_console_inverse_table` checks this port's inverse table against
  bytes dumped live from the vendor client's own field object (see `FecTests.cs`'s equivalent — the head
  and tail of the console's real `1/b` division-table row), which is unique to polynomial `0x11d` across
  all 16 primitive degree-8 polynomials. Everything past the field itself (matrix construction, encode,
  erasure recovery) is self-consistency — it proves this port's decoder inverts this port's encoder, not
  that it matches a console-produced parity unit, for the same reason no A/V vector exists. 2,654 cases
  (the field-law sweep over all 256 GF values dominates that count) — including the recovery-is-independent-
  of-slot-stride case that settled the long-running "is the 0x10 stride a mis-derived wire constant"
  question on the .NET side (it never was one; the console's own stride *is* its coded length, ours is free).
  `FEC_MAX_TOTAL_UNITS` (64) bounds a frame's total unit count for this port's fixed-size buffers — no
  malloc anywhere in this port — chosen because the wire's own 11-bit `unit_index` field permits far more
  (up to 2048) than any real captured frame ever codes over.
- **Stream-plane packet crypto** (`rc_gcm.c`'s GMAC over GF(2^128), `stream_key_schedule.c`'s SP800-108 KDF,
  `stream_packet_crypto.c`'s per-packet nonce derivation and periodic GMAC-key rotation) — cross-checked
  against the .NET reference the same way Phase 0's control-plane crypto was: `tools/Ripcord.ProtocolLab`
  now also emits `tests/vectors/stream-crypto.kat` (gmac/streamkdf/packetnonce/packettag vectors, generated
  from `AesGcmCore`/`HalyardStreamKeySchedule`/`HalyardPacketCrypto` directly), checked in
  `tests/stream_crypto_test.c` (65 cases). Deliberately does not implement ECDH itself — same as the .NET
  side, which delegates entirely to `System.Security.Cryptography.ECDiffieHellman` and has no custom EC
  point arithmetic anywhere in the project. `stream_packet_crypto` therefore takes an already-derived
  `aes_key`/`base_iv` pair; deriving those from a real console handshake is the ECDH/`SESSION_REQUEST`
  backlog item below, still unstarted.
- **The demuxer** (`stream_demux.c`) — splits the multiplexed stream by packet type, verifies+decrypts each
  media packet through a crypto seam (same seam-and-stub shape as `IHalyardSessionCrypto`:
  `stream_demux_passthrough_crypto` — identity, for testing without real keys — or
  `stream_demux_packet_crypto` wiring the struct above), reassembles video units into whole Annex-B frames
  keyed on frame index, triggers FEC recovery when enough units survive, detects IDR/IRAP slices to decide
  when to re-prepend the out-of-band SPS/PPS parameter sets, classifies HEVC vs. H.264 from those parameter
  sets (never from slice headers — an ordinary H.264 slice byte can read as a valid HEVC type), and strips
  audio's redundant loss-concealment units down to the one real Opus frame per packet. Callback-based
  (`stream_demux_sink`) rather than the .NET reference's C# events. `tests/stream_demux_test.c` (31 cases)
  runs the whole pipeline end to end against the passthrough seam — including one case that builds a real
  FEC-coded frame, drops two of four source units, and confirms the demuxer's own flush path recovers the
  original bytes exactly, the FEC self-consistency check applied one layer up from `fec_test.c`.

**Why no on-device app yet**, unlike every phase before it: this phase has nothing real to exercise without
a live stream key. Phases 3-5 each got an on-device app because there was something real to try (a
broadcast to send, a console to connect to, a transport to drive) even before the layer above it existed;
this phase's crypto seam only becomes meaningful once ECDH exists to feed it real keys, so an app today
could only wrap the passthrough stub the host tests already exercise more thoroughly. Cross-compiled clean
against the real ARM11 toolchain regardless (`-march=armv6k -mtune=mpcore -mfloat-abi=hard -mtp=soft
-D__3DS__`, the same flags every other phase's `.3dsx` uses) — every file in `source/stream/` compiles
warning-free and the whole module links against libctru — confirming this phase is 3DS-buildable, just not
yet independently demonstrable on hardware.

**Backlog, uncovered by this phase**: the ECDH key exchange and the `SESSION_REQUEST`/stream-key handshake
that would let `stream_packet_crypto` run against a real console instead of the passthrough stub. Scoped out
explicitly, same reasoning as the .NET side: hand-rolling P-256/P-521 point arithmetic in C on ARM11 is
large and security-critical, and the pragmatic path is the `-DRC_CRYPTO_MBEDTLS` seam `rc_crypto.h` already
anticipates (see the optional `3ds-mbedtls` package in section 2 above) — not yet decided, tracked here for
whenever ECDH work starts.

**Phase 6 — media.** MVD H.264 decode → Y2R → PICA200, Opus audio, input mapping.

Pairing is not on this list: pair with desktop Ripcord and copy the record across. See the README.

## 6. Gotchas worth knowing before you hit them

- **`osSetSpeedupEnable(true)`** — without it a New 3DS runs at the old clock. Any performance number taken
  without it describes a machine you are not targeting. `source/app/main.c`, `source/linktest/main.c`,
  `source/discovery/main.c`, `source/session/main.c` and `source/takion/main.c` all call this first thing
  in `main()`; a new on-device entry point needs its own call.
- **`socInit` buffer size** — 0x100000, aligned to 0x1000. Undersizing it produces drops that look exactly
  like a Wi-Fi ceiling. `rc_soc_init()` (`source/net/rc_soc.c`) owns this; use it rather than calling
  `socInit` directly.
- **No per-packet `printf`** — console output alone will cap throughput well below the link. Counters only.
  `source/linktest/main.c` only prints once per stage, for exactly this reason.
- **HEVC must be refused at negotiation** — the MVD decoder does H.264 only.
- **The constants are generated, never copied.** `tools/gen_constants.py` reads the one committed bundle at
  build time and writes into `build/`, which is gitignored. If you ever find yourself checking a generated
  constants file in, stop and read that script's header.
- **The binary control-channel frame has no "RPCS" magic**, despite what
  `docs/protocol/ps5-session-transport.md` (and every doc file that repeats it) says. That description was
  superseded 2026-08-03; the real format is an 8-byte header with no magic at all. See Phase 4 above and
  `source/session/halyard_ctrl_message.c`'s own header comment before trusting that doc file on this point.
- **The control-field counter is one running per-connection value, not per-message-type.** The five
  `/sess/ctrl` fields use counters 0-4; a later login-passcode submission on the binary channel continues
  at 5. Resetting it back to 0 for a later message on an already-established connection reuses an IV.
- **`SO_BROADCAST` before `sendto()` to a broadcast address.** Without it the OS refuses the send outright;
  `source/discovery/main.c` sets it once at socket setup.
- **`host-request-port` is a documented red herring.** The LAN wake exchange goes to the discovery port
  itself (9302/987), never to this advertised value, on both console families - see
  `docs/protocol/ps5-local-discovery.md`. `halyard_discovery.c` does not parse it at all, deliberately.
- **A shared directory as an order-only prerequisite (`| $(BUILD)`) is not safe once two build products use
  it.** GNU Make can see a dependency cycle and silently drop the `mkdir` side effect - see the note in
  section 3. Every recipe in both Makefiles now runs `@mkdir -p $(@D)` itself instead.
- **A literal `/*` inside a `/* ... */` doc comment is a hard error under `-Werror` (`-Wcomment`)**, and it
  has bitten three times now - phrases like `*out_chunk/*out_chunk_length` or a path fragment ending in
  `/*` read as a nested-comment start. Reword rather than lean on a slash-separated list of pointer names
  or path-like text inside a comment.
