# ripcord-ps3

A PS5 Remote Play client for PlayStation 3 homebrew, in C.

**Status: nothing runs on a console yet.** What exists is the bitstream front end — reader,
parameter-set and slice-header parsers, Annex-B splitter, picture-boundary tracking — built and tested
on the host, because none of it needs a PS3 to be found wrong.

This branch sits on `feat/ports-common`, so `ports/common` — the portable protocol core — is in the
tree. That was not true when the port was scoped: the core lived on `feat/vita-port`, 94 commits behind
`main` and unable to build, since `docs/protocol/*.proto` were added to `main` afterwards and the .NET
solution could not restore there. `CONTRIBUTING.md` requires `commit → test → push`, and a branch whose
tests cannot run is the wrong base — so steps 1–3 were deliberately chosen to need nothing from the core,
and the core was rebased onto `main` on its own branch before step 4 asked for it.

No PS3 on hand yet, which is fine: none of the work that comes first needs one.

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

## The platform layer, provisionally

Four functions, and the PSL1GHT answers are mostly obvious. All `[X]` until something compiles:

| Seam | PS3 | Note |
|---|---|---|
| `rc_time_ms()` | `sysGetCurrentTime()`, or the PPC timebase | Must not go backwards — the core's timeout loops depend on it |
| `rc_sleep_ms()` | `sysUsleep(ms * 1000)` | The header already warns about the units trap that cost the 3DS three files |
| `rc_tick()` / `rc_tick_hz()` | `mftb` timebase register; PS3 timebase is 79.8 MHz `[X]` | Profiling and association tags only, never randomness |
| `rc_random_bytes()` | `sys_get_random_number()` lv2 syscall `[X]`, else `/dev/urandom` | Key material. A failure must never fall back to a PRNG |
| sockets | PSL1GHT BSD names after `netInitialize()` | Same shape as `socInit()` / `sceNetInit()`. Belongs in a port bring-up file, not the seam |

Two things the Vita port learned that apply here unchanged: **"it compiles" is not "it works"** for socket
idioms, and `bind()` to port 0 is worth testing on a device before assuming — the 3DS rejects it outright.

`rc_stack_vita.c` suggests thread stack sizing needs an explicit answer per platform; PS3 PPU threads and
SPU thread groups both do too.

## Can the hardware do it?

| Concern | PS3 | Verdict |
|---|---|---|
| H.264 decode | Cell SPUs in software; no exposed fixed-function block `[X]` | **The whole problem.** See [`DECODE.md`](DECODE.md) |
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
4. `rc_platform_ps3.c` and a PSL1GHT skeleton that links and prints a timestamp. Cheap, and it flushes
   out the toolchain before anything depends on it. **This is the step that wants `ports/common`**, which
   is why the branch was rebased onto `feat/ports-common` before starting it. Next.
5. ~~Choose the decoder base on the evidence from 1.~~ **Done — openh264.** CABAC decided it;
   `DECODE.md` §1.
6. SPU bring-up: one SPE running a trivial DMA job, measured.
7. Decoder proper, stage by stage, against the same vectors.

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
