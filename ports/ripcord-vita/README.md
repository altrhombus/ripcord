# ripcord-vita

A PS5 Remote Play client for homebrew-enabled PS Vita hardware, in C, sharing Ripcord's protocol
specification **and**, unlike the 3DS port, its protocol code.

**Status: started 2026-08-17. Nothing has been built, and nothing has been run.** What exists is the
platform seam (`source/platform/rc_platform_vita.c`, never compiled), this document, and the extraction
of the shared core into [`ports/common`](../common) that made a second port worth attempting at all.
Every hardware claim below is marked `[C]` or `[X]`, and the `[X]`s outnumber the `[C]`s on exactly the
questions that matter.

## Why this exists, and why the reason differs from the 3DS port's

The [3DS port](../ripcord-3ds) exists as a **completeness test for `docs/protocol/`**: a second client,
written from the spec in a different language on a different OS, to find out which parts of that
document were load-bearing and which only looked complete because the author already knew the answer.
It worked — it found four unmodelled control-channel message types, a real defect in our Takion
continuation-fragment handling, and promoted three assumptions to `[V]`.

**This port cannot do that, and should not pretend to.** It reuses the 3DS port's C. Running the same
code on a second machine re-tests the *seam*, not the spec.

So the honest reason is different: **the Vita is the first target where the hardware is not the
limiting factor.** The 3DS port is a proof; this one could be a good way to play a PS5 game.

| | 3DS | Vita |
|---|---|---|
| Screen | 400×240 — most source rows discarded on the way down | **960×544** — the stream is already requested at 960×540 |
| Decode | MVD block, undocumented, 1,376 lines and a war diary | `sceAvcdec`, a conventional decoder API |
| Sticks | one, plus a C-stick that is a poor right stick | **two real analog sticks** |
| Touch | one touchscreen | touchscreen **and a rear touchpad** |
| CPU | ARM11 @ 804 MHz, no NEON | **quad Cortex-A9 @ up to 444 MHz, with NEON** |
| RAM | 128 MB, and a 32 KB main stack that crashed twice | 256 MB `[X]`, raisable to 365 MB `[X]` |

There is a secondary benefit, and it is real but smaller: a second consumer proves `ports/common`'s
platform seam is a seam rather than a rename. That already paid for itself before any Vita code was
written — pulling the core out surfaced two portability defects (below) that the 3DS build had been
hiding.

## Can the hardware actually do this?

**Vita 1000 / 2000 handheld is the target.** PlayStation TV is not, for the ordinary reason that the
project has no PSTV to test on.

| | Budget | Assessment |
|---|---|---|
| CPU | Quad-core ARM Cortex-A9, ARMv7-A, 2 MB L2 `[C]` | Retail default **333 MHz**, raisable to **444 MHz** (and the silicon reaches 500) via `scePowerSetArmClockFrequency` `[C]`. Comfortably ahead of the ARM11 that already handles the control plane in ~25 µs per field |
| Crypto | NEON, but **no ARMv8 crypto extensions** `[C]` | AES-128 stays in software, exactly as on 3DS. `VMULL.P8` *is* baseline ARMv7 NEON `[C]` and can build a GF(2^128) carryless multiply for GHASH — slower than PMULL, but the 3DS has no equivalent at all |
| Video | `sceAvcdec` hardware H.264 `[C]` | **No HEVC path exists at any level** — `SCE_VIDEODEC_TYPE_HW_AVCDEC` is the only codec type in the header `[C]`. HEVC must be refused at negotiation, same as 3DS. Handles 1280×720 comfortably; 1080p only at 30 `[X]` |
| Colour | Decoder outputs `YUV420_RASTER` **or `RGBA8888` directly** `[C]` | Potentially removes the 3DS's whole MVD→Y2R→PICA200 dance. Whether hardware RGBA conversion is free or costs decode throughput is `[X]` |
| Screen | 960×544, OLED (1000) / LCD (2000) `[C]` | Effectively native. The 3DS discards most source rows; this does not |
| Wi-Fi | 802.11 b/g/n, **2.4 GHz only** `[C]` | Real-world figures are anecdotal, ~10–25 Mbps `[X]`. Must be measured, exactly as the 3DS's were |
| Audio | `sceAudioOut`, 48 kHz `[C]`; Opus in vitasdk's `vdpm` `[C]` | Affordable, and the 3DS port's Opus integration ports directly |
| RAM | 256 MB default budget `[X]`, → 365 MB via SFO `ATTRIBUTE2=12` `[X]` | The 513 KB `stream_demux` that had to be forced into `.bss` on 3DS is a non-issue |

### The traps we already know about, because the 3DS found them

These are not speculation. Each is a bug the 3DS port hit on real hardware, and each has a Vita analogue
that should be pre-empted rather than rediscovered:

