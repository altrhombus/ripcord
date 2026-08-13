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

**Required since Phase 6a** — the ECDH backend. This is the one primitive the port does not implement
itself (see `source/crypto/rc_ecdh.h` for the argument), so without it the key agreement cannot run:

```sh
sudo dkp-pacman -S 3ds-mbedtls
```

The host-side test runner needs its own copy, since it builds for this machine rather than the 3DS:

```sh
sudo apt install libmbedtls-dev
```

Note the two are different majors — devkitPro ships **2.28.x**, Debian ships **3.6.x** — and 3.x moved
several struct fields behind `MBEDTLS_PRIVATE`. `rc_ecdh.c` is written against the function-level
`mbedtls_ecp_*` / `mbedtls_mpi_*` API only, which is stable across both, and extracts the shared X via
`mbedtls_ecp_point_write_binary` rather than reading the point struct. If you ever need a field 3.x has
hidden, add another write_binary-shaped detour rather than defining `MBEDTLS_ALLOW_PRIVATE_ACCESS`.

Neither package is needed to build the rest of the port. `make ECDH_BACKEND=none` (either makefile) drops
both, and the ECDH runner then reports a skip and exits 0 instead of failing — the property that keeps
"any machine with a C compiler" true.

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
# 1. The .NET side builds and the vector emitter runs. It writes four files, not one.
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#    expect: wrote ports/ripcord-3ds/tests/vectors/control-crypto.kat
#            kdf=80 ctxkey=21 iv=13 mode=33 field=14  (ps4 tables present)
#            wrote .../stream-crypto.kat    gmac=9 streamkdf=4 packetnonce=12 packettag=12
#            wrote .../session-crypto.kat   ecdhpub=6 ecdhshared=8 ecdhsig=4 streamkeys=8
#            wrote .../control-proto.kat    sessionreq=5 sessionreply=5

# 2. The C compiles and agrees with it - ten runners, no hardware involved.
make -C ports/ripcord-3ds/tests
#    expect: self-test: AES-128 matches FIPS-197 C.1
#            171 / 24 / 53 / 61 / 65 / 2654 / 58 / 31 / 44 / 71 passed, 0 failed  (3,232 total)
#    if ecdh_test says "skipped: built without an ECDH backend", libmbedtls-dev is missing

# 3. The cross-compile produces five homebrew binaries...
make -C ports/ripcord-3ds
#    expect: ripcord-3ds.3dsx, -linktest, -discovery, -session, -takion

# 4. ...and the modules that have no .3dsx of their own still compile for ARM11.
make -C ports/ripcord-3ds crosscheck
#    expect: all app-less modules compile clean for ARM11

# 5. Optional sanity on the managed suites.
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

**Phase 3 — sockets and discovery. RUN AND CONFIRMED on real hardware (2026-08-12).** A PS5 was found
both awake and resting, with `host-type`, `host-name`, `host-id` and `system-version` all parsing — so the
SRCH wire format holds up outside the spec's transcribed examples, in both console states.

**The first run failed, and the bug was ours.** `bind()` returned `EINVAL` (errno 22) before a single
packet went out. The socket bound port 0, letting the stack pick an ephemeral port — which is what the
.NET side does (`HalyardControlSearch` binds `IPAddress.Any:0`) and is correct there, but the 3DS SOC
service rejects a zero port outright. `source/linktest/main.c` had been binding a *nonzero* port on the
same hardware since Phase 2, and that differential is what identified it: same socket type, same
`INADDR_ANY`, same addrlen, only the port differed. Discovery now binds a fixed port (9310, retrying up to
9313) and logs which it got. A console answers SRCH to whatever source port the probe came from, so any
port works — "let the stack choose" is the one option unavailable.

This is the failure mode this port is structurally prone to and host vectors can never catch: an idiom
that is right on the reference platform and invalid on the target. No vector exercises a socket.

The SOC bring-up
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

**Phase 4 — `/sess/init` -> `/sess/ctrl`. RUN AND CONFIRMED on real hardware (2026-08-12), first try.**
The single largest de-risking event this port has had. Against a real PS5:

