# The decode path

Everything else in a PS3 port is affordable — `ports/common` (on `feat/vita-port`) already carries the
protocol, transport and crypto, and `rc_platform.h` asks for four functions. This document is about the
part that is not.

> **This document's founding premise was wrong, and hardware said so on 2026-09-14.** It assumed the PS3
> exposes no H.264 decoder of its own, marked `[X]` because it came from general knowledge of the platform
> rather than from the console. It does: **cellVdec**, reached through PSL1GHT's `codec/vdec.h` and
> `libvdec.a`, decoding on the SPEs. It is now the port's decoder, with openh264 kept as the fallback.
> See "6. The console's own decoder" at the foot of this document for what it took to get there and what
> it cost. Everything between here and there is still worth reading — openh264 on the PPE is a working
> decoder, it is what proved the rest of the pipeline, and it is what the fallback path still uses — but
> read it knowing the premise did not survive.

`ripcord-3ds` feeds Annex-B NAL units to the New 3DS's MVD block and gets pictures back; the hard part of
video was a driver call. The PS3 was assumed to have no exposed fixed-function H.264 decoder, with its
Blu-ray playback taken to be **software decode on the Cell SPUs** — see the correction above.

So the decoder was taken to be ours, and two questions decided what that would cost: **what licence it can
be built from**, and **what the console actually sends**.

---

## 1. Licensing

Ripcord is Apache-2.0 and its ports have held a permissive-only line — mbedtls (Apache-2.0), Opus
(BSD-3-Clause). Three options, in the order they get suggested.

### FFmpeg — recommended against

`libavcodec`'s H.264 decoder is **LGPL-2.1-or-later** (not GPL; FFmpeg's GPL-only parts are elsewhere).
Two problems, and the second is the real one:

- **Apache-2.0 and LGPL-2.1 are treated as incompatible** by the FSF: Apache-2.0's patent-termination and
  indemnification clauses impose conditions LGPL-2.1 does not permit. Apache-2.0 *is* compatible with
  LGPL-3.0, but "or later" is the downstream recipient's election, not ours to exercise on their behalf
  in a way that binds the combined work.
- **The LGPL relink provision does not fit the target.** LGPL permits combining with differently-licensed
  code provided the user can substitute their own build of the library — in practice, dynamic linking.
  PS3 homebrew is a statically linked `.self`/`.elf` and PSL1GHT has no meaningful shared-library story,
  so satisfying it means shipping object files and a link script with every release. Possible; ugly; and
  it turns a clean posture into one that needs a paragraph of explanation.

Not legal advice. But the existing line is clean, and this would be the first dependency to complicate it.

### openh264 — the realistic base

Cisco's **openh264 is BSD-2-Clause**: Apache-2.0-compatible without qualification, and the same shape of
dependency the other ports already take.

- Its **decoder** covers Baseline, Main and High. (Its *encoder* is Constrained Baseline only —
  irrelevant; we only decode.)
- It is C++ with x86 and ARM SIMD back-ends. Neither helps on SPU, so the inner loops get rewritten for
  the SPU's 128-bit SIMD regardless. What transfers is the structure, the syntax parsing, and algorithms
  conformance-tested against real streams — which is most of the value.
- **Patents are a separate question from licence.** Cisco's royalty coverage attaches to *their* published
  binary module, not to source-derived builds. Against that: the core AVC patents were filed around
  2002–2004 and the pool has largely run out on a twenty-year term `[X — not verified, not legal
  advice]`. For a homebrew client this is the usual grey area rather than a novel risk.

### From scratch — possible, and §2 decides the cost

Fully Apache-2.0 and maximum control. Weeks or months depending on the profile:

- **Constrained Baseline** (CAVLC, I- and P-slices, no CABAC, no 8×8 transform) is genuinely tractable.
- **High profile** (CABAC, 8×8 transform, possibly B-slices) is a different order of work. CABAC alone is
  substantial, and — see §3 — it is the part that resists parallelism.

### A note on the clean-room rule, so it is not over-applied

`CLAUDE.md` forbids reading another implementation of **these protocols** to obtain implementation detail.
That rule is about Remote Play. H.264 is ITU-T H.264 / ISO-IEC 14496-10, a published international
standard, and reading the specification or a BSD reference decoder is an ordinary engineering activity in
a different category entirely. Nothing in the independence claim is touched by it. Stated plainly here so
the next reader does not stretch the rule to cover a codec.

**Recommendation, now that §2 is answered: openh264.**

CABAC settles it. Writing an arithmetic decoder and its context modelling from scratch is the one part of
H.264 that is both large and unforgiving — it has to be bit-exact or the stream desynchronises silently,
and there is no partial credit. Everything *else* §2 turned up points the other way (Main not High,
progressive, I+P only, one slice group), so a from-scratch decoder is far from absurd — but it would be
spending the effort precisely where the risk is concentrated.

The shape of the work is therefore **not** "port openh264 to PS3". It is: take its Main/progressive/I-P
CABAC path, discard what §2 rules out — B-slices, High-profile transforms, field coding, FMO — and rewrite
the inner loops for the SPU's 128-bit SIMD. Its x86/ARM SIMD does not transfer; its structure and its
conformance-tested correctness do.

### Does it build for the PPU? — **tested, 2026-09-12**

The recommendation above was made on the evidence of §2 and on reading openh264's licence. Whether the
thing can be *compiled for this target at all* was never checked, and it is the assumption the whole
decoder plan rests on. Checked now, against ps3dev's GCC 7.2.0 for `powerpc64-ps3-elf`:

| | |
|---|---|
| `codec/decoder/core` | **20 of 20 files compile.** No warnings, no modifications, big-endian PowerPC64 objects |
| `codec/common` | 13 of 16 |
| Licence | Two clauses, no endorsement clause — **BSD-2-Clause**, as claimed |

The three failures are shallow and two of them are things this port does not want:

