# ripcord-ps3

A PS5 Remote Play client for PlayStation 3 homebrew, in C.

**Status: it runs on a PS3, the platform seam is confirmed against hardware, and it decodes H.264
pixel-correctly on the console.** What exists and is tested is the
bitstream front end — reader, parameter-set and slice-header parsers, Annex-B splitter, picture-boundary
tracking — built on the host, because none of it needs a PS3 to be found wrong. What is now *also*
confirmed is the platform layer: on 2026-09-11 the bring-up program ran on a real console and every check
passed. The time base, the sleep's units and the CSPRNG are measurements rather than assumptions.

**The headline measurement: the PPE time base is 79,800,986 Hz against the 79,800,000 this port
expected — 12 parts per million.** That number scales every timeout in the core, and it is right.

What remains untouched by hardware is everything above the seam: sockets, threads, decode.
[`SETUP.md`](SETUP.md) records what the toolchain install took, what it corrected, and — at some length —
the five runs it took to get a program to start at all.

This branch sits on `feat/ports-common`, so `ports/common` — the portable protocol core — is in the
tree. That was not true when the port was scoped: the core lived on `feat/vita-port`, 94 commits behind
`main` and unable to build, since `docs/protocol/*.proto` were added to `main` afterwards and the .NET
solution could not restore there. `CONTRIBUTING.md` requires `commit → test → push`, and a branch whose
tests cannot run is the wrong base — so steps 1–3 were deliberately chosen to need nothing from the core,
and the core was rebased onto `main` on its own branch before step 4 asked for it.

No PS3 on hand yet, which is fine: none of the work that comes first needs one. A *toolchain*
unblocks more of this port than a console does, and that has now been demonstrated rather than argued —
installing one settled two of this file's open `[X]` questions and turned up two build errors, without
a console being involved. [`SETUP.md`](SETUP.md) is how to get one, and is written for the same Linux
box the 3DS and Vita ports are built on.

## Why this port is only the decoder

`ports/common/platform/rc_platform.h` is the entire list of things the portable core
asks of an operating system, and it is **four functions**: `rc_time_ms`, `rc_sleep_ms`,
`rc_tick`/`rc_tick_hz`, and `rc_random_bytes`. Sockets turned out to need no seam at all, because both
consoles expose the BSD names.

The Vita port is the measure of what a new target costs. It is on its own branch rather than in this
tree — `feat/ports-common` carries the core without it — but the shape of the bill is what matters:

```
rc_platform_vita.c    67 lines
rc_random_vita.c      70
rc_stack_vita.c       56
app/main.c           187
                     ---
                     380 lines
```

Protocol, transport, crypto, discovery, control session, input encoding — all of it is already portable
and already tested against known-answer vectors on a host compiler. **So the PS3 port is a hundred-odd
lines of platform layer plus one genuinely hard thing: the video decoder.**

That is also why the media path is deliberately absent from `rc_platform.h` — the header says so
explicitly. Decode, audio out and present are installed by each port as callbacks, because they are the
largest platform surface and the least shared.

## The platform layer

`source/platform/rc_platform_ps3.c`. It compiles against real PSL1GHT headers now, which is not the
same as working: every row below is still `[X]` on *behaviour*, because no line of it has executed on a
console.