```
/sess/init -> 200
/sess/ctrl -> 200
ctrl message type 0x0005, 1 bytes
session ready (17-byte session id)
ctrl message type 0x0017, 9 bytes
ctrl message type 0x0016, 2 bytes
requested rest mode
rest mode acknowledged
ctrl message type 0x0003, 4 bytes
```

`/sess/ctrl -> 200` is the line that matters: the console accepted all five encrypted `RP-*` fields, which
means the Phase 0 control-plane crypto — the KDF, the per-field IV, the AES-128-CFB field cipher, the
running counter, **and the hardcoded codec selector 2** — is correct against real hardware, not merely in
agreement with our own .NET implementation. The binary control channel then framed correctly (17-byte
session id), and a Y press round-tripped rest mode (`0x0050` -> `0x8050`) and cleanly closed the session.

**Four message types arrived that this port does not model**, and they are new information:

| Type | Payload | Status |
|---|---|---|
| `0x0005` | 1 byte | Known to the .NET side as the login/"you may stream" signal; the payload size is new, and `source/session/main.c` only special-cases `0x0004` so this fell through to the generic log |
| `0x0016` | 2 bytes | **Unknown to both implementations** |
| `0x0017` | 9 bytes | **Unknown to both implementations** |
| `0x0003` | 4 bytes | **Unknown to both implementations**, arrived after the rest-mode ack |

Nothing broke for not understanding them — the framing skipped each cleanly, which is itself a check on
`halyard_ctrl_message.c`. Their meanings are open questions, not defects.

The first
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

**Phase 5 — Takion. RUN ON HARDWARE (2026-08-12); it crashed, and the crash was a real bug.** The probe
died with an ARM11 data abort before printing anything past its banner. The transport itself was never
exercised, so the handshake codec remains unvalidated on hardware — but the crash was worth having.

Luma's dump decoded to: data abort, **write**, translation fault, `FAR = 0x07ffb944` against
`SP = 0x07ffb950` — a write 12 bytes *below* the stack pointer, into an unmapped guard page. A stack
overflow, hit in the function prologue, which is why the log stopped after the banner and never named the
function it was in.

**The cause: `takion_reliable_channel` is 49.6 KB** (`TAKION_MAX_UNACKED` 32 x `TAKION_MAX_PACKET` 1500)
**and it was an ordinary local in `run_takion()`. libctru gives a `.3dsx` main thread a 32 KB stack in
total** (`__stacksize__` defaults to `0x8000`). The frame could never fit. Fixed by making it `static`.

**The structural fix matters more than the fix.** The 3DS build now passes
`-Wframe-larger-than=8192`, so an oversized frame fails the build instead of the handheld. It immediately
caught a second instance nothing had noticed: `fec_reed_solomon_decode` held three 64x64 scratch matrices
— 12.6 KB, 40% of the whole thread stack, several frames deep inside the demux flush path. Those are now
`static` too, which makes that function non-reentrant and single-threaded-only; safe today because this
port creates no threads anywhere, and flagged in the source for whenever the media pipeline does.

A third landmine was sitting unarmed: `stream_demux` is **513 KB**, so the first `stream_demux demux;`
local in the eventual connect flow would have died exactly the same way. Both oversized structs now carry
the warning in their headers, and the compiler enforces it regardless.

**The re-run (2026-08-12, after the fix) got past the prologue and then found the probe's real problem.**
It reached `handshake: sending INIT to <console>:9297` — proving the stack fix — and then
`handshake did not complete (5 attempts x 1000 ms)`. That failure is not a transport defect and not weak
evidence; it is **structural**. Two facts, one of which had been understated:

- **The console has no listener open outside a live session.** It opens its stream UDP ports only between
  `/sess/ctrl` and the stream. This probe runs standalone, so an INIT has no possible recipient — and the
  run that demonstrated it had already ended its session and put the console into rest mode. No port
  value makes this program work against a console.
- **9297 is the senkusha port, not the stream port.** The A/V stream sits on **9296**, one below
  (`HalyardStreamingSession.SenkushaPort`, wire-confirmed). The `takion.txt` default was pointing at the
  wrong one of the two, which mattered less than the first point but is worth correcting.

So `ripcord-3ds-takion.3dsx` keeps its value only against a peer we control — a host-side responder or a
second 3DS. **Takion still has no hardware evidence either way**, and getting it needs the connect flow
rather than a better port number: hold the Phase 4 control channel open past session-ready, run senkusha
on 9297, then INIT the stream on 9296, in one combined program. That is the natural home for Phase 6a's
negotiator too, and it is the next real piece of work.