- `WelsThread.cpp`, `WelsThreadLib.cpp` — openh264's threading. PSL1GHT's pthreads are partial
  (`pte_handle_t` has no int constructor) and `sys/sysctl.h` does not exist, which is what the core count
  is read from. **The decoder is not going to use openh264's thread pool anyway** — the design in §3 runs
  the PPE single-threaded and puts the work on SPEs, which is a different parallelism model entirely.
- `crt_util_safe_x.cpp` — `vsnprintf` not declared. A missing include, nothing more.

So §1's recommendation survives contact with the toolchain. That was worth establishing before writing
any decoder code against it.

### Endianness — **settled, and it is fine**

The PPE is big-endian and openh264 is developed on little-endian x86 and ARM, with no endianness handling
anywhere in its decoder core — no `bswap`, no `BYTE_ORDER`, nothing. That is either fine or fatal and the
two look identical until something runs, so it was the first thing step 7 had to settle: every later bug
would otherwise have been debugged through it.

**Settled by a differential decode, 2026-09-12.** The console's own `video.264` — 2,475 NAL units,
2,451 P-slices, 22 IDRs — decoded twice with the same source, no modifications, pure-C path both times:

| | |
|---|---|
| little-endian | native x86-64, 1,214 frames, 419,558,400 bytes |
| big-endian | `powerpc64-linux-gnu`, statically linked, run under `qemu-ppc64`, 1,214 frames, same size |
| result | **identical SHA-256**, `<redacted>`, byte for byte across 400 MB |

So openh264 decodes bit-exactly on a big-endian PowerPC64, on this project's own stream. That is a real
measurement over the whole capture, not a spot check.

Why it works is worth knowing, because it says the property is structural rather than lucky. The bitstream
cache is filled a byte at a time and composed explicitly MSB-first —

```c
uiCache32Bit |= (((pBuf[2] << 8) | pBuf[3]) << (32 - uiRemainBits));
```

— so no pointer is ever cast to a wider type to read the stream, and there is no byte order to get wrong.
The pointer casts that *do* exist, 81 in `mv_pred.cpp` and 39 in `deblocking.cpp`, are bulk uniform writes
(`val * 0x01010101UL`) and zeroing, where every byte is equal; those cannot observe endianness either.
Reading had suggested this and could not establish it at that volume, which is why it was tested.

**What this does and does not prove.** `qemu-ppc64` emulates a big-endian PowerPC64 running Linux, which
is the right proxy for byte order and the wrong one for everything else — it is not the PPE, not GCC 7.2,
not newlib, and not 256 MB of XDR. Endianness is the question it was asked and endianness is what it
answered. Toolchain and memory behaviour on the real console remain to be established by running there.

### It links, too — `tools/build-openh264.sh`

Compiling is not linking, and the decoder turned out to want more than the files that compile. The
build is now a script, in the shape `ports/common/tools/build-mbedtls.sh` established: **fetched from a
pinned, hash-verified release (2.6.0) at build time into a gitignored directory, never vendored.** A
checked-in 23,000-line third-party codec would be a maintenance and provenance liability nobody asked for.

It produces `libopenh264dec.a` for the PPE, and a program that creates, initializes and destroys an
`ISVCDecoder` through the real API links to a 2.6 MB big-endian PowerPC64 executable with **no undefined
symbols**.

Three things had to be dealt with, and **none of them is a patch to openh264**:

- **`-std=gnu++11`, not `-std=c++11`** — the same trap the port's C code hit with `-std=c99`. Strict ISO
  mode defines `__STRICT_ANSI__`, under which newlib hides `vsnprintf`; openh264 calls it on every
  platform while including `<stdio.h>` only on the Windows paths. The error names `vsnprintf` and
  suggests `vsprintf`, which reads exactly like a missing include and is not one.
- **`<sys/sysctl.h>` supplied by `tools/shim`** — openh264 asks the OS for a logical processor count, and
  a bare newlib target matches none of its guarded platforms. The shim declares the call and the build
  supplies one that always fails, so openh264's own error path sets `ProcessorCount = 1`. That is the
  right answer arrived at by its own logic: this port's parallelism is SPEs, which openh264 knows nothing
  about.
- **PSL1GHT's `libpthread`** carries the mutexes, condition variables and semaphores underneath.

One file is excluded: `WelsThread.cpp`, whose `CWelsThread` constructs a `pte_handle_t` from an int that
PSL1GHT's pthreads-embedded does not offer. Nothing the decoder needs refers to it.

Keeping openh264 unmodified is deliberate. A patch against a dependency fetched and verified at build
time has to be carried, rebased and justified forever; a shim on the include path is local, visible, and
costs nothing when the dependency moves.

### It decodes on the console — **2026-09-12**

The capture was copied to `/dev_hdd0/`, the bring-up program split it with **this port's own**
`rc_h264_annexb` — the one with 142 host checks behind it, because that is the seam the real client will
use — fed each NAL to `ISVCDecoder`, and hashed the first eight output frames with FNV-1a over the
logical pixels, ignoring stride padding so the numbers are comparable with a plain reference file.

```
dec:   2096837 bytes, 45 NALs fed, 8 frames out, 640x360
       frame   0  0x<redacted>
       frame   1  0x<redacted>
       ...
```

**All eight match the little-endian reference decode exactly.** The PS3 produced pixel-identical output
to a known-good decode of its own stream — which confirms the PPE, GCC 7.2's code generation, newlib and
the console's memory all at once, in a way qemu could not. The emulated result said the code is
endian-clean; this says the real machine runs it.

45 NAL units to produce 8 frames is the expected shape: the stream opens with SPS, PPS and a 22-slice
IDR, so the first picture alone costs two dozen of them.

### How fast is the PPE alone? — **far faster than this document assumed**

