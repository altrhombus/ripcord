# The decode path

Everything else in a PS3 port is affordable — `ports/common` (on `feat/vita-port`) already carries the
protocol, transport and crypto, and `rc_platform.h` asks for four functions. This document is about the
part that is not.

`ripcord-3ds` feeds Annex-B NAL units to the New 3DS's MVD block and gets pictures back; the hard part of
video was a driver call. The PS3 has no exposed fixed-function H.264 decoder. Its Blu-ray playback is
**software decode on the Cell SPUs**, and so is anything we do here `[X — asserted from general knowledge
of the platform, not verified against hardware]`.

So the decoder is ours, and two questions decide what it costs: **what licence it can be built from**, and
**what the console actually sends**.

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

**Recommendation:** do not decide yet. Answer §2 first — if the answer is Constrained Baseline, from
scratch becomes attractive and the licence question evaporates. If it is High/CABAC, port from openh264.
Either way, **not FFmpeg.**

---

## 2. The decisive unknown

**Nothing in this repository records which H.264 profile the PS5 sends `[X]`.**

`docs/protocol/` establishes that the stream is Annex-B and that SPS/PPS arrive as their own NAL units
prepended to the first IDR (`ports/ripcord-3ds/source/media/rc_mvd.h`). It does not record:

| Field | Where | Why it decides the design |
|---|---|---|
| `profile_idc` | SPS | Baseline vs Main vs High — sets the feature surface |
| `entropy_coding_mode_flag` | PPS | **CABAC or CAVLC.** The biggest lever on both effort and parallelism |
| `transform_8x8_mode_flag` | PPS | A second transform path if set |
| slices per frame | slice headers | Whether entropy decode parallelises across SPUs at all |
| B-slices present | slice headers | Reordering delay; low-latency encoders usually avoid them — confirm |

All five come out of **one SPS, one PPS and one frame of slice headers in a capture already held.**
Cheapest experiment available, and it gates everything below. Task one.

That MVD accepted the stream is indirect evidence it is not exotic, but MVD's own profile support is not
documented here either, so it constrains nothing usefully.

---

## 3. Where the parallelism is, and where it is not

Cell for homebrew: one PPE (in-order PowerPC, 2-way SMT, 3.2 GHz) and **six usable SPEs**, each with
**256 KB of local store**, 128-bit SIMD, no cache, explicit DMA.

The 256 KB is the binding constraint and shapes everything. A 720p luma plane alone is ~921 KB, so an SPE
never holds a frame — it works in macroblock-row stripes with double-buffered DMA in and out. Standard
Cell pattern; it just has to be designed in rather than retrofitted.

**Entropy decode is the serial stage.** CABAC is context-adaptive and bit-serial: it cannot be SIMD'd and
cannot be split *within* a slice. CAVLC is table-driven and cheaper but still serial per slice. Everything
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

**The lever that changes this picture is slices per frame.** If the PS5 emits several independent slices
per frame — likely, given low-latency encoding and the documented FEC scheme — each slice is independently
entropy-decodable, SPE 0 stops being a bottleneck, and the entropy stage spreads across as many SPEs as
there are slices. That is why slice count is in §2's table, and it may be the difference between
comfortable and marginal.

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

1. **Parse SPS/PPS from an existing capture.** Answer every row of §2. Half a day; decides §1.
2. **Bitstream reader + SPS/PPS/slice-header parser**, portable C, host-tested in
   `ports/ripcord-ps3/tests/` against vectors from `dotnet run --project tools/Ripcord.ProtocolLab --
   vectors` — the pattern `ports/ripcord-3ds/tests/` already uses, so it needs no console.
3. **Annex-B splitter and slice-boundary extraction**, likewise host-tested. The seam between the existing
   demuxer and any decoder, and useful whichever route §5 takes.
4. **`rc_platform_ps3.c` and a PSL1GHT skeleton** that links and prints a timestamp. Cheap, and it flushes
   out the toolchain before anything depends on it. This is the step that wants `ports/common`, so rebase
   onto it here rather than earlier.
5. **Choose the decoder base** on the evidence from 1.
6. **SPU bring-up**: one SPE running a trivial DMA job, measured. Establishes the toolchain and job model
   before codec work rides on it.
7. **Decoder proper**, stage by stage, against the same vectors.

Steps 2 and 3 are worth doing regardless of how 5 resolves, which is the argument for starting there
rather than with the SPU.