Handshake, reliable delivery,
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

**Phase 6a — the stream key agreement. Implemented and host-tested; no on-device app yet.** The gap Phase
5.5 explicitly left open: `stream_packet_crypto` had a complete cipher and no keys to put in it. Ported
from `Ripcord.Protocol.Halyard.Takion.TakionSessionNegotiator` and the ECDH half of
`Crypto/V1/HalyardStreamKeySchedule`.

**The one thing this port does not implement itself.** Elliptic-curve arithmetic over a 521-bit prime
field is not something to write twice, and the .NET side reached the same conclusion — it delegates
entirely to `System.Security.Cryptography.ECDiffieHellman` and has no custom EC point math anywhere. So
`source/crypto/rc_ecdh.h`/`.c` is a seam over mbedtls, in the shape `rc_crypto.h` anticipated from the
start (`-DRC_CRYPTO_MBEDTLS`). Two properties were treated as non-negotiable:

- **No fake fallback.** Built without a backend, every entry point fails and `rc_ecdh_available()` returns
  0. A stub returning predictable "shared secrets" would let a broken build pass its own tests and then
  negotiate a session with no confidentiality at all, so there is deliberately no such stub — the test
  runner skips instead.
- **The RNG is the caller's.** mbedtls's own entropy sources assume a hosted OS the 3DS does not provide,
  so on device it has to come from libctru regardless; and known-answer vectors require a *fixed* private
  key, which a curve implementation insisting on generating its own randomness cannot give you. The
  callback signature matches mbedtls's `f_rng` so it passes straight through.

**What's implemented:**

- **`source/crypto/rc_ecdh.*`** — P-256 and P-521 keygen, public-key recovery from a fixed scalar, and
  shared-secret derivation. Peer keys are validated as on-curve before being multiplied by our private
  scalar (the invalid-curve attack leaks the scalar a subgroup at a time, and "it came from the console"
  is not authentication), and a peer key whose length implies the other curve is refused rather than
  coerced — the wire carries no curve id, so length is the only thing distinguishing them.
- **`source/takion/takion_control_proto.*`** — a hand-rolled proto2 codec for the two messages this
  exchange needs. The .NET side compiles all 41 messages with Google.Protobuf + Grpc.Tools, which is right
  there and wrong here: this port needs two, has no malloc, and would pay more for a code generator in a
  cross-compiled makefile than the ~200 lines cost. Field numbers come from
  `docs/protocol/stream_control.proto`, which is the authority.
- **`source/takion/takion_session_negotiator.*`** — builds `SESSION_REQUEST`, consumes `SESSION_REPLY`,
  verifies the peer's `ecdhSignature` in constant time before touching its key, and derives all four
  per-direction key/IV values. Socket-free by design, the same split `halyard_discovery.h` uses.

**On `handshakeKey`, which is the easiest thing here to get wrong.** It is 16 fresh random bytes the
*client* generates per session, and it is **not** derived from the pairing record and **not** related to
the control-plane KDF. It reaches the console inside the launch spec — OFB-encrypted under the control
plane's `out1` key and base64'd, as the JSON member `handshakeKey` — which is why the control plane has to
be up before this exchange runs. Its only job is binding the two ECDH public keys to a session the console
already agreed to. A caller passing a fixed value instead of random bytes has removed the exchange's only
protection against a man in the middle.

**Verification** — 115 host cases, in two runners:

- `ecdh_test` (44) reads the new `tests/vectors/session-crypto.kat`. The private scalars are **fixed**,
  because a key agreement has no known-answer vector otherwise; `rc_ecdh_keypair_from_private()` is in the
  public header for that reason rather than as a test-only back door. Note what is and is not under test:
  not mbedtls's curve arithmetic, but the code around it — curve selection, uncompressed-point layout,
  full-width coordinate writes, peer validation. The `streamkeys` vectors run the whole chain (scalar →
  shared secret → KDF → keys) with no intermediate handed over, so a port that gets ECDH wrong cannot pass
  the half it can still do. There is also a full simulated negotiation against a hand-encoded reply,
  checking that both sides land on the same four values *with the directions crossed*, and that a
  one-bit-flipped `ecdhSignature` is refused.