300 frames decoded on the console, timing only `DecodeFrameNoDelay` — file reading, NAL splitting and
hashing all outside the measured region, and the hashing of the first eight frames (19 ms) reported
separately and excluded:

```
PPE alone: 300 frames in 1687 ms, 5624 us/frame, 177.8 fps   (640x360)
```

**177.8 fps, with no SIMD and no SPE involvement at all.** openh264's pure-C path, on one in-order
PowerPC core.

Scaling to the target resolution — 720p is exactly four times the pixels, 921,600 against 230,400:

| | |
|---|---|
| extrapolated 720p | **22.5 ms/frame, ~44 fps** |
| against a 60 fps frame period (16.7 ms) | 1.3x over |
| against §4's ~8 ms decode target | 2.8x over |
| against a 30 fps frame period (33.3 ms) | **68% of budget** |

This document has been assuming the PPE could not do it — "the PPE alone will not decode 720p H.264 and
there is no fixed-function block to fall back on". On this evidence that was too pessimistic. The PPE
alone is within **1.3x** of 720p60 and comfortably inside 720p30, before a single SPE is used and before
a single inner loop is vectorised.

**Four reasons not to over-read it**, because a 4x pixel scaling is the crudest possible extrapolation:

1. **CABAC tracks bitrate, not pixels.** §3 already makes this point. The capture is 2.8–7.8 Mbps; a
   720p stream would carry more, so the entropy share grows by something other than 4x and the estimate
   above under-counts it.
2. **Decode is not the whole frame.** The real client also runs network, Takion, crypto, reassembly and
   Annex-B splitting on the same PPE, plus colour conversion and present. 22.5 ms of a 16.7 ms period
   leaves nothing for any of it.
3. **This is decode from memory with no deadline.** No jitter buffer, no arrival pacing, nothing
   competing.
4. **It is one stream at one resolution.** 640x360 is what the console happened to send the 3DS port.

**And one reason it may be better still: the PPE has AltiVec/VMX and openh264 is not using it.** The
build takes the pure-C path because `build/arch.mk` has no PowerPC branch — it dispatches on x86, arm,
arm64, mips and loongarch, and an unknown architecture gets no SIMD at all. So this figure is the
*scalar* floor, and the 128-bit vector unit sitting on the same core is entirely unexploited.

**That reorders the work.** §3's plan moves the inner loops to SPEs; this measurement says the cheaper
experiment comes first — vectorise the PPE's hot loops with VMX, which shares an instruction set family
with the SPU work that would follow and would not be wasted if the SPEs are needed anyway. Whether the
SPEs are needed for 720p60 at all is now an open question rather than a settled premise.

### The little-endian reference output is now ground truth

The same run produced a reference decode of the whole capture: 1,214 frames of 640x360 NV12-equivalent
planar YUV, from the console's own stream. Every later decoder — a stage at a time, on the PPE, then on
SPEs — can be compared against it frame by frame with `sha256sum`, which is the cheapest possible
correctness harness and needs no console. It lives in the dirty room with the capture that produced it,
for the same reason the capture does.

Still **not FFmpeg**, for the licence reasons above. Worth noting the project already uses ffmpeg as a
*development* tool — `mvdreplay`'s comment cites `ffmpeg -i video.264` for ground truth — and that is
entirely fine. The constraint is on what gets linked into a shipped client, not on what decodes a dump on
a workstation.

---

## 2. What the console actually sends — **answered**

Measured by parsing the parameter sets out of the two decrypted Annex-B dumps the 3DS port's
`dumpvideo=1` wrote (1,214 and 941 pictures). Both agree exactly; one SPS and one PPS serve each whole
session. Recorded in `docs/protocol/ps5-av-stream.md` as **[W]**.

| Field | Value | What it costs us |
|---|---|---|
| `profile_idc` | **77 — Main** | No 8×8 transform, no scaling matrices. Those are High-only |
| `entropy_coding_mode_flag` | **1 — CABAC** | The expensive half. This is the answer that decides §1 |
| `frame_mbs_only_flag` | **1 — progressive** | No field coding, no MBAFF |
| Slice types | **I and P only** | No B-slices: no reordering, no bipredictive MC, no DPB reorder |
| `num_slice_groups_minus1` | 0 | One slice group; no FMO/ASO |
| `chroma_format_idc` | 1 — 4:2:0 | |
| `level_idc` | 31, at 640×368 | 720p60 exceeds Level 3.1's MB rate, so the console must raise it **[X]** |
| Slices per picture | min 1, **mean 2.0–2.3**, max 22 | The max is the IDR. Ordinary P-pictures carry one or two |

**So the target is narrow and well-defined: Main profile, progressive, I- and P-slices only, CABAC, no
8×8 transform, one slice group, 4:2:0.** That is a great deal less than "H.264".

Two caveats worth carrying. Both dumps are 640×368, so **slices per picture at 720p is not measured
`[X]`** — one slice per MTU implies it scales with macroblock count, but that is inference, and §3 depends
on it. And the level will differ at higher resolutions, which affects DPB sizing.

## 3. Where the parallelism is, and where it is not

> **Read §1's throughput measurement first.** This section was written before anything ran on a console
> and assumes the PPE cannot decode 720p, so the whole design hangs off moving work to SPEs. The measured
> figure — 177.8 fps at 640x360, extrapolating to ~44 fps at 720p, scalar, no SPEs, no SIMD — does not
> support that premise as stated. The allocation below is still the right shape if SPEs turn out to be
> needed; it is no longer established that they are. What follows is kept as written, with that caveat
> attached, rather than rewritten to match one measurement.


Cell for homebrew: one PPE (in-order PowerPC, 2-way SMT, 3.2 GHz) and **six usable SPEs**, each with
**256 KB of local store**, 128-bit SIMD, no cache, explicit DMA.