| Seam | PS3 | Note |
|---|---|---|
| `rc_time_ms()` | Derived from the time base, **not** a wall clock | The seam forbids going backwards, and the PS3 has a user-settable date and an internet time sync. One monotonic source makes that property structural rather than hoped for |
| `rc_sleep_ms()` | `sysUsleep(ms * 1000)` | **Microseconds, confirmed on hardware.** A 1000 ms sleep measured 79,800,986 ticks against an independently-reported 79.8 MHz time base, which only works out if the units are what the seam assumes |
| `rc_tick()` / `rc_tick_hz()` | `mftb`, at whatever `sysGetTimebaseFrequency()` reports | **The 79.8 MHz magic number is gone** — lv2 answers it directly (syscall 147), and 79,800,000 survives only as the value the bring-up program cross-checks against. On the console the two agreed to 12 ppm, so the documented figure was right all along; the point is that the port no longer *depends* on it having been |
| `rc_random_bytes()` | `sysGetRandomNumber`, in `source/platform/rc_random_ps3.c` | Resolves against `sysPrxForUser` — the always-resident library, so no `sysModuleLoad` first. Capped at 4096 bytes a call, so the body is a chunking loop. **Both of its `[X]`s are now closed on hardware:** zero does mean success, and lv2 *does* tolerate an unaligned destination — `rc_random_init()`'s probe asks for bytes at a deliberately odd address and it passed |
| sockets | PSL1GHT BSD names after `netInitialize()` | **Confirmed on hardware.** UDP socket, broadcast, `bind()`, `sendto`, `recvfrom` — the PS3 found a real PS5 on the LAN. `netInitialize()` is required first, as predicted, and `sin_len` is the trap: the PS3's `sockaddr_in` carries the original BSD length byte that Linux and the 3DS dropped |

Three of the four functions read the PowerPC time base with one instruction, so the whole file's exposure
to PSL1GHT is one sleep call and one frequency query. That is a much smaller surface to be wrong about
than four independent SDK calls — and smaller again now that the frequency is asked for rather than
asserted, which removed the only value here that a wrong answer would have corrupted in silence.

What that leaves the bring-up program doing is *better*, not redundant. Its own comment used to concede
that measuring ticks against a sleep tests the conjunction of two unknowns and cannot say which failed.
With the frequency authoritative, a disagreement is evidence about `sysUsleep` specifically.

Two things the Vita port learned that apply here unchanged: **"it compiles" is not "it works"** for socket
idioms, and `bind()` to port 0 is worth testing on a device before assuming — the 3DS rejects it outright.

**Both were worth testing, and both came out well.** `bind()` to port 0 is **accepted** on the PS3, so
the 3DS's restriction is that platform's rather than a general one — `rc_platform.h` records the answer.

`sin_len` is the more instructive one, because the first conclusion drawn about it here was wrong. The
PS3's `sockaddr_in` does carry the original BSD length byte that Linux and the 3DS dropped, and this port
sets it because PSL1GHT's own sample does — but a comment in `rc_netlog.c` went on to call it *"not
optional"*, which was copied reasoning rather than a result.

That mattered beyond this port. `ports/common` builds `sockaddr_in` in three places —
`halyard_control_session.c` and `rc_tcp.c` — and never sets the field, so "required" would have meant the
shared core could not open a control session on a PS3. Rather than change code every port depends on from
a comment, the question went to the hardware: `rc_discover.c` broadcasts the same SRCH probe twice, once
with the field zeroed, and **the console accepted both and replied to both.** Not required. `ports/common`
needs no change, and the field stays set here only because matching the SDK costs nothing.

### Discovery works, from the console

```
disc:  broadcasting SRCH for 3000 ms
       bind() to port 0: accepted
       sent 1 probe(s), 1 datagram(s) back, 1 parsed
       192.168.1.42  PS5  MyConsoleName  id=...  sw=...  standby
```

The first thing in this port that has talked to a PS5 rather than to a file. The division of labour is
the one `rc_platform.h` describes, exercised end to end for the first time: `ports/common` builds the
SRCH datagram and parses the reply, and `source/discovery/rc_discover.c` contributes sockets, a
broadcast address and a deadline. Note the console answered from **standby** — discovery replies in rest
mode with a different status line, which is what makes LAN wake possible later.

### The CSPRNG — implemented, and why it is a probe

`source/platform/rc_random_ps3.c`, as of 2026-09-11. This was the last thing the README called "standing
between this port and a session", and what unblocked it was reading the SDK's own headers — no console
involved.

Three things were established from our own installed toolchain, with `ar` and `objdump` rather than from
documentation: the call is `sysGetRandomNumber(void *, u64)` in `<lv2/system.h>`, it is defined in
`liblv2.a` which this port already links, and — the part worth the disassembly — it is a **PRX stub**
resolving against `sysPrxForUser`, the library the loader binds without being asked. So there is no
`sysModuleLoad()` to perform first, which was the open question worth settling before writing anything.