- `control_proto_test` (71) checks encodings byte-for-byte against Google.Protobuf's own output.

**A note on the P-521 coordinate width**, since it is the one place a plausible implementation silently
diverges: X is at most 521 bits but the field is 66 bytes, so the top seven bits are always zero and the
first byte is always `0x00` or `0x01`. A leading zero byte therefore lands about half the time, against
roughly 1-in-256 on P-256 — which means an implementation writing the coordinate at its trimmed natural
length rather than the curve's fails these vectors immediately instead of intermittently in production.

**What is deliberately NOT here.** The launch spec (`launchSpecJson`) is a caller-supplied input, not
built by this phase — it needs session configuration (codec, resolution, bitrate) and belongs to whoever
owns the connect flow, the same way `stream_packet_crypto` takes an already-derived key rather than
reaching upward for it. There is consequently no on-device app: the negotiator has no socket attached, and
wiring it to `takion_reliable_channel` without a launch spec to send would produce a request a console
rejects. `make crosscheck` confirms all three new modules compile clean for ARM11 regardless.

**Phase 6b — the combined connect flow. RUN ON HARDWARE (2026-08-12); got most of the way, and found a
real wire bug.** `ripcord-3ds-connect.3dsx` carries a live control session through senkusha to the stream
plane. Two runs against a real PS5 were identical:

```
/sess/init -> 200 ; /sess/ctrl -> 200 ; session ready
senkusha: established / PROTOCOL_VERSION_ACK / SESSION_REPLY - bring-up complete
stream: Takion ESTABLISHED (9296)
stream: SESSION_REQUEST 1716 bytes (curve P-521), fragmenting
<nothing>
```

**What this settles.** The **Takion transport is confirmed against real hardware** — twice over, on two
independent associations (9297 and 9296), including the 4-way handshake, DATA, SACK and reassembly, since
senkusha's PROTOCOL_VERSION and SESSION exchanges both completed. Senkusha's bring-up is confirmed. The
stream-plane handshake on **9296** is confirmed, settling the 9296-vs-9297 ambiguity the spec left open.

**The bug: continuation fragments were sent on channel 0.** The 1716-byte SESSION_REQUEST is the first
message this port has ever fragmented, and `takion_data_chunk.c` zero-filled value offsets 4-7 of a
continuation, having documented those four bytes as "reserved, meaning unconfirmed". They are not
reserved: `TakionDataChunk.Build` writes the **channel** at value offset 4 in *both* fragment shapes and
shrinks only the reserved region (3 bytes to 2), which is the whole reason the payload offset differs (9
vs 8). So every fragment after the first went out labelled channel 0 — the channel the *console* sends
on. Fixed; `takion_data_build_continuation` now takes the channel, and `takion_test.c` asserts both the
round-trip and the literal byte offset, because the previous round-trip test ignored the field and that
is precisely why nothing caught it.

**A second, self-inflicted problem worth recording:** the run ended having logged nothing after
"fragmenting", because the negotiate loop's timeout path had no message. A silent timeout is
indistinguishable from a crash in a log file, and it cost a debugging session to rule out the latter. The
wait now always names itself and reports how many reliable messages arrived on the stream channel — zero
vs. non-zero being the single most useful discriminator for what to suspect next.

**The run after the fix (2026-08-12) reached `STREAM KEYS DERIVED`.** The console returned a 203-byte
SESSION_REPLY, its `ecdhSignature` verified, and both per-direction key/IV pairs came out. That single
line confirms, against real hardware and all at once, a list of things that until then were only "agrees
with our own .NET implementation":

- **The Takion continuation-fragment layout** — the channel-0 fix was right.
- **The hand-rolled SESSION_REQUEST protobuf**, including the fragmented encoding of a 1716-byte message.
- **The launch spec**, byte for byte. A wrong character anywhere in that document would have left the
  console unable to parse it.
- **The streaminfo cipher at counter 0.** This one is worth stating on its own: the counter had *never*
  been pinned by any captured vector on either side (the .NET call site now says so), and it is a
  suspicious value because `RP-Auth` already uses counter 0 with the CFB field cipher, so the two share
  an IV and their first keystream blocks are identical. It is nonetheless correct — the console
  recovered the handshakeKey from inside that document, which is the only way the signature could verify.