The 256 KB is the binding constraint and shapes everything. A 720p luma plane alone is ~921 KB, so an SPE
never holds a frame — it works in macroblock-row stripes with double-buffered DMA in and out. Standard
Cell pattern; it just has to be designed in rather than retrofitted.

**Entropy decode is the serial stage.** CABAC is context-adaptive and bit-serial: it cannot be SIMD'd and
cannot be split *within* a slice. It is also the part the §1 measurement is least able to extrapolate,
since it tracks bitrate rather than pixel count. CAVLC is table-driven and cheaper but still serial per slice. Everything
downstream — prediction, inverse transform, motion compensation, deblocking — is parallel over macroblocks
with known dependencies.

So the split falls out:

```
PPE     network, Takion, crypto, reassembly, Annex-B split, job dispatch
SPE 0   entropy decode  -> per-MB syntax elements into a work queue
SPE 1-4 macroblock reconstruction: intra/inter prediction, inverse transform, MC
SPE 5   deblocking (wavefront over MB edges) + DMA out to RSX-visible memory
RSX     YUV -> RGB, scale, present
```

**§2 measured the lever, and the answer is less generous than hoped.** Ordinary P-pictures carry one or
two slices, not six — the 21-slice maximum is the IDR, and IDRs are rare. So slice-parallel entropy decode
relieves the steady state by about 2×, not 6×, at least at 640×368. Whether 720p multiplies it is
unmeasured **[X]** and is the single most useful next measurement for this design.

**But the load probably is not where the original split assumed.** CABAC throughput tracks *bitrate*, and
the observed stream is 2.8–7.8 Mbps — low. Reconstruction and deblocking track *pixels*, and those scale
with the resolution we actually want. So the likely bottleneck at 720p60 is the per-pixel half, not the
entropy half, and the allocation should lean that way until measurement says otherwise:

```
PPE      network, Takion, crypto, reassembly, Annex-B split, job dispatch
SPE 0-1  CABAC, one slice each where a picture has two
SPE 2-4  macroblock reconstruction: intra/inter prediction, inverse transform, MC
SPE 5    deblocking (wavefront over MB edges) + DMA out to RSX-visible memory
RSX      YUV -> RGB, scale, present
```

Frame-level pipelining — entropy for picture N+1 while reconstructing N — would hide the serial stage
entirely, and is available precisely because there are no B-slices. It costs one frame of latency
(16.7 ms), which is most of the decode budget in §4, so it is a fallback rather than a starting point.

### What a job costs — **measured on hardware, 2026-09-11**

Step 6 of README.md's order of work ran on a console. One SPE, one thread group, one job, synchronous:

```
best of 10 runs each; spread is the worst case beside it
empty job      60 us  (worst 70 us)   <- thread group start, SPE startup, completion signal
256 KB copied  80 us  (worst 127 us)
DMA alone      20 us, ~24 GB/s both ways, single-buffered  (a difference of two measurements)
```

**Dispatch costs ~60 µs, and that is the number this section needed.** It is 0.4% of a 16.7 ms frame
period, so a few dozen jobs per frame is free and the granularity question has an answer: a job must be
worth at least a few hundred microseconds of work for the overhead to disappear into it. A macroblock-row
stripe comfortably is; a single macroblock is not, by two orders of magnitude. The stripe-based design
above stands, and "dispatch per macroblock" is ruled out on evidence rather than on instinct.

The 60–70 µs spread matters as much as the figure: dispatch cost is *stable*, so a decoder can budget
against it. A wide spread would have meant it could not.

**METHOD, because the first version of this paragraph was wrong.** It cited 65 µs and 9,967 MB/s from a
single sample of each. A second run of the same binary gave 89 µs and 39,741 MB/s — and 39.7 GB/s is not
a credible figure for one SPE, which is what exposed the method rather than the number. The probe now
repeats each measurement ten times and keeps the minimum, the fastest observation being the one least
contaminated by the XMB, the network stack and the filesystem sharing the machine. The dispatch figure
moved by a third under that treatment. Two samples of a noisy quantity are not a measurement, and a
document that other decisions rest on should not have been given one.

**DMA is not the constraint, and the figure should be read as an order rather than a throughput.** It is
the difference between two measured times, both noisy, and 20 µs for 512 KB in both directions lands
suspiciously close to the ~25 GB/s an SPE can theoretically pull from the EIB — which is what a
difference-of-two-numbers tends to do. What survives is the conclusion: a 720p NV12 frame is ~1.4 MB, so
a whole frame in and back out is a few hundred microseconds against an 8 ms budget, with a probe that is
deliberately single-buffered and therefore a floor. Bandwidth was never going to be the problem; local
store size and the serial entropy stage still are.

**[X] All of this is one SPE.** Whether six run at anything like six times the aggregate is unmeasured,
and the EIB and memory controller are shared. It is the obvious next measurement, and cheap now that the
job model works.

---

## 4. Budget

**Latency.** At 60 fps the frame period is 16.7 ms. Decode should land under ~8 ms to leave room for the
jitter buffer the client already maintains, colour conversion and presentation. Cell DMA latency is not
the concern; the serial entropy stage is.

**Throughput `[X — estimate, not measured]`.** The yardstick is that the PS3 decoded Blu-ray 1080p24
routinely, on fewer SPEs than homebrew can use:

| Target | Pixel rate | vs Blu-ray 1080p24 (~50 Mpixel/s) | Read |
|---|---|---|---|
| 540p60 | ~31 Mpixel/s | 0.6× | Comfortable |
| 720p60 | ~55 Mpixel/s | 1.1× | **The sensible target** |
| 1080p60 | ~124 Mpixel/s | 2.5× | Likely out of reach at low latency |

`AdaptiveBandwidthController` already steps 1080p → 720p → 540p and then halves frame rate, so the port
does not need to pick — it needs to negotiate honestly and let the ladder settle where the hardware lands.

Low-latency encoding helps: short GOPs and few or no B-frames remove reordering delay and make the
slice-parallel path more effective than it would be on general content.