Two things were **not** established, and both are why `rc_random_init()` draws a probe rather than
returning 1: whether zero means success (inferred from every other lv2-family call in this SDK, not
documented), and whether an unaligned destination is accepted (the header says `void *addr` and nothing
more). The probe asks for bytes at an odd address on purpose, and rejects a call that reports success
while writing nothing — which is what an unimplemented syscall looks like, and produces an all-zero key.

36 host checks in `tests/random_test.c` cover the half that does not need a console: the chunking loop,
the refusal to return a half-filled buffer, the gate before init, and the inversion in
`rc_random_rng_callback`. They build `rc_random_ps3.c` unmodified against a stand-in for the SDK header.
They prove nothing whatsoever about lv2, and the file says so at length.

### The main thread's stack — settled, and it cost two runs to settle

`SYS_PROCESS_PARAM(1001, 0x100000)` in `source/app/main.c`, and **the second argument is a raw byte
count**. This document previously said it was one of the `SYS_PROCESS_SPAWN_STACK_SIZE_32K` … `_1M`
enumerations. That was wrong, and it was wrong in a way that shipped: `SYS_PROCESS_SPAWN_STACK_SIZE_1M`
is `0x70`, so the binary asked lv2 for a **112-byte stack** — worse than the default it was written to
replace, and a crash before `main()` with no output of any kind.

Two pieces of evidence were available before that build was made and neither was used. Every sample in
PSL1GHT writes `SYS_PROCESS_PARAM(1001, 0x100000)` — a byte count, spelled in hex. And the field
immediately after this one in the same struct is `SYS_PROCESS_SPAWN_MALLOC_PAGE_SIZE_1M`, which is
`0x00100000`: the struct's own neighbour makes the units unambiguous. The `_STACK_SIZE_*` names belong to
the `flags` argument of `sysProcessExitSpawn`, a different call entirely. Checking against the SDK's own
samples is what caught it.

The Vita's `--gc-sections` trap does **not** repeat here, and that part was checked properly: built with
this port's exact flags, `.sys_proc_param` survives at 32 bytes with its magic and size intact, through
`strip` and `sprxlinker` as well. No linker pin is needed. Recorded because a negative result is what
stops the next person adding an incantation against a problem they do not have.

**The deeper lesson is not about stacks.** The mechanism was identified, the linker question was
investigated, the finding was written up in three documents — and nobody put the macro in the program.
"We identified the mechanism" and "the program uses it" are different claims, and the documentation
recorded the first as though it settled the second.

**Still open `[X]`:** the default the loader applies when the macro is absent. Never measured, because
the runs that would have measured it were broken for other reasons.

### What lv2 does with SPU thread arguments — measured, and it cost four runs

`sysSpuThreadInitialize` takes a `sysSpuThreadArgument` with four `u64` fields, and every SPU `main` in
PSL1GHT's samples is declared to receive four of them. **lv2 delivered `arg0`, `arg1` and `arg2` intact
and `arg3` as zero.** The SPE then DMA'd to effective address 0, the MFC faulted, and the thread died
before its next instruction.

The SDK corroborates it in hindsight: every sample declares the four-parameter signature and **not one
populates `arg2` or `arg3`**. Nothing in the SDK exercises the convention past the second slot, which is
exactly the sort of thing that looks load-bearing until you test it. `[X]` Whether `arg3` is reserved by
lv2 or simply not delivered is unknown, and does not matter — the fix does not want it.

So the port passes **one** argument, the effective address of a job block in main memory that the SPE
fetches by DMA before doing anything else. That is the standard shape for SPU work dispatch and what step
7 needs regardless: a decode job has far more than four parameters, so the argument registers were never
going to be the mechanism. Finding the limit on a 30-line memcpy rather than inside a half-built decoder
is the cheap version.