- **Omitting `adaptiveStreamMode:"resize"` is harmless**, closing the other open question about the spec.
- **P-521 for client version 17**, the ECDH agreement itself, and `rc_random` on device.

The finding about counter 0 has been written back into `HalyardStreamingSession.BuildSessionRequest` —
this port existing to test the spec is the whole argument for it, and this is the first time it has paid
that back as a confirmation rather than a defect.

**Phase 6d — MVD decode. Two hardware bugs found and fixed; no picture yet.** Three findings worth
keeping, because two of them were things this port *already knew* and repeated anyway:

1. **MVD's input buffer needs `linearMemAlign(size, 0x40)`, not `linearAlloc`.** With plain linearAlloc
   every single NAL unit was rejected - 1105 fed, 1105 errors. *All* units failing is the signature of
   the block refusing the buffer; a bad bitstream fails only some.
2. **The render target has to be a 16-bit screen the console does not own.** After the alignment fix the
   symptom became 0 pictures with **zero** process errors and ~15 render errors per session - the
   bitstream decoding perfectly and having nowhere to go. `gfxInitDefault()` gives the top screen 24-bit
   BGR8 while MVD emits BGR565, and `consoleInit(GFX_TOP)` had handed that framebuffer to printf.
3. **The receive loop drained one packet per 2 ms tick.** This is the same mistake `source/linktest`
   made on its own first hardware run, where a vblank-coupled drain produced a fake ~2 Mbps ceiling -
   repeated here, in a different file, three phases later. A keyframe is a burst of a dozen units, so a
   one-packet drain guarantees overflow, and unit loss ran at ~48%.

**The diagnosis of (3) is worth recording as a method note.** Loss rose sharply the moment IDR requests
were added, and the obvious story - keyframes are large, the link is saturated, this is
loss-amplification - was wrong. A decode-on/decode-off A/B produced *identical* loss (1799 vs 1743
units), which ruled out both CPU contention and the amplification story in one measurement and pointed
at the drain. The toggle existed only because the earlier guess (that decode cost would starve the
socket) had been wrong too; keeping it paid for itself immediately.

**Also confirmed in these runs:** IDR requests do work (keyframes went from 1 per session to 15-18), and
the A/V GMAC continues to verify on essentially every packet across ~3,700 per run.

## Architectural research still owed

Written up after the Phase 6d decode work, because several of these are questions the port has been
answering by accident rather than on purpose. Ordered by how much they constrain the design.

**1. Per-packet A/V crypto cost on the ARM11 — owed since Phase 1, and now measurable.** Phase 1
measured the control plane at ~25 µs per field and said explicitly that "a real per-packet decrypt
benchmark against Phase 2/6 traffic sizes is still owed before treating software AES on the A/V path as
settled". That benchmark still does not exist, and there is now real traffic to run it against: roughly
4,300 video and 5,900 audio packets per 60-second window at ~1,100 bytes each. Every one costs an
AES-128-CTR pass plus a GMAC over the whole packet. **This is the number that decides whether software
AES can carry a higher bitrate at all**, and without it every discussion of raising resolution above the
bottom rung is guesswork.

*Already found while writing this up:* the connect probe was verifying GMAC **twice per packet** - once
directly and once inside `stream_demux_ingest`'s crypto seam - about 10,000 redundant whole-packet GMACs
a minute. Now sampled at 1-in-16, which keeps the corruption-vs-crypto-fault diagnostic and returns the
rest of the time to the decoder.

**1 and 2 - ANSWERED on hardware, 2026-08-12.** Both were measured in one 60-second window.

**Cores: there are four, and that changes the design.** The probe tries `threadCreate` on each and
reports what took:

```
core 0: available      core 2: available
core 1: no             core 3: available
core 1: available after APT_SetAppCpuTimeLimit(30%)
```

Better than libctru's documentation suggests - cores 2 and 3 need the BASE memory region, which the
Homebrew Launcher's host application evidently has. So decode, packet crypto and the frame scale can all
be moved off the network thread. **This is now a decision to make rather than a constraint to work
around**, and it carries the cost already recorded in this tree: `fec_reed_solomon_decode` keeps 12.6 KB
of scratch `static` *because* this port has no threads, and would need caller-supplied scratch.