---

## 5. Order of work

Nothing in 1–4 needs a PS3.

1. ~~**Parse SPS/PPS from an existing capture.**~~ **Done** — §2, and it decided §1. Took one throwaway
   script against `3ds/video.264`; nothing from the dirty room was committed.
2. ~~**Bitstream reader + SPS/PPS/slice-header parser.**~~ **Done.** `rc_h264_bits` reads RBSP with
   emulation-prevention handled in place rather than by copying — the SPE has 256 KB of local store and
   no cache, so a decode that copies each slice to strip three bytes has doubled its DMA for nothing.
   `rc_h264_params` parses both parameter sets and the slice header as far as `redundant_pic_cnt`, which
   is exactly what sec 7.4.1.2.4 needs to find picture boundaries and no further.
3. ~~**Annex-B splitter and slice-boundary extraction.**~~ **Done.** `rc_h264_annexb` yields NAL units as
   pointers into the caller's buffer — nothing copied, because the decoder DMAs slice bytes from exactly
   those pointers into 256 KB of local store — and an access-unit tracker absorbs the parameter sets and
   reports where each picture begins. 142 host checks across steps 2 and 3.
4. ~~**`rc_platform_ps3.c` and a PSL1GHT skeleton.**~~ **Done, on hardware.** The seam measures the time
   base rather than trusting the constant — the only part of it a wrong value would corrupt silently —
   and the console answered 79,800,986 Hz against 79,800,000 expected.
5. ~~**Choose the decoder base** on the evidence from 1.~~ **Done — openh264**, §1.
6. ~~**SPU bring-up**: one SPE running a trivial DMA job, measured.~~ **Done, on hardware** — see §3.
   Dispatch costs ~65 µs and DMA runs at ~10 GB/s, which settles the granularity question this document
   had been deferring.
7. **Decoder proper**, stage by stage, against the same vectors. **The only step left**, and it now has
   a reference to be checked against: openh264 builds for the PPU (20 of 20 decoder-core files) and
   decodes this project's own capture bit-exactly on big-endian PowerPC64, so the base is sound and the
   1,214-frame little-endian decode is ground truth for everything built on top of it.

Steps 2 and 3 were worth doing regardless of how 5 resolved, which was the argument for starting there
rather than with the SPU — and it held up: the front end was finished and tested before any console was
involved. Everything in 1–6 is now confirmed, so the remaining work is the decoder itself and the two
measurements §3 marks `[X]`: whether slice parallelism scales at 720p, and whether six SPEs aggregate.

---

## 6. The console's own decoder — **cellVdec, 2026-09-14**

The premise at the top of this document was never checked against the console. It is wrong: PSL1GHT
exposes `codec/vdec.h` and `libvdec.a`, and `SYSMODULE_VDEC_H264` loads. The decoder runs on the SPEs.

**It is now the live decoder, with openh264 the fallback**, behind the seam already in
`rc_decode_probe.h` — the same seam-and-stub shape `CLAUDE.md` describes for the crypto. The choice is
made at open and logged, never inferred from the frame rate.

### What it is worth

Measured on the Forza Horizon 5 start screen at 1280x720, thirty seconds each:

| | openh264 (PPE) | cellVdec (SPE) |
|---|---|---|
| pictures decoded | 124 of 843 | **887 of 889** |
| decode errors | 711 | **0** |
| on screen | 4 fps | **29 fps** |
| worst decode run | 121 ms | **6 ms** |
| frame queue depth | 8 of 8, 27 dropped | **1 of 8, 0 dropped** |
| units lost | 157 | **6** |
| decode + blit | 25,839 us | **5,155 us** of a 33,333 us budget |

The last row is the one that matters beyond this stream: the whole path now costs 15% of a 30 fps frame
budget, where openh264 could not fit inside it at all.

### Four values no SDK header states

Each cost at least one hardware run, and each is derived rather than recalled — the sweep or the
measurement IS the derivation, which is the discipline `CLAUDE.md` asks for on protocol values and which
applies just as well to an undocumented API.

1. **`vdecType.profile_level` is H.264's `level_idc`.** Swept 0..255; the console accepts exactly
   `10 11 12 13 20 21 22 30 31 32 40 41 42`, which is that set exactly.
2. **The callback needs a 32-bit `{entry, toc}` descriptor**, not GCC's 64-bit ELFv1 OPD. `libvdec.a`'s
   own PRX stubs show the shape (`lwz r0,0(r12)` / `lwz r2,4(r12)`); PSL1GHT supplies `__build_opd32`.
   Given the 64-bit one the decoder branches to a null entry and never calls back at all.
3. **Every buffer the decoder writes into must be 128-byte aligned.** Otherwise the last bytes of each
   row are corrupted — visible as an 8-pixel strip down the right edge, and invisible to every counter
   in the pipeline.
4. **`vdecEndSequence` completes on a callback, not on return.** Calling `vdecClose` before `SEQDONE`
   arrives hangs the console.

Output planes are packed at the **display** size, `Y` then `U` then `V`, stride equal to width. The
reported `picture_size` is larger (`640x368x1.5` for a 640x360 picture) and is a buffer requirement
rather than a description of the layout.

### How it was validated, and the one that mattered

The decoder was checked against openh264 on the same capture, plane by plane, before it went anywhere
near the screen: `Y`, `U` and `V` bit-identical. ffmpeg on the development machine then produced the same
luma hash again, so the reference was three decoders wide rather than one.

**That validation still did not prevent the last bug, and the reason is worth keeping.** The capture is
level 3.1; the live stream is level 4.0. The decoder was opened at level 31 on the reasoning that 3.1 is
"the level for 720p" — but H.264 levels constrain bitrate and frame rate as well as picture size, so a
resolution cannot pick one. Opened below what the stream needs, the decoder accepted every access unit,
returned success from every call, reported zero errors, and produced 889 uniformly **black** pictures.

