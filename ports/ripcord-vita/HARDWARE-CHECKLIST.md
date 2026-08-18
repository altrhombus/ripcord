# ripcord-vita — the hardware checklist

Everything built so far was written and verified **without a Vita**. This file is the list of what that
leaves unanswered, in the order you will hit it, with what to run and what failure looks like.

It exists because the 3DS port taught the lesson twice: the bugs that cost hardware runs were not
algorithm bugs, they were *idioms that are correct on the reference platform and invalid on the target*
— binding port 0, a nanosecond argument, a 32 KB stack. No vector catches those. Only a device does.

Written 2026-08-17, when the work paused for want of hardware.

---

## Getting back to where we left off

Neither SDK exports its environment variables on login, which cost time once already:

```sh
export DEVKITPRO=/opt/devkitpro DEVKITARM=/opt/devkitpro/devkitARM
export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$DEVKITPRO/tools/bin:$DEVKITARM/bin:$PATH"

# from the repo root
dotnet run --project tools/Ripcord.ProtocolLab -- vectors   # regenerate the KAT vectors
make -C ports/common/tests            # expect: 3,287 assertions, 0 failed, 0 skipped
make -C ports/common/tests compile    # expect: "the entire portable core compiles on the host"
make -C ports/ripcord-vita            # expect: ripcord-vita.vpk
make -C ports/ripcord-3ds             # expect: seven .3dsx
```

`make -C ports/ripcord-vita clean` deliberately **keeps** `build/mbedtls`; `distclean` re-fetches it.

Branch: `feat/vita-port`. Nothing here is merged.

---

## What is already settled, so nobody re-checks it