**Cost per stage, and it is worse than expected:**

| stage | calls/60 s | us/call | % of one core |
|---|---|---|---|
| demux + decrypt (per packet) | 10,310 | **2,183** | 38% |
| ARM11 scale (per frame) | 335 | **12,691** | 7% *as measured* |
| MVD feed (per NAL) | 1,611 | 1,988 | 5% |
| GMAC verify (per packet) | 645 | **1,264** | 1.4% *sampled 1-in-16* |
| MVD render (per frame) | 1,027 | 197 | 0.3% |

Two of those figures are measured under conditions that will not hold:

- **The scale ran on only ~1/4 of frames**, because the geometry sweep spends most of its time on
  candidates that skip the blit. Resampling is permanent (see item 4), so every frame will pay it:
  1,743 frames x 12.7 ms = **37% of a core**, not 7%.
- **GMAC was sampled at 1-in-16.** Unsampled that is 10,310 x 1,264 us = **22% of a core** - though the
  demux figure already contains a second, unsampled GMAC of its own.

**Realistic total: ~80% of one core at 640x360, before audio, input or any UI.** Single-core is not
viable, which is the answer to the threading question: it is required, not optional.

### Optimisation results, measured on hardware

Both changes beat their host-benchmark projections, because the host has hardware the ARM11 lacks:

| stage | before | after | change |
|---|---|---|---|
| GMAC verify | 1,264 us | **129 us** | **9.8x** |
| demux + decrypt | 2,183 us | **1,070 us** | 2.0x |
| ARM11 scale | 12,691 us | **4,687 us** | 2.7x |
| MVD feed | 1,988 us | 2,151 us | - |
| MVD render | 197 us | 223 us | - |

GHASH was predicted at 5.4x from a host benchmark and delivered **9.8x**, because the bit-serial loop's
cost on ARM11 was dominated by the 16-byte shift the table removes entirely. The scale was predicted at
1.6x and delivered 2.7x, for the reason the prediction flagged: x86 has a divider and the ARM11 does not,
so removing 96,000 `__aeabi_idiv` calls per frame is worth more here than the host could show.

**Whole-pipeline budget, projecting the scale onto every frame and GMAC unsampled: ~80% of one core ->
~41%.** With four cores available (item 2) that is comfortable rather than marginal, and it leaves real
headroom for audio.

**And the packet loss confirms the contention theory.** Unit loss fell from 1-3% to **0.7%** (29 of
4,155) with no network change whatsoever - only CPU work removed. Phase 2 measured that CPU contention on
this core costs UDP throughput; this is the same effect running in reverse, and it is the clearest
evidence yet that the receive loop and the frame pipeline genuinely compete.

**Memory:** the MVD work buffer sized itself at **5,868 KB** against the browser's 9,217 KB default -
3.3 MB returned. The whole media module now reports 6,584 KB.

**GHASH: DONE, 2026-08-12.** `gf128_mul` is now byte-indexed rather than bit-serial - a 256-entry table
of `b * H` plus a 256-entry reduction table, built once per GMAC rotation window (~650 packets) and
cached in `stream_packet_crypto` via a prepared `rc_gmac_key`. Operation count per multiply fell from
~3,000 byte-operations to ~544, and a like-for-like host benchmark of the same work measured
**57.6 us -> 10.7 us, a 5.4x speedup** - which projects the hardware figure from 1,264 us to roughly
235 us per packet, and the demux stage (which contains a GMAC of its own) from 2,183 us to well under
1,000.

Two details worth keeping:
- The reduction table stores only two bytes per entry, because multiplying a block whose only content is
  byte 15 by x^8 leaves a result that is nonzero solely in bytes 0 and 1. That was **verified
  exhaustively over all 256 entries** in a host prototype before being relied on, not assumed from the
  algebra.
- The whole change was validated by the 65 existing cross-language GMAC vectors, which pass unchanged -
  the tags are byte-identical to .NET's `AesGcmCore`. A table-driven field multiply is exactly the kind
  of optimisation that can be subtly wrong on a fraction of inputs, and having known-answer vectors
  already in place is what made it a safe change rather than a risky one.