Five builds of careful measurement went into the buffer and the threading and never touched the cause,
because the validation had been done against a stream that happened to fit the wrong assumption. It now
opens at 42 — the highest the console accepts, and a decoder opened high decodes anything below it — and
logs the level it chose beside the level the stream declares.

### Audio — **working, 2026-09-14**

48 kHz stereo Opus in 10 ms frames, decoded by libopus on the PPE and played through PSL1GHT's audio
port. `tools/build-opus.sh` builds it on the same terms as openh264: pinned, hash-verified, fetched at
build time, never vendored. BSD-3-Clause, so section 1's permissive-only line is unchanged.

First run on hardware: **2,999 frames decoded, 0 errors, 5,615 blocks to the hardware, 1 silence block**
— the first one, before the ring had filled — and no ring overflows. 5,615 × 256 samples over thirty
seconds is 47.9 kHz, which is the 48 kHz it claims.

Two mismatches, one ring: Opus emits 480 samples per channel and the port takes fixed blocks of 256, and
frames arrive when packets do while the port wants one every 5.33 ms forever.

**There is no audio thread.** Opus is about 1% of a frame's work, the port's block ring holds 42 ms, and
the receive loop comes round far faster — so it is decoded in the demuxer callback already running there
and the blocks are topped up from the same loop. Three console lockups in this port have come from
threads added casually, and `ripcord-3ds` reached the same conclusion independently.

**One value the SDK gets wrong in its own header.** PSL1GHT declares `audioPortConfig.readIndex` as "index
of currently read block"; the syscall it wraps returns an **address** holding that index, which is what
the console reports. The code discriminates at runtime — a block index is below `numBlocks`, an address
is not — rather than picking one. Taking the header at its word would have been silent corruption.

### 1080p — **tested, and the answer is no**

Not for want of speed. The whole decode-and-blit path costs 15% of a frame budget at 720p, and 1080p30 is
244,800 macroblocks a second against level 4.2's ceiling of 522,240 — throughput was never the question.

Three things had to be fixed before the real answer appeared, and each was worth fixing on its own:

1. **The console would not grant 1080p at all** until the launch spec's `bwKbpsSent` was raised. It had
   been 8,000 kbps throughout, not by choice but because that is the pairing record's default. It is a
   claim the console sizes the stream against, exactly as the declared rtt and mtu are. At 30,000 the
   request was granted.
2. **The demuxer refused every 1080p frame**, silently. `FEC_MAX_TOTAL_UNITS` bounds what the
   Reed-Solomon module can recover; it was also being used as the number of unit slots a frame may
   occupy. Those coincided at 640x360 and 1280x720 (about 21 slots) and diverge badly at 1080p, which
   needs well over a hundred. See `STREAM_DEMUX_MAX_UNITS_PER_FRAME`.
3. **The stream declares level 5.0 and cellVdec offers at most 4.2.** b145's sweep is exact:
   `vdecQueryAttr` accepts `10 11 12 13 20 21 22 30 31 32 40 41 42` and nothing above.

The third is the wall, and the reason is the **decoded picture buffer**, not the pixel rate. The console
encodes with **nine reference frames**. At 720p that is 32,400 macroblocks of DPB, inside level 4.0's
32,768 — which is exactly why the 720p stream declares 4.0, and why it decodes. At 1080p the same nine
frames need 73,440 against 4.2's 34,816.

So the console is not padding its declared level; it is asking for what it uses, and this hardware cannot
be configured to provide it. Lowering the declared level in the SPS was tried and produced black pictures,
as it must — the number was never the constraint, the buffer behind it was. That clamp is kept but is now
guarded by the quantity that actually decides: it fires only when the stream's own `max_num_ref_frames`
fits the target level's DPB at the stream's own resolution, so at 1080p against this console it correctly
does nothing.

**1280x720 at 29 fps is this port's ceiling**, and it is a hardware limit rather than a missing feature.
It would move only if the console could be asked for fewer reference frames, and no field we have
identified does that.

### The bitrate ceiling is a slice count — **measured, 2026-09-14**

The console slices each picture so that every network unit decodes independently. Slice count therefore
follows units per frame, which follows the bandwidth declared in the launch spec (`bwKbpsSent`) — and
cellVdec has a limit on slices per picture that it does not advertise and does not complain about.

| declared `bwKbpsSent` | slices per 720p picture | delivered | result |
|---|---|---|---|
| 8,000 | ~21 | 5.5 Mbps | 873 of 875 pictures, 29 fps |
| **15,000** | **65** | **9 Mbps** | **845 of 891 pictures, 28 fps** |
| 30,000 | 136 | 15 Mbps | every picture black, **no error reported** |

128 is the obvious candidate for the real limit and 136 is just past it, but only 65 and 136 have been
measured, so the port warns between them rather than at a number nobody has tested.

**15,000 is the better setting**: same smoothness as 8,000 and nearly twice the delivered bitrate.

What made this expensive to find is that every layer reported success. The bytes were correct — every
access unit began with a start code, and a 96-unit frame assembles byte-exact on the host (there is now a
test for that, which nothing else covered). The parameters were correct — the same profile 77, level 40
and nine reference frames as the stream that decodes perfectly. The decoder accepted all of it, returned
success from every call, produced a picture per frame, and every picture was black. A hardware decoder
past an unadvertised limit does not fail; it just stops working.

Two faults found on the way there were real and are fixed regardless: `bwKbpsSent` had been 8,000 because
that was the pairing default rather than anyone's decision, and the demuxer refused any frame needing
more than 64 unit slots — silently, which at 15 Mbps is most of them.

### CONNECTION_QUALITY — implemented, and **not shown to do anything**

