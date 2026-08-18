# ripcord-vita

A PS5 Remote Play client for homebrew-enabled PS Vita hardware, in C, sharing Ripcord's protocol
specification **and**, unlike the 3DS port, its protocol code.

**Status: it builds. Nothing has been run on hardware.** As of 2026-08-17 `make` produces
`ripcord-vita.vpk` — the whole portable core (all 36 files, first try, under `-Werror -Wconversion
-Wsign-conversion`), the platform seam, and a Phase 1 crypto smoke test, through vitasdk's
elf→velf→eboot→vpk chain. What that proves is that the extraction into [`ports/common`](../common) was
real; what it does not prove is that a single instruction of it does the right thing on a Vita.

All four Phase 0 questions are answered:

| Question | Answer |
|---|---|
| Sockets: POSIX names or `sceNet*`? | **POSIX works.** Compiles *and links* — no socket seam needed `[C]` |
| CSPRNG | `sceKernelGetRandomNumber` (`psp2/kernel/rng.h`) `[C]`, but see the 64-byte question below |
| ECDH backend | **Cross-built** — vitasdk packages no mbedtls, so we build the ECP/MPI layer (~23 KB) from a pinned, hash-verified 2.28.8 `[C]` |
| Main-thread stack | Mechanism written and the symbol verified present in the binary — after it was silently dropped once `[C]` |

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

### Sockets — answered, and the answer was free

This was expected to be the expensive one. vitasdk's *documented* surface is `sceNetSocket` /
`sceNetBind` / `sceNetRecvfrom` — the same BSD shapes under a prefix — which implied a socket seam of
roughly twelve mapped call names.

It ships real POSIX headers as well (`sys/socket.h`, `netinet/in.h`, `arpa/inet.h`, `fcntl.h`,
`poll.h`), and a program using the POSIX names **compiles *and* links** against `-lSceNet_stub` `[C]`.
Checked by compiling and linking, not by reading documentation, because a header declaration is not a
link-time symbol. `inet_aton` is among them, which retires trap 4 above for this platform.

So the shared core's socket code needs no changes and
[`rc_platform.h`](../common/platform/rc_platform.h) stays four functions.

Two things this does **not** settle, both still `[X]`:

- Whether `sceNetInit`'s memory pool has to be up before the POSIX names work. Almost certainly yes —
  the 3DS's `socInit()` is the same shape — and it belongs in this port's own bring-up file, not the
  seam.
- **Whether `bind()` to port 0 is accepted.** The 3DS rejects it, which is correct on .NET and on Unix,
  and cost a hardware run to discover. No documentation either way for the Vita.

"It compiles" is not "it works", and sockets are exactly where that gap lives.

### What building it actually found

Two defects, both caught before hardware, both of the kind that produce no build error:

1. **The main-thread stack size symbol was silently dropped.** `sceUserMainThreadStackSize` is read by
   the *loader*; nothing in the program references it. Under `-fdata-sections` it lands in its own
   section and `-Wl,--gc-sections` collects it as unreachable — build succeeds, `.vpk` is produced,
   stack is silently the 4 KiB default, and the first symptom would have been a crash deep in the audio
   loss-concealment path on hardware. Verified with `nm`: present in the object, absent from the linked
   ELF. Fixed with `-Wl,--undefined=sceUserMainThreadStackSize`. (`__attribute__((retain))` is the
   self-contained fix and is unavailable — GCC 15.2 accepts it but vitasdk's binutils does not support
   the section flag.)
2. **`rc_program_dir`'s fallback was hardcoded to `"sdmc:/"`**, a 3DS device path, sitting in the
   *portable* core. Invisible on 3DS forever; on Vita it is a nonexistent device and the only symptom
   would have been a log file that never appeared. Now `RC_PROGRAM_DIR_FALLBACK`, defined per port.

And one flag correction worth recording because it is the **exact inverse of the 3DS**: devkitARM needs
`-march`/`-mfloat-abi` spelled out because libctru is built with them, while vitasdk's compiler already
defaults to `armv7-a+simd` / `cortex-a9` / `neon` / **hard** float and its own CMake toolchain sets no
arch flags at all. Passing `-mfloat-abi=softfp` — a faithful-looking description of the hardware, and a
different *calling convention* — failed the link on every translation unit.

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
source/platform/    rc_platform_vita.c   the seam: clock, sleep, tick
                    rc_random_vita.c     the CSPRNG, chunked at 64 bytes
                    rc_stack_vita.c      the main-thread stack size, raised before it could crash