**The original diagnosis, for the record.** 1,264 us to authenticate a ~1,100-byte packet is very
slow, and the reason is in `rc_gcm.c`: `gf128_mul` is bit-serial - 128 iterations, each doing a 16-byte
shift and a conditional 16-byte XOR, about 3,000 byte-operations per multiply, and a 1,100-byte packet
needs ~69 of them. A nibble-indexed table (the standard approach: precompute n*H for each 4-bit value and
process four bits at a time) is roughly 6x fewer operations. That would take GMAC from ~1,264 us to
~210 us and the demux stage - which contains a GMAC of its own - from 2,183 us to under 1,000, moving the
whole budget from ~80% of a core to nearer 50% **before** any threading work. It is also the safest
change available, because `tests/stream_crypto_test.c` already checks GMAC against 65 cross-language
known-answer vectors, so a wrong optimisation fails loudly on the host.

**The scale stage: partly fixed, and the first guess was wrong.** 96,000 pixels at ~132 ns each is far
too slow for a copy, and the cache-stride theory above turned out to be at most half the story. The
actual inner loop computed the source row as `((y - y_offset) * src_h) / draw_h` **per pixel**, and
**the ARM11 has no hardware divider** - that expression compiles to a `bl __aeabi_idiv` function call,
executed 96,000 times a frame, plus a multiply and a branch.

Both maps are now precomputed once (400 + 240 entries, rebuilt only if the geometry changes), with
`map_y` holding the source row's *offset* rather than its index so the multiply disappears too, and the
letterbox bands written as their own runs instead of tested for inside the pixel loop. A host benchmark
of the same two loops measures 1.6x - and understates the ARM11 case badly, because x86 has a divider
and the 3DS does not.

**What is left is memory traffic, and it should be measured before it is optimised.** Reads walk a
column of the source with a 1,280-byte stride, so nearly every pixel touches a fresh cache line;
reordering the loops only moves that cost to the writes, since one side or the other must be strided
when converting a row-major frame into a column-major framebuffer. Tiling would genuinely reduce both,
and the PICA200 would remove the cost entirely - but the next hardware run will say how much of the
12.7 ms was division and how much is memory, and that decides whether either is worth doing.

**2. Which cores this port may actually use.** Everything runs on one thread today, and the receive loop
has already been the direct cause of one large loss regression. libctru's `threadCreate` takes a core id
and documents the constraints: processor 0 is always available, processor 1 needs
`APT_SetAppCpuTimeLimit`, **processor 2 is New3DS-only and needs exheader kernel flag 0x2000** - which
for a `.3dsx` means whatever the Homebrew Launcher's host application carries, so it has to be probed
rather than assumed. A ten-line experiment (try to create a thread on cores 1 and 2, report what
succeeds) settles what the threading design is even allowed to be.

**This has a known cost, already documented elsewhere in the tree:** `fec_reed_solomon_decode` keeps 12.6
KB of scratch matrices `static` specifically because this port has no threads. Introducing one means that
function needs caller-supplied scratch. Deciding the threading model *before* the media pipeline grows is
much cheaper than retrofitting it.

**3. Memory budget - PARTLY ADDRESSED.** MVD's work buffer no longer takes the browser's 9.0 MB default:
`mvdstdCalculateBufferSize` now sizes it for this stream (H.264 level 3.1 at the negotiated resolution),
falling back to the default if the calculation fails or returns something implausible - a decoder that
will not start is worse than one that is generous. The module reports its own total at startup, so the
figure stops being something anyone has to reconstruct from three headers.

Still fixed and unexamined: `stream_demux` at 513 KB, two Takion channels at ~50 KB each, and now ~9.6 KB
of GHASH tables (two prepared keys at ~4.8 KB). None of those is worth attacking until the work buffer
figure comes back from hardware, since it dominates everything else combined.

**5. Frame pacing - DONE.** Frames were being swapped the instant a decode completed, at whatever rate
they happened to arrive. That both tears and wastes work: swapping more often than the display refreshes
is effort thrown away on a port already measured at ~80% of a core. Presentation is now rate-limited to
the 60 Hz interval, and the run reports frames presented per second alongside frames decoded, so the two
can be compared.