The encoding is in `ports/common` (a protocol fact, matching `HalyardTakionStream` field for field) and
the policy that decides when to send is in this port (slice count is a cellVdec property, not a Halyard
one). It is **off by default** — `connquality=1` in the pairing record.

It is off by default because its effect is unproven, and the honest record of why is worth keeping:

- **b176** stepped the ask from 30,000 kbps to the 4,000 floor over 8 reports, and the video dropped from
  ~58 MB in thirty seconds to 8.9 MB. That looked like proof the console had listened, and it was written
  up as proof.
- **b177** sent the same 8 reports to the same floor and the console sent the full 57.6 MB.

Two runs, the same asks, opposite outcomes. The likeliest explanation for b176 is the content: the test
screen is animated and a quiet stretch produces a low byte count on its own, which this project has been
caught by before. So `targetBitrate`'s units remain `[X]` — unconfirmed, exactly as the .NET side has
them — and nothing here has demonstrated the console acting on the message at all.

**What does work is the launch spec's `bwKbpsSent`**, which demonstrably controls how finely the console
slices: 8,000 gives ~21 slices a picture, 15,000 gives 65, 30,000 gives 136 and a black screen. That is
the lever, it is set once before the stream starts, and 15,000 is the measured-good setting.

One thing was tried and removed: requesting a keyframe on each throttle step. The reasoning was right —
a stream that has changed shape needs a fresh reference — but setting `g_awaiting_keyframe` hands it to
the repeat loop that asks every 200 ms until one arrives, turning 8 steps into 79 IDR requests.

### 720p60 — **working, 58 fps, 2026-09-15**

Level 4.2 bounds the decoded picture buffer, not the frame rate: nine reference frames at 720p need
32,400 macroblocks either way, and 720p60 is 216,000 macroblocks a second against a 522,240 ceiling. The
console grants it and declares level 40, so the cap this port applies is on SIZE alone.

Getting 60 fps onto the screen took five separate limits, every one of them a constant or a policy that
was correct when it was written and had stopped being correct:

| build | limit | what it was |
|---|---|---|
| b182 | `RC_VDEC_AU_SLOTS` 8 | a 30 fps number; frames arrive twice as often |
| b182 | dropped frames never asked for a keyframe | the chain broke and stayed broken |
| b183 | `BUSY` treated as failure | it is back-pressure; the decoder's queue is 4 deep |
| b184 | `num_spus = 1` | one SPE decodes ~41 fps; a sixth sat idle |
| b186 | two picture buffers, one published slot | 1,777 decoded, 960 delivered |
| b187 | the blit dropped rather than waiting | 1,777 decoded, 1,128 shown |

The last one is the clearest case of a stale rationale. `on_picture` refused to wait for the display
because waiting blocked the thread draining the socket — true, and measured, in b87. Decode and blit have
had their own thread since b144, so the cost of waiting became "a decode thread pauses for a vsync",
which the picture ring exists to absorb. It waits now, bounded at 25 ms, and the longest wait observed is
15 — one vsync.

**Result: 1,748 of 1,769 frames decoded, all 1,748 shown, 0 dropped at the display, 2 units lost, audio
clean.** What remains is 17 submissions still refused after the 6 ms BUSY wait, which is the decoder
briefly saturated and costs about 1% of frames.

### The SPE colour pass is compute, not DMA — **measured, and it overturned the prediction**

```
last frame: 1,667 us waiting for the MFC, 16,444 us converting and scaling (summed over 4 SPEs)
```

Reading the kernel suggests the opposite. It uses one DMA tag and blocks on every transfer — roughly 270
blocking puts and 180 blocking gets per SPE per frame — so the obvious conclusion is that it is stalled on
DMA, that double buffering is the fix, and that taking `ARGB32` from the decoder would make things worse
by inflating transfers 2.7x. **Waiting is 9% of it.**

So the conversion is the cost, `ARGB32` output (accepted and filled, b179) is the right optimisation, and
double buffering would buy almost nothing. Scaling still has to happen, so the SPE becomes a scale-only
pass rather than going idle — but that pass is most of 16 ms, and at 60 fps that is the headroom worth
having.

Worth noting the instrument was wrong first: the SPU decrementer does not run until it is written, so
b182 and b183 reported zero for both halves and printed nothing at all.

### The decoder converts the colour; the SPEs only scale — **measured, 2026-09-15**

`vdecGetPicture` will produce `VDEC_PICFMT_ARGB32`, so the YUV-to-RGB pass on the SPEs is work nobody has
to do. Enabled with `decoderrgb=1` in the pairing record.

| | YUV420 in | ARGB32 in |
|---|---|---|
| SPE, converting and scaling | 16,444 us | **10,876 us** |
| SPE, waiting for the MFC | 1,667 us | 1,892 us |
| blit, wall clock | 5,091 us | **4,075 us** |
| decode + blit | 5,229 us | **4,207 us** |
| on screen | 58 fps | 58 fps |

The conversion was a third of the SPE's arithmetic and it is gone. What remains — 10,876 us — is the
scaler alone, which is still scalar: one output pixel per iteration through a 16.16 accumulator. That is
the next thing to vectorise if the budget ever matters, and at 25% of a 60 fps frame it does not yet.

The change was small because of the kernel's shape: `convert_line` fills a 32-bit-per-pixel line buffer
and `scale_line` maps it to the output, so a packed-RGB row IS that buffer's contents and is DMA'd
straight into it. The packing needed no swizzle either — `convert_line` writes `0x00RRGGBB` and the
decoder's ARGB32 is `0xAARRGGBB` in the same byte order, so the alpha lands in the byte the display
ignores.

**It cost one hard lockup to get here, from a mistake worth naming.** `RC_VDEC_PICTURE_BYTES` was sized
`1920*1088*3/2`, a YUV420 picture. Packed RGB is four bytes a pixel, so the decoder wrote half a megabyte
past the end of every slot into the next one. The bound that should have caught it computed the YUV size
too, so it passed every time. Buffers are now sized for the widest format and the bound takes the
bytes-per-pixel of the format actually asked for.