| | |
|---|---|
| Sockets | vitasdk ships POSIX names that **link** against `-lSceNet_stub`, `inet_aton` included. No socket seam. `[C]` |
| CSPRNG | `sceKernelGetRandomNumber`, `psp2/kernel/rng.h` `[C]` |
| Clock / sleep / tick | `sceKernelGetProcessTimeWide` (µs), `sceKernelDelayThread` (µs) `[C]` |
| ECDH | mbedtls 2.28.8 ECP/MPI cross-built, ~23 KB, links and passes 44 host vectors `[C]` |
| Compiler flags | **Pass no `-march`/`-mfloat-abi`.** vitasdk already defaults to `armv7-a+simd`/`cortex-a9`/`neon`/**hard** float; `softfp` is a different calling convention and fails the link `[C]` |
| Portable core | All 36 files compile for Cortex-A9 under `-Werror -Wconversion -Wsign-conversion` `[C]` |

---

## 1. First boot — before anything else

**Create `ux0:data/ripcord/` on the card first.** The Vita will not create it, and `rc_log_open` fails
silently to "screen output only" — which on this port means no output at all, because there is no screen
yet (§2). A first run that appears to do nothing is most likely this.

Install `ripcord-vita.vpk` with VitaShell, run it, then copy `ux0:data/ripcord/smoke-test.log` off.

### 1a. Does the loader honour `sceUserMainThreadStackSize`? — the highest-risk unknown

**Why it matters more here than on the 3DS.** The 3DS defaulted to 32 KB, needed 128 KB, and crashed
*twice* getting there — the second time from cumulative call depth, not one large frame, down the audio
loss-concealment chain (`run_media → takion_channel_poll → ingest_audio → rc_audio_submit → opus_decode
→ celt_decode_lost`, ~12.4 KB in five frames). **That chain is shared code and will exist here**, and
the Vita's default is reported as **4 KiB** — an order of magnitude tighter than the one that already
failed.

`source/platform/rc_stack_vita.c` sets 256 KB, and the symbol is **verified present in the linked ELF**
(it was silently collected by `--gc-sections` once; `-Wl,--undefined=` now pins it). What is *not*
verified is that the loader reads it. No vitasdk header declares the name — it is defined by the
application — so **a wrong name compiles, links, and is ignored.**

- **Check:** add a temporary recursive function with a ~2 KB frame, recurse ~40 times (≈80 KB), and see
  whether it survives. Under a 4 KiB stack it dies almost immediately.
- **Failure looks like:** a crash with no log, or a log that stops mid-line — the 3DS's exact signature.
- **If it fails:** the mechanism or the name is wrong. Check how other vitasdk homebrew declares it
  before assuming the value is too small.

### 1b. Does `sceKernelGetRandomNumber` short-fill past 64 bytes?

Its documented maximum request is reported as **64 bytes**; this port's largest single ask is a **P-521
private key at 66**. `rc_random_vita.c` chunks at 64 rather than assuming — but if the call instead
*short-fills without reporting an error*, that loop is the only thing between this port and predictable
key material.

**The smoke test's ECDH round trip does NOT catch this.** If both sides get equally weak keys they still
agree, and the check passes. A matching pair is necessary, not sufficient.

- **Check:** fill a 66-byte buffer with `0xAA`, request 66 bytes, and confirm the last two bytes changed
  *and* the call returned success. Then request 65 bytes in one call and see whether it errors.
- **Failure looks like:** trailing bytes still `0xAA` with a success return.

### 1c. The number this program exists for

`smoke-test.log` reports µs per control-field encryption. **The 3DS/ARM11 @ 804 MHz measured ~25 µs.**
A Cortex-A9 should be well under that; how far under is the first real input to whether software AES on
the A/V path is affordable here. If it lands in *milliseconds*, that is a finding, not a rounding error.

Also confirm: constants bundled, KDF deterministic, round trips at all eight lengths, counter affects
the keystream, and **P-521 ECDH agrees at 66 bytes** — the last of which exercises the cross-built
mbedtls end to end.

### 1d. Clock

`scePowerSetArmClockFrequency(444)` is available `[C]` but not called anywhere yet. The 3DS port calls
`osSetSpeedupEnable(true)` first thing in `main()`. Worth adding once §1a is trusted — and worth
measuring §1c both with and without it, since the retail default is 333 MHz.

---

## 2. A way to see output — before Phase 2

vitasdk ships no debug-screen printf (`psvDebugScreen` is a samples/common file, not SDK). Logging to
the card is fine for a smoke test and useless for a link test or a HUD, and every phase from here wants
a screen. Either vendor a debug screen from the samples or take `vita2d` from `vdpm`.

---

## 3. Network — Phase 2/3

### 3a. Does `sceNetInit`'s pool have to be up before the POSIX names work?

Almost certainly yes — the 3DS's `socInit()` is the same shape, and vitasdk's own sample allocates ~1 MB.
This port has **no bring-up file yet**; the 3DS's is `source/net/rc_soc.c` and it is the model. The POSIX
socket names *link* without it, which says nothing about whether they *work* without it.

### 3b. Is `bind()` to port 0 accepted?

**This is the one that already cost a hardware run on the 3DS.** Letting the stack pick an ephemeral
port is correct on .NET and on Unix, and the 3DS SOC service rejects it outright with `EINVAL`. No
documentation was found either way for the Vita.

- **Check it deliberately and early**, on its own, before it can be confused with a protocol bug.
- **If rejected:** bind a fixed port with retry, as `source/discovery/main.c` does on 3DS (9310, retrying
  to 9313). A console answers SRCH to whatever source port the probe came from, so any port works.

### 3c. Wi-Fi throughput

802.11 b/g/n, **2.4 GHz only** `[C]`. Real-world figures are anecdotal (~10–25 Mbps) `[X]`. Port
`linktest/main.c` and pair it with `ports/common/tools/udp_link_test_sender.py`, exactly as the 3DS did.

Two things the 3DS learned that transfer: **measure idle *and* under CPU load** (contention cost it real
throughput), and **do not drain the receive queue on the vblank clock** — its first run produced p99
numbers that were purely an artefact of the harness, not the link.

---

## 4. Media — Phase 6

| | Unverified |
|---|---|
| `sceAvcdec` | Handles 1280×720 comfortably; 1080p only at 30 `[X]`. Both from a third-party project's observations, not our measurement |
| Output format | Decoder can emit `YUV420_RASTER` **or** `RGBA8888` directly `[C]` — but whether hardware RGBA costs decode throughput is `[X]`. If it is free it removes the 3DS's whole MVD→Y2R→PICA200 dance |
| HEVC | **There is none, at any level** `[C]`. Must be refused at negotiation, same as 3DS |
| `sceAudioOut` | Only 48 kHz / grain 256 is confirmed (from vitasdk's own sample); other rate/grain combinations `[X]` |
| RAM budget | 256 MB default, → 365 MB via SFO `ATTRIBUTE2=12` `[X]`. Both from a Rust-toolchain doc, not the C SDK's |

The 3DS's `HARDWARE-PROBES.md` is worth reading before starting this phase — not for its MVD specifics,
which do not transfer, but for how much of that fight was *the decoder lying about success*. `FRAMEREADY`
did not mean pixels existed, and `MVD_STATUS_OK` was not zero. Assume `sceAvcdec` has its own version of
that and check the output buffer, not the return code.

---

## 5. Input

Native Vita has **one shoulder button per side and no clickable sticks** — no L2/R2, no L3/R3 `[C]`.
The `SceCtrlButtons` enum defines those bits, but they are for PlayStation TV with a real DualShock; on
a handheld they never assert.

**Testing the full input path needs a Bluetooth controller, and that needs a taiHEN kernel plugin**
(`ds4vita`-class), not anything vitasdk provides — a handheld Vita will not pair a DualShock on its own.
Whether such a plugin surfaces **analog** L2/R2 and L3/R3 through `sceCtrl`'s extended read functions, or
flattens them to the handheld's button set, is `[X]` — and it decides whether the input path can be
tested completely on this hardware at all.

Also `[X]`: the touch coordinate range (commonly cited as ~1919×1087, not header-verified) and
`sceMotion`'s sampling rate (no constant exposed).

---

## 6. Things that are decisions, not measurements

- **`connect/main.c` is 2,074 lines and mostly portable.** `service_control`, `wait_for_session_ready`,
  `check_sign_in_gate`, `await_control_message`, `seal_control_packet` and the stream callbacks are flow
  logic with no 3DS in them. Moving them into `ports/common` is roughly the difference between this port
  owing ~6,000 lines of platform code and ~2,500. Worth doing before Phase 5, not after.
- **Pairing stays on a PC.** Same as the 3DS, for the same reason: registration needs PSN OAuth and TLS
  and buys a handheld nothing. Import a pairing record.
- **The clean-room line is sharper here.** A third-party Remote Play client exists for this hardware and
  is off-limits — source, constants, wire-format notes, all of it. `CLAUDE.md`'s same-project exemption
  covers *Ripcord's* ports reading `src/`, not another project's. Platform facts (vitasdk headers,
  henkaku's wiki, homebrew for unrelated protocols) are fair game and always were.