The diagnostics that found it are still in the program and still pointed at its replacement: a heartbeat
the SPE writes to its own local store (read back by the PPE with `sysSpuThreadReadLocalStorage`, which
does not need the SPE to participate), and a mirror of the job block as the SPE received it. Between
them they separate "never ran", "ran and its DMA went nowhere", and "was handed the wrong address" —
three failures that look identical from outside and have nothing in common as bugs.

### The shared core is byte-order clean — **measured, not argued**

`ports/common` is the code every port shares — Takion, the FEC, stream framing and demux, the control
session, discovery, input encoding — and until 2026-09-12 every assertion in it had only ever run on
little-endian x86. The PPE is big-endian.

**All eleven of its runners** now build for the PPE and run on the console, and the counts match the host
exactly — runner for runner, not just in total:

```
core:  running ports/common's suites on this hardware
       discovery pass  session pass  takion pass  stream_header pass
       stream_demux pass  input pass  fec pass
       control_crypto pass  stream_crypto pass  ecdh pass  control_proto pass
       3287 assertions passed, 0 failed        (host: 3287)
```

That is the whole core: Reed-Solomon and the Galois tables, the Takion handshake, data chunks, SACK and
reassembly, stream framing and A/V demux, discovery, the control session, input encoding — and the
crypto, which is where a byte-order bug would have been most expensive. A wrong key derivation does not
crash; it produces a session that negotiates and then silently fails to decrypt.

`ecdh` runs against **mbedtls cross-built for the PPE**, and that needed no new code at all:
`ports/common/tools/build-mbedtls.sh` already takes `CROSS=`, so `CROSS=powerpc64-ps3-elf-` produced a
102 KB `libmbedcrypto.a` from the same pinned, hash-verified 2.28.8 release the host suite and the Vita
port use. A script written for one console worked unchanged for a third target.

The four vector-backed runners read `.kat` files from `/dev_hdd0/ripcord-vectors/`, copied across by
hand — they are dirty-room material and are not embedded in the binary, for the same reason the H.264
capture is not. A missing file reports `skipped` rather than failing, which is the contract `ecdh_test`
already has on the host when its backend is absent.

Reading had said it should be clean: 28 sites assemble multi-byte values with explicit shifts and there
is not one multi-byte pointer cast in the transport, stream, session or util layers. That is an argument.
This is a result, and the distinction has earned its keep repeatedly in this port.

The other four runners read `.kat` vector files emitted by the .NET side, so running them means getting
those onto the console — a second step rather than a harder one, and the one that would extend this to
the control crypto, the stream crypto and ECDH.

## Can the hardware do it?

| Concern | PS3 | Verdict |
|---|---|---|
| H.264 decode | openh264 on the PPE: **177.8 fps at 640x360**, measured on hardware. No SIMD, no SPEs | Was "the whole problem". Extrapolates to ~44 fps at 720p, which is 1.3x off 60 and inside 30. See [`DECODE.md`](DECODE.md) §1 |
| Crypto | PPE trivially | Free — the New 3DS manages it at 268 MHz |
| Opus | Software, PPE | Free |
| RAM | 256 MB XDR + 256 MB GDDR3 | Ample — a 720p NV12 frame is ~1.4 MB |
| Colour convert + scale | RSX shaders | Free, and the right place for it |
| Network, wired | Gigabit Ethernet | Ideal |
| Network, Wi-Fi | **802.11 b/g only, every model** | ~20 Mbps real. Workable at the 10 Mbps default, poor headroom. Prefer wired |
| Input | DualShock 3 | Needs a profile — no touchpad, no adaptive triggers, accelerometer + single-axis gyro `[X]` |
| Display | HDMI to 1080p | The first target where the screen is not the limiting factor |

The comparison with the 3DS is the useful one: that port had a hardware decoder (MVD) and almost no CPU.
This one has abundant CPU and no decoder. More work, far less likely to hit a wall.

## What the console sends — measured

Parsed out of the two decrypted Annex-B dumps the 3DS port already wrote. Both agree; recorded in
`docs/protocol/ps5-av-stream.md` as **[W]**, and in `DECODE.md` §2.