### The decoder's colour is byte-exact, and the SPE split is 3/3 — **2026-09-15**

Two questions the RGB path left open, both now answered on hardware.

**Is the conversion right?** The capture's first pixel is `Y=24 U=133 V=125`. The SPE kernel's own BT.709
limited-range arithmetic gives `R=3 G=9 B=19`; a decoder leaving the input at full range would give
`24 24 24` and lift every level above it. The decoder returns `ff 03 09 13` — alpha, then exactly those
three values. Same conversion, same channel order, no shift.

**Where do the SPEs go?** Three each. The converter held four while it converted and scaled; it only
scales now, so it gave one to the decoder — which had been refusing about a dozen submissions a run, all
on the large frames of a transition, and a refusal breaks the reference chain. That was the visible
blockiness and it is gone.

The cleanest run so far: **1,753 pictures of 1,778 frames, zero decode errors, 58 fps, two IDR requests
and six lost units in thirty seconds.**

Scaling on three SPEs costs 4,943 us a frame against 3,969 on four, which the budget absorbs. The 10,876
us of arithmetic is now entirely the scaler, still scalar - one output pixel per iteration through a
16.16 accumulator - and vectorising it is the next win if one is ever needed.

### The scaler, vectorised — **2026-09-15**

With the conversion gone the SPE's whole cost was the scaler, written one output pixel at a time. That is
expensive for a reason particular to this machine: **the SPU has no scalar store.** Writing one 32-bit
word to local store is a read-modify-write of the entire 16-byte quadword, so `g_out[x] = g_line[idx]`
cost a load, a rotate, an insert and a store per pixel. Four are now built in a register and stored as
one quadword.

| | before | after |
|---|---|---|
| SPE scaling | 10,876 us | **4,386 us** |
| SPE wall clock | 4,943 us | **2,961 us** |
| blit | 4,079 us | **3,056 us** |
| decode + blit | 4,779 us | **3,128 us**, 9.4% of a 60 fps frame |
| on screen | 58 fps | **59 fps**, 1,781 of 1,785 frames, zero errors |

The gather is untouched and cannot be vectorised the same way — each output pixel picks a source at an
index the accumulator computes, and the SPU has no vector gather. The prediction attached to that was
"a fraction rather than a factor", and it was wrong: the store dominated, and the scaling term fell 2.5x.

**It also inverted the DMA question.** Waiting for the MFC was 1,667 us against 16,444 us of arithmetic —
9%, which is why double buffering was measured and rejected. It is now 4,007 us against 4,386 — 48%. The
right answer changed when the other half got faster, and double buffering is where the next win is if one
is ever wanted.

### Bilinear upscaling — **built, measured, and off by default**

`bilinear=1` in the pairing record. It works; it costs too much for 60 fps.

| | per SPE, one frame | summed over 3 |
|---|---|---|
| nearest neighbour | 3,017 us | 4,173 us of arithmetic |
| bilinear | **21,038 us** | **62,489 us** |

A 60 fps frame allows 16,667 us, so bilinear does not fit and the pipeline halves to 29 fps. At 30 fps it
fits inside 63% of the budget, which is where it is usable as written.

Two attempts were needed and the first is worth keeping in view. Written scalar - nine interpolations a
pixel, each extracting a byte from a word and putting one back - it missed the 25 ms strip deadline so
completely that not one stream frame was converted and nothing reached the screen. Vectorised, with a
pixel's four channels unpacked into 32-bit lanes and interpolated as one vector, it converts every frame
with no fallbacks. That fixed the deadline; it did not make it cheap.

The arithmetic was checked on the development machine against exact bilinear over 200,000 random inputs:
1.98 levels of worst-case error truncating, 1.00 with the lerp rounded, never outside 0..255. That host
check is the ONLY correctness evidence this path has - `rc_video_self_test` compares the SPE against the
PPE and there is no PPE bilinear to compare against.

What would make it fit at 60 fps is processing four output pixels in parallel lanes rather than one
pixel's four channels, which needs the gather done with shuffles out of two quadwords per row. That is a
much larger rewrite than this one. Horizontal-only interpolation is the cheaper middle - one vector lerp
a pixel instead of three, two unpacks instead of four - and would remove the column doubling that a 1.5x
scale makes most visible, at perhaps a third of the cost.

### Still open
- **The fifth SPE — `ARGB32` output works.** Asked offline in b179: `vdecGetPicture` accepts
  `VDEC_PICFMT_ARGB32` and fills the buffer (bytes 0..255 across 8 pictures). So the YUV-to-RGB pass
  really can come out of the SPE kernel.
  **But the conversion does not simply disappear, and this document said it would.** Scaling 1280x720 to
  a 1920x1080 display still has to happen somewhere, so the SPE becomes a scaler rather than going idle —
  a cheaper pass, not no pass. Two routes worth weighing before building either: keep the SPE and make it
  scale-only, or set the display to 720p and let the television scale, which removes the pass entirely at
  the cost of handing off scaling quality. Neither is urgent: decode and blit together cost 15% of a
  frame budget, and no other work is waiting on an SPE.
- ~~**Audio latency.**~~ **Tried and reverted.** About 93 ms sat between the decoder and the speaker and
  the stream underran exactly once, on the first block, so the depth looked like waste. It is not: audio
  arrives in network bursts and that depth is the jitter buffer. Cutting the hardware lead and trimming
  the ring to 20 ms bounded latency at 40 ms and discarded 167,488 samples in 251 trims — 11.6% of a run,
  stuttering throughout. The lead is back to six blocks and the trim is now a runaway guard at 100 ms
  that normal jitter never reaches. `ripcord-3ds` steers a rate loop instead, which works there because
  audio and the DSP share a timebase; that is the shape any future attempt should take, not a trim.