**`gspWaitForVBlank` is deliberately NOT used.** It would block the receive loop for up to 16.7 ms, and a
stalled drain is precisely what caused this port's largest packet-loss regression (see the Phase 6d note
on the one-packet-per-tick drain). The swap is throttled while the loop keeps draining; an early frame
waits in the back buffer rather than stalling the network.

**6. Audio - scoped, not started.** The pieces are all present and the shape is clear:

- **`3ds-libopus` is installed** (`dkp-pacman` package `3ds-libopus`, 1.4-1), so no new dependency
  argument is needed - and unlike mbedtls it carries no seam question, since Opus is a pure decoder with
  no platform entanglement.
- **NDSP is the output path** (`ndspInit`, `ndspChnSetFormat`, `NDSP_FORMAT_STEREO_PCM16`), which is
  what 48 kHz stereo wants.
- **The demuxer already delivers the frames.** `audio_frame_ready` fires ~1,900-6,000 times per window
  in real runs, with the redundant loss-concealment units already stripped down to the one real Opus
  frame per packet. The 14-byte audio header from STREAM_INFO is the decoder configuration.

So the work is: decode Opus frames to PCM16 and feed NDSP, with a small ring buffer to absorb jitter.
**The open question is cost, and it should be measured before the threading model is settled** - Opus
at 48 kHz on an ARM11 lands on the same core budget as the packet crypto and the frame scale, and item 2
established there are four cores to spread across. Deciding audio's home at the same time as video's is
much cheaper than moving it later.

**3. The memory budget, which is larger than it looks.** MVD's work buffer alone is
`MVD_DEFAULT_WORKBUF_SIZE` = **9.0 MB**. Add `stream_demux` at 513 KB, two Takion channels at ~50 KB
each, the MVD input staging at 256 KB and its output buffer at ~460 KB, and the port is holding ~10.3 MB
of fixed allocations before audio, input or any UI exists. `mvdstdCalculateBufferSize` can compute a
smaller work buffer from the actual level and reference-frame count instead of using the browser's
default - worth doing once the decoder works, and worth knowing about now.

**4. Which resolutions the console will negotiate - ANSWERED on hardware, 2026-08-12.** Six candidates,
one full connect each (`proberesolutions=1` in pairing.txt):

| Asked | Result |
|---|---|
| 640x360 | accepted as asked |
| 400x240 | refused |
| 320x180 | refused - encoder error |
| 480x270 | refused - encoder error |
| 512x288 | refused - encoder error |
| 960x540 | **clamped to 640x360** |

**The console says why, and it is a capability limit rather than a policy one.** Every non-ladder
resolution produced the same DISCONNECT reason:

```
Nagare did not init! AvCap failed to initialize video: [InitResult:-5]
```

That is the console's own video capture/encoder failing to initialise, so this is not something a
different launch spec can talk it out of. Note also that 400x240 and 512x288 are both perfectly
macroblock-aligned and still refused - alignment was never the criterion, the standard ladder is.

**Consequence, and it is a permanent one: scaling on this side is mandatory.** MVD will not scale
(established in Phase 6d) and the console will not send anything the screen can display directly. So
every frame must be resampled by the ARM11 or the PICA200 between decode and display, forever. That
moves a per-frame cost from "possible optimisation" into the fixed budget, which is exactly what makes
items (1) and (2) below load-bearing rather than nice-to-have.

**One thread worth pulling:** 960x540 came back as 640x360 rather than refused, and the launch spec was
asking for only 2000 kbps at the time. The choice may be bandwidth-driven rather than purely
request-driven, in which case a higher `streambitrate` might unlock a higher rung later - relevant for
quality, not for the scaling question, which is settled either way. One probe run with
`streambitrate=8000` would answer it.

*(The probe also showed its own flakiness: back-to-back sessions produced `/sess/init -> 403` and
`ECONNRESET` when the console had not finished tearing the previous one down. The inter-session gap is
now 5 s rather than 2 s.)*

**5. Frame pacing.** Frames are presented the moment a decode completes, with no relationship to vblank.
Whether that tears, and whether the decoder should present on the display's clock instead, is unmeasured.

**6. Audio, which is entirely unstarted.** Opus at 48 kHz stereo on an ARM11, with `3ds-libopus` not
installed by default. Its decode cost lands on the same core budget as (1) and (2) and should be measured
before the threading model is fixed, not after.

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