**Main profile, CABAC, progressive, I- and P-slices only, no 8×8 transform, one slice group, 4:2:0.**

CABAC is the answer that decides the decoder base — see `DECODE.md` §1, which now recommends openh264.
Everything else narrows the target usefully: no B-slices means no reordering and no bipredictive motion
compensation, and Main rather than High removes the 8×8 transform and scaling matrices entirely.

Slices per picture measured 1–2 in the steady state (the 22-slice maximum is the rare IDR), which is less
parallelism than hoped for the entropy stage. Both dumps are 640×368, so **whether that scales at 720p is
unmeasured `[X]`** and is the most useful next measurement.

## Order of work

Nothing in 1–4 needs a PS3.

1. ~~Parse SPS/PPS from an existing capture.~~ **Done** — see above. Settled the decoder route.
2. ~~Bitstream reader and SPS/PPS/slice-header parser.~~ **Done** — `source/media/rc_h264_bits.[ch]`
   and `rc_h264_params.[ch]`.
3. ~~Annex-B splitter and slice-boundary extraction.~~ **Done** — `source/media/rc_h264_annexb.[ch]`:
   a zero-copy NAL iterator and an access-unit tracker that answers where each picture begins from the
   slice headers rather than from the framing layer's frame index. Steps 2 and 3 together are 142 checks
   in `tests/h264_test.c`, no console needed: `make -C ports/ripcord-ps3/tests`.
4. ~~`rc_platform_ps3.c` and a PSL1GHT skeleton that links and prints a timestamp.~~ **Done — it ran on
   a console on 2026-09-11 and every check passed.** The time base measured 79,800,986 Hz against an
   expected 79,800,000; `sysUsleep` is microseconds; `rc_time_ms` is monotonic; the CSPRNG is live and
   tolerates an unaligned destination. Getting there took five hardware runs and four of them produced no
   output whatsoever — [`SETUP.md`](SETUP.md) §6 is the post-mortem, and it is worth reading before
   packaging anything else for this console.
5. ~~Choose the decoder base on the evidence from 1.~~ **Done — openh264.** CABAC decided it;
   `DECODE.md` §1.
6. ~~SPU bring-up: one SPE running a trivial DMA job, measured.~~ **Done, on hardware 2026-09-11.**
   `spu/rc_spu_probe.c` and `source/spu/rc_spu.c`. Dispatch costs **~60 µs** (best of ten, spread 60–70)
   and DMA is comfortably not the constraint; `DECODE.md` §3 has both the numbers and why the first
   version of them was a single unrepeated sample and wrong. Took five
   console round trips, and the cause of four of them is worth knowing before writing any more SPU code —
   see "what lv2 does with SPU thread arguments" below.
7. **Decoder proper**, stage by stage, against the same vectors. **Started, and the base is proven.**
   openh264 builds for the PPE, links, and on 2026-09-12 decoded eight frames of this project's own
   capture on the console **pixel-identical to a reference decode** — `DECODE.md` §1. `tools/build-openh264.sh`
   fetches and hash-verifies it rather than vendoring it. What remains is throughput: moving the inner
   loops onto SPEs, one stage at a time, checked against the same hashes.

## Licensing

Ripcord is Apache-2.0 and its ports hold a permissive-only line: mbedtls (Apache-2.0), Opus (BSD-3).
**FFmpeg does not fit it** — `libavcodec` is LGPL-2.1-or-later, which the FSF treats as incompatible with
Apache-2.0, and the relink provision is awkward on a statically linked homebrew target. `DECODE.md` §1
sets out the options.

**Settled: openh264.** Its `LICENSE` was read directly rather than recalled — two clauses, no endorsement
clause, so **BSD-2-Clause**, which is Apache-2.0-compatible without qualification. CABAC (§2) is what ruled
out writing the entropy decoder ourselves.

Worth separating two things that get conflated: ffmpeg as a *development* tool is fine and this project
already uses it that way — `mvdreplay`'s own comment cites `ffmpeg -i video.264` for ground truth. The
constraint is only on what gets linked into a shipped client.