source/app/         Phase 1 on-device crypto smoke test (ripcord-vita.vpk)
```

Everything else comes from [`ports/common`](../common), which holds the protocol core shared with the
3DS port: `crypto/`, `halyard/`, `stream/`, `takion/`, `session/`, `discovery/`, `input/`, `net/rc_tcp.c`
and the portable half of `util/`. What a port owes on top of that is its platform: the seam above, a
socket bring-up, a CSPRNG, video decode, audio out, present, and one `main.c` per program.

## The plan from here

Phases mirror the 3DS port's, because that ordering was earned — each phase ends at a question only
hardware can answer, and none of them assumes the next one works.

- **Phase 0 — toolchain. Done (2026-08-17).** vitasdk installed; the portable core, the seam and the
  ECDH backend all compile; `make` produces a `.vpk` that links P-521. Sockets, the CSPRNG and the
  ECDH backend are all answered.
- **Phase 0.5 — a way to see output.** The smoke test writes to `ux0:data/ripcord/smoke-test.log`
  because vitasdk ships no debug-screen printf (`psvDebugScreen` is a samples/common file, not SDK).
  That is fine for a log and useless for a HUD, and every phase from 2 onward wants a screen. Either
  vendor a debug screen or take `vita2d` from `vdpm`.

### The ECDH backend — cross-built, not packaged

The shared core delegates exactly one primitive — ECDH over P-256/P-521 — to a third-party library, for
the reason [`rc_ecdh.h`](../common/crypto/rc_ecdh.h) gives at length: a hand-written constant-time
bigint is the last thing this project should write twice. The 3DS reaches devkitPro's `3ds-mbedtls`.
**vitasdk packages no mbedtls** `[C]` (its `vdpm` list has `openssl` and `libsodium`; libsodium is
Curve25519/Ed25519 and cannot do the NIST curves at all).

So this port builds its own, via
[`ports/common/tools/build-mbedtls.sh`](../common/tools/build-mbedtls.sh):

- **Only the ECP/MPI layer** — five translation units (`bignum`, `ecp`, `ecp_curves`, `platform_util`,
  `constant_time`), ~23 KB of ARM text. No TLS, no X.509, no cipher suites, no entropy source. The
  allow-list config is [`ripcord-mbedtls-config.h`](../common/tools/ripcord-mbedtls-config.h), and the
  module list was established by compiling and linking `rc_ecdh.c` against it rather than by reading
  upstream's makefile.
- **Pinned to 2.28.8 to match devkitPro's package**, so one `rc_ecdh.c` serves both ports. (3.x moved
  struct fields behind accessors and would need a second code path.)
- **Fetched and SHA-256 verified at build time, never vendored.** Same reasoning that keeps the interop
  constants generated rather than copied — a checked-in third-party crypto tree is a provenance and
  maintenance liability. The hash is checked *before* extraction, because an unverified tarball would
  make the pin decorative.
- `ECDH_BACKEND=none` still builds, and `rc_ecdh_available()` then returns 0 rather than a stub faking
  a key agreement.

**One config choice is security-relevant and is documented where it is made.** `MBEDTLS_ECP_NO_INTERNAL_RNG`
looks like it disables scalar-multiplication blinding and does not: mbedtls falls back to its own DRBG
for blinding *only when the caller passes `f_rng == NULL`*, and all three call sites in `rc_ecdh.c`
pass a real RNG. That was checked before setting it. If a call site ever passes NULL, the define
becomes a genuine vulnerability rather than a build detail.

**It also fixed the host suite.** `ecdh_test` used to skip on any machine without `libmbedtls-dev`,
which quietly meant the P-256/P-521 agreement and the derived stream keys went unchecked precisely
where nobody would notice. The same script builds for the host (`CROSS=`), so the suite is now
**3,287 assertions with nothing skipped**, on a machine with a C compiler and no packages installed —
which is what `rc_crypto.h` always claimed.

**A second, smaller open question remains.** `sceKernelGetRandomNumber`'s documented maximum request is
reported as 64 bytes `[X]`, and this port's largest single ask is a P-521 private key at 66.
`rc_random_vita.c` chunks at 64 rather than assuming. If the call short-fills silently instead of
erroring, the smoke test's two ECDH secrets would still *match* — both sides equally weak — so a
matching pair is necessary, not sufficient. Confirm on hardware.

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