1. **Main-thread stack size.** libctru defaults to 32 KB; the 3DS port needed **128 KB** and crashed
   twice before getting there — the second time from cumulative call depth, not one large frame, along
   the audio loss-concealment chain (`run_media → takion_channel_poll → ingest_audio → rc_audio_submit
   → opus_decode → celt_decode_lost`, ~12.4 KB in five frames). The Vita equivalent is the
   `sceUserMainThreadStackSize` export, whose default is reported as **4 KiB** `[X]`. That same call
   chain will exist here. Set this before writing a session, not after the first crash dump.
2. **Binding port 0.** Correct on .NET, correct on Unix, rejected outright by the 3DS SOC service —
   which cost a hardware run. Whether `sceNetBind` accepts port 0 is **unconfirmed** `[X]` and no
   documentation was found either way. Assume nothing.
3. **The network stack needs a memory pool up front.** `sceNetInit` takes an explicit pool (~1 MB in
   vitasdk's own sample) `[C]`, directly analogous to the 3DS's 0x100000 aligned SOC buffer.
4. **`inet_aton` is a BSD extension, not C99.** Two call sites in the shared core (`net/rc_tcp.c`,
   `session/halyard_control_session.c`). vitasdk's documented helper is `sceNetInetPton` `[C]`. Found
   for free by compiling the core on a Linux host during the extraction — the 3DS build had been hiding
   it because libctru declares it unconditionally.

### Sockets are the largest open question

vitasdk's documented socket surface is `sceNetSocket` / `sceNetBind` / `sceNetRecvfrom` / … — the same
BSD shapes under a prefix, with `sceNetEpoll*` in place of `poll()` `[C]`. **Whether vitasdk also ships
POSIX-named wrappers is unconfirmed** `[X]`, and it is the single largest unknown in the seam: if it
does not, roughly twelve call names in the shared core need mapping and
[`rc_platform.h`](../common/platform/rc_platform.h) grows a socket section. Non-blocking mode is
`fcntl(O_NONBLOCK)` in the core today (10 call sites); the Vita equivalent may be
`sceNetSetsockopt(SCE_NET_SO_NBIO)` `[X]`.

**Resolve this against a real vitasdk install before writing the transport, not after.**

### Controls, and what a Vita cannot express

Native Vita hardware has **one shoulder button per side and no clickable sticks** — there is no
physical L2/R2 and no L3/R3 `[C]`. The `SceCtrlButtons` enum does define `L2`/`R2`/`L3`/`R3` bits, but
they exist for PlayStation TV with a real DualShock; on a handheld the shoulder buttons map to `L1`/`R1`
and the rest never assert `[C]`. So out of the box this port owes the same compromise the 3DS port
makes: no analog triggers, no stick clicks.

What it gains over the 3DS is substantial: **two real analog sticks**, a **front touchscreen and a rear
touchpad** (6 and 4 simultaneous points respectively `[C]`) which between them can carry a DualSense
touchpad far better than one screen can, and a full **gyro + accelerometer + magnetometer** via
`sceMotion` `[C]`.

**On pairing a real controller over Bluetooth:** this is the intended way to test the full input path,
and it needs a **taiHEN kernel plugin** (`ds4vita`-class) rather than anything vitasdk provides — a
handheld Vita will not pair a DualShock on its own; that is a PSTV feature `[C]`. Whether such a plugin
surfaces analog L2/R2 and L3/R3 through `sceCtrl`'s extended read functions, or flattens them to the
handheld's button set, is **unconfirmed** `[X]` and is the thing to establish first, because it decides
whether the input path can be tested completely on this hardware at all.

### Pairing happens on a PC, not here

Same as the 3DS, for the same reason: registration needs PSN OAuth and TLS, which buys a handheld
nothing. Pair with desktop Ripcord and copy the pairing record across. The registration constants
therefore never need to exist in this binary, and `ports/common/tools/gen_constants.py` deliberately
does not emit them.

## Layout

```
source/platform/    the rc_platform.h seam, vitasdk side  <- the only file that exists today
```

Everything else comes from [`ports/common`](../common), which holds the protocol core shared with the
3DS port: `crypto/`, `halyard/`, `stream/`, `takion/`, `session/`, `discovery/`, `input/`, `net/rc_tcp.c`
and the portable half of `util/`. What a port owes on top of that is its platform: the seam above, a
socket bring-up, a CSPRNG, video decode, audio out, present, and one `main.c` per program.

## The plan from here

Phases mirror the 3DS port's, because that ordering was earned — each phase ends at a question only
hardware can answer, and none of them assumes the next one works.

- **Phase 0 — toolchain.** Install vitasdk. Compile `source/platform/rc_platform_vita.c` and fix what
  the headers disagree with. Answer the socket question (POSIX names or `sceNet*`?), the
  `rc_random_bytes` question, and the ECDH-backend question below. **Nothing below starts until those
  are settled.**

### The ECDH backend is an open decision, and it is not mbedtls

The shared core delegates exactly one primitive — elliptic-curve Diffie-Hellman over P-256/P-521 — to a
third-party library, for the reason [`rc_ecdh.h`](../common/crypto/rc_ecdh.h) gives at length: a
hand-written constant-time bigint is the last thing this project should write twice. The 3DS reaches
devkitPro's `3ds-mbedtls`, and uses only the `mbedtls_ecp_*` and `mbedtls_mpi_*` layers — no TLS, no
X.509, no SSL, just curve arithmetic and bignums.

**vitasdk does not package mbedtls** `[C]` (checked against `vdpm`'s package list, 105 packages, 2026-08-17).
It packages `openssl`, and `libsodium` — but libsodium is Curve25519/Ed25519 and cannot do P-256 or
P-521, so it is not a candidate. Three options, none yet chosen:

1. **OpenSSL backend.** Packaged and maintained, with full P-256/P-521 support via `EC_GROUP`/
   `EC_POINT_mul`/`BN_*`. Costs a third `#if` branch in `rc_ecdh.c` — which is what the seam is for —
   and pulls in a large library for four operations.
2. **Cross-build mbedtls for Vita.** The ECP/MPI layer is portable C with no OS dependency, and this
   port needs only that layer. Keeps one backend across both ports; costs a build step vitasdk does not
   provide.
3. Something else entirely (a small dedicated EC library). Unexplored.

Whichever wins, `rc_ecdh.c`'s existing contract holds: without a backend it must fail cleanly and
`rc_ecdh_available()` must return 0. **It must never substitute a stub that fakes a key agreement** — a
build that negotiates a session with no confidentiality is far worse than one that does not link.
- **Phase 1 — first boot.** A `.vpk` that runs the crypto self-test on device and reports what a control
  field encryption costs on a Cortex-A9. The 3DS's number is ~25 µs; this is the comparison that says
  whether the A/V path's software AES is affordable.
- **Phase 2 — link test.** Port `linktest/main.c`. Measure real UDP receive throughput and loss, idle
  and under load. The 3DS's anecdotal Wi-Fi expectations were wrong in both directions; assume these are
  too.
- **Phase 3 — discovery.** SRCH probe on the LAN. First contact with a console, and the first place a
  socket idiom will bite.
- **Phase 4 — `/sess/init` → `/sess/ctrl`.** First use of the crypto against a real console, and the
  single largest de-risking event the 3DS port had.
- **Phase 5 — Takion + stream keys.** The connect flow through to derived per-direction AES keys.
- **Phase 6 — media.** `sceAvcdec` H.264, `sceAudioOut` Opus, `sceCtrl` input.
- **Phase 7 — what the Vita can do and the 3DS cannot.** 960×544 without downscaling, both sticks, the
  rear touchpad as a real DualSense touchpad, gyro.

An early cross-cutting task worth doing before Phase 5: **split the portable orchestration out of the
3DS's `connect/main.c`.** It is 2,074 lines, and most of it — `service_control`,
`wait_for_session_ready`, `check_sign_in_gate`, `await_control_message`, `seal_control_packet`, the
stream callbacks — is flow logic with no 3DS in it. Moving that into `ports/common` means this port
writes a thin shell instead of re-deriving the connect flow, and it is the difference between owing
~6,000 lines of platform code and owing roughly 2,500.

## Clean-room: a sharper constraint here than on the 3DS

Read [`CLAUDE.md`](../../CLAUDE.md)'s clean-room section before touching anything protocol-related. Two
points specific to this port:

**Reusing the 3DS port's code is explicitly allowed and is not a clean-room concern.** `CLAUDE.md`
states it directly: a same-project port may read, port and adapt code from Ripcord's own tree at will,
because that is this project's own reference implementation and not the external source the rule exists
to keep out. That exemption is what `ports/common` relies on.

**There is an existing third-party Remote Play client for this hardware, and it is off-limits.** Its
source, its constants, its wire-format notes, and any page describing the protocol as it implements it
are all outside the boundary — the exemption above covers *Ripcord's* ports, not another project's. The
temptation is sharper here than it was on the 3DS precisely because a same-platform implementation
exists to compare against, and "just checking how they did it" is exactly the contaminated route
`docs/protocol/` cannot afford. If a protocol question comes up that our own spec and captures cannot
answer, it is an open question to mark `[X]` and derive — not a value to go and look up.

**Platform facts are a different matter and are fair game.** vitasdk headers, henkaku's wiki, and
homebrew for unrelated protocols are ordinary engineering references. The 3DS port already set this
precedent in [`HARDWARE-PROBES.md`](../ripcord-3ds/HARDWARE-PROBES.md), which cites a GPL-3.0 3DS video
player for MVD call sequencing under "read for facts, nothing copied". The line is the protocol, not the
platform.
