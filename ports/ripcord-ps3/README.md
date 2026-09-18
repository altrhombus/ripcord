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

## The network track — measured, on hardware

The decoder is one half of this port. The other is reaching a console at all, and it is far enough along
to record. All of the below ran on a real PS3 against a real PS5; nothing here is argued from the source.

**Discovery, wake, and the control plane.** Discovery finds the console and parses its reply. A `WAKEUP`
from the spec's source port woke it from standby in **~12.3 s**, consistently, across every run that has
measured it — a figure worth knowing, because a deadline sized for an already-awake console reports a
slow console as an absent one. `ARM` → `/sess/init` → `/sess/ctrl` then open the binary control channel.

**The sign-in gate, and why it is a gate.** A console whose profile is locked answers the control session
with `LOGIN_PROMPT` (`0x0004`) and nothing else — no `SESSION_ID`, no heartbeats. This is not a courtesy
message to skip past: a console that has not authorised the client **silently drops every Takion INIT**,
so anything attempted past this point times out and looks like a transport fault. The reference client
answers the prompt with a passcode, and so does this port; the passcode arrives out of band as `pin=` in
the pairing record, the same route the registration key already travels, and is never logged.

Established by controlled experiment on 2026-09-14, one build, one variable: without `pin=` the run stops
at the prompt; with it, `SESSION_ID`.

**The console's half of the control channel is readable.** The control-field cipher's counter is
per-connection and shared across a whole *direction*, so each side counts independently: ours spends 0–4
on the `/sess/ctrl` request fields (which is why a passcode is 5), and the console spends 0 on its own
`/sess/ctrl` response, so its next encrypted frame is **1**. Only payload-carrying frames spend a counter
— heartbeats and the login prompt carry nothing and spend nothing, and since both are frequent, getting
that half wrong desynchronises almost immediately.

Confirmed on hardware rather than assumed: the console's `LOGIN` (`0x0005`) verdict decrypted at counter 1
to exactly `0x00`, "accepted". A wrong counter origin would not have produced a byte the port recognises.

**The session is negotiated, end to end.** On 2026-09-14 a PS3 woke a PS5, answered its sign-in gate,
brought up both Takion channels, ran senkusha's gating legs, and completed the ECDH exchange:
`SESSION_REQUEST` 1800 bytes on P-521, `SESSION_REPLY` 203 bytes, the reply's point verified under the
handshake key, and all four per-direction stream keys derived. Every check in the bring-up passed in the
same run.

**Two bugs it cost, and both are worth knowing.**

The first was in `ports/common` and every port had it. `takion_reassembler_first` returned two different
lifetimes: a message needing reassembly was copied into the reassembler and lived as long as the channel,
while a message arriving complete in ONE CHUNK had the caller's pointer handed straight back — and in
`takion_channel_poll` that buffer is a local. The short lifetime belonged to the common case, because
anything under about a kilobyte arrives in one chunk. Both headers already promised otherwise; the code
contradicted its own documented contract.

It impersonated a crypto fault for eleven hardware runs. A 203-byte `SESSION_REPLY` is one chunk, so its
HMAC verified — that read happens immediately — and the on-curve check failed a few calls later, once
mbedtls's own frames had overwritten the point. Every signpost pointed at the arithmetic: a well-formed
P-521 point, `INVALID_KEY` from the backend, the host build accepting the identical bytes, the same check
passing on the console against a copy. What finally said otherwise was fingerprinting the derivation's own
argument and finding it differed from the copy, plus the fact that adding a READ-ONLY call changed the
answer. A pure function cannot change what a buffer holds; a stack frame can.

The second was this port's Makefile, which tracked no header dependencies at all, so editing a header
rebuilt nothing that included it. A struct that gained a field left older objects reading the old layout —
which surfaced as one core suite failing on big-endian for a dozen builds. It also means readings taken
from non-clean builds during that window are suspect. `-MMD -MP` now, and the two recipes that build with
`-Dmain=...` needed it added by hand: they did not match the shape of the others, and they were precisely
the stale ones.

**A side effect worth keeping.** The stack probe added while chasing the first bug settles a question this
port had assumed since its first boot. PSL1GHT's header defines the process stack size as an *enum*
(`0x70` = 1M) while its own samples pass a raw byte count; this port passes `0x100000`.
`sysThreadGetStackInformation` reports **1,048,576 bytes given, ~6 KB used** at the deepest point of the
session exchange. lv2 takes the byte count. The value was right, and now it is measured rather than
believed.

**Takion is up.** Senkusha first — the console gates the stream channel's `SESSION` exchange on a senkusha
bring-up having happened — then the stream channel's own handshake on a different UDP port. Both completed
their four-way handshake on 2026-09-14 and returned a non-zero **peer tag**, which is a number the console
chose and cannot be manufactured at this end.

The control session stays open throughout, serviced by a tick callback roughly every 20 ms. That shape is
load-bearing and not a style choice: the console resets a session 15–30 s after heartbeat replies stop,
the UDP waits are longer than that, and this port has one thread.

**Sealed, and the console has described the stream.** Once the keys exist, GMAC sealing goes on before
anything else is sent: from that moment the console authenticates every control packet it receives, so the
first unsealed one is the last it listens to. SACKs are sealed too, which is a documented way to lose a
session — the channel routes every outgoing control packet through one callback, so that is right by
construction rather than by anyone remembering it.

`STREAM_INFO` then arrives unprompted and is acked. On 2026-09-14 it carried **330 bytes**: a request for
960×540 granted at 960×540, a **40-byte video header** (the SPS/PPS, without which the first IDR cannot be
decoded — they are not carried in the video stream) and a **14-byte audio header**. It is acked whether or
not its payload parses; the ack means "received", and withholding it because this build could not read a
field would stall a console that had done nothing wrong.

**The console is streaming, and every packet authenticates.** On 2026-09-14, six seconds of a held
session carried **1250 A/V packets, 842,990 bytes** — 653 video, 597 audio — and **1250 of 1250**
verified under the A/V rule.

That rule is not the control rule, and the differences are exactly where it goes wrong quietly: the tag
sits at offset 10, the key position travels **in the packet** at offset 14 rather than being ours to
choose, and only the tag region is zeroed in the AAD — not the key position. Getting either half wrong
makes every packet fail, which is a far better outcome than silently decrypting noise.

**And it had been arriving all along.** One UDP socket carries both the control association and the A/V
stream, and `takion_channel_poll` does an unconditional `recvfrom`: anything it did not recognise was
consumed and discarded. Runs before this one reported "the console sent only control traffic" and could
not have been right — "no A/V" and "A/V thrown away" were indistinguishable. `MSG_PEEK` reads the base
type (the low nibble of byte 0: 0 control, 2 video, 3 audio) without taking the datagram, so each one goes
to whichever reader owns it.

**There is a picture on the television.** The display came up on 2026-09-14 at **1920x1080, pitch 7680,
double-buffered** — the console's own mode, read from `videoGetState` rather than chosen.

Getting there took five wrong hypotheses and one useful failure, and both halves are worth recording:

- `rsxInit`'s IO region must be **1 MB-aligned AND a whole number of megabytes** (`rsx.h` says so in the
  paragraph above the sequence). 512 KB gave `0x802100FF`.
- `rsxInit` and `videoGetState` are **PRX imports** — `nm` shows them as `D` symbols with `_stub`
  entries. `sprxlinker` patches the call sites, but `sysModuleLoad(SYSMODULE_GCM_SYS)` and `SYSUTIL`
  must still make the modules resident. Networking never needed this because `netInitialize` loads its
  own; the display has no such courtesy.
- The **command buffer is carved out of the IO region**, so asking for 1 MB of command buffer inside a
  1 MB region leaves nothing for the heap `rsxInit` builds. A small command buffer in a larger region is
  the shape that works.
- **`videoGetState` reports 0 on a working display**, which PSL1GHT's own header calls
  `VIDEO_STATE_DISABLED`. The value is recorded and never judged — refusing to open on it would
  manufacture exactly the black screen this was trying to explain.

The useful failure was moving the display check to the **front** of the run. It failed there too, which
eliminated in one go every theory about networking, the raw SPU, openh264 or the decoder holding
something the RSX needed. After four wrong guesses about the call, `rsxInit` is now **swept** across
several size pairs rather than given one — the same discipline the writable-directory probe uses.

## A PS5 stream, on a PS3, on a television — measured

On 2026-09-14 the whole thing ran end to end, unattended, for thirty seconds:

| | |
|---|---|
| A/V packets | **6287 of 6287 authenticated** |
| video frames demuxed | 893 |
| units lost / loss events | 9 lost, **0 events** — FEC recovered every one |
| decoded | **892 pictures from 893 frames, 0 errors** |
| on screen | **891 pictures, 29 fps** |
| decode | 11,731 µs per picture |
| colour conversion | 12,037 µs per picture |
| total against the 30 fps budget | **23,768 µs of 33,333 µs** |

Getting from a first picture to that number took four runs, and every one of them fixed something
self-inflicted rather than anything the console did:

- **The display stalled the receive loop.** Blitting and waiting for vsync on the thread draining the
  socket cost 90 units and 30 loss events. Presenting is non-blocking now, and a picture arriving while
  the previous flip is in flight is dropped whole — a dropped frame costs one frame, a waited-on frame
  costs every packet that arrives during the wait.
- **The conversion did twice the chroma work it needed.** 4:2:0 means a column *pair* shares one sample;
  indexing `u[col / 2]` per luma pixel is an integer divide and a redundant load for every pixel.
- **Loss was counted and not acted on.** Over one run the console sent 893 frames and *one* keyframe,
  and 686 frames failed with `dsNoParamSets | dsRefLost`. An inter-frame gap is not one lost frame, it
  is every frame until the next keyframe — and the console only sends one **when asked**. Requesting an
  IDR on loss (throttled to the reference's 200 ms) took decoding from 205/893 to 717/858.
- **The loop slept a millisecond per packet.** At ~190 packets/s that is 190 ms of every second asleep
  with data waiting, on top of 24 ms per frame of work. It now drains up to 64 datagrams per pass and
  sleeps only when nothing arrived.

**The instrument is still flagging one thing**: the worst drain burst reaches the bound, 64 of 64.
Benign at this bitrate — yielding every 64 packets is every ~300 ms against a 1000 ms heartbeat — and
recorded rather than tidied away, because it will matter if the bitrate rises.

## Colour conversion on the SPEs

The PPE converted a 960x540 picture in **11,869 us** while also spending 11,731 us decoding — 72% of a
30 fps budget for a window occupying a quarter of the screen. The arithmetic is per-pixel with no
dependency between pixels, which is the shape the SPEs exist for.

| | µs per picture |
|---|---|
| PPE, scalar | 11,869 |
| 5 SPEs, scalar | 5,884 |
| **5 SPEs, SIMD** | **1,020** |

The middle row is the instructive one. Five processors bought only 2x, because **per pixel each SPE was
2.5x slower than the PPE**: there is no scalar unit on an SPE, so every scalar operation is a vector
operation with the value extracted and reinserted around it, plus a branch per channel per pixel on a
processor with no branch predictor. The parallelism was buying back what the instruction mix threw away.

The vector version keeps values as 32-bit lanes and multiplies with `spu_mulo`, which takes the **odd
halfwords** of two short vectors — on this big-endian machine, the low 16 bits of each 32-bit lane. Every
value fits in 16 bits, so that is a 16x16 -> 32 multiply per lane with no packing. Clamping is
`spu_cmpgt`/`spu_sel` rather than branches.

**It is checked, not assumed.** The first converted frame of every run is done *both* ways and the results
hashed and compared, because a coefficient in the wrong lane or a shift off by one gives a picture that is
present and subtly wrong — exactly what a glance at a television does not catch.

**Three shutdown lockups, and what they taught.** Two tidy teardowns froze the console: terminating SPEs
blocked in a mailbox read (they cannot notice), then asking them to leave and joining the group. The
teardown now writes a quit sentinel and waits for nothing — at process exit lv2 reclaims the thread group
anyway, and cleanup that *cannot hang* is worth more here than cleanup that is thorough. More valuable
still was the ordering fix: **the logs close before the teardown**, so a frozen console costs a reboot
rather than the run that would explain it.

## Scaling, and where the budget actually goes

The display is whatever mode the television negotiated and the source is whatever the console agreed to
send, so scaling is a permanent part of the path rather than an alternative to choosing a resolution
well. It lives in the SPE conversion because that already touches every output pixel.

On 2026-09-14, a 960×540 stream scaled to **1920×1080 full screen at 29 fps**, 891 pictures, nothing
dropped, no fallbacks, no decoder errors, no loss events:

| stage | µs per frame | share of a 30 fps budget |
|---|---|---|
| decode (openh264, PPE) | ~11,490 | 34% |
| convert **and scale** to 1080p (5 SPEs) | **3,053** | 9% |
| **total** | **14,544** | **44%** |

Scaling to four times the pixels cost about 2,000 µs more than converting at source resolution. The
scale is the **smaller** of the two ratios, so the picture fits in both directions and is centred in
whichever has room left — taking the larger would fill the screen by cropping, and cropping a game
someone is playing is worse than a border. Nearest neighbour for now `[X]`: exactly correct for the
integer factor that matters most here, and not yet compared against bilinear on hardware.

**What that leaves.** Decode is now the only significant term. Both of the first two rows are measured:

| source | decode | convert + scale | total | 30 fps budget |
|---|---|---|---|---|
| 960×540 | 11,491 | 3,053 | **14,544** | ✓ 44% |
| 1280×720 | 18,722 | 4,064 | **22,786** | ✓ 68% |
| 1920×1080 | ~37,300 `[X]` | ~3,000 (1:1) | ~40,300 | ✗ 121% |

**Decode scales as pixels^0.85, not linearly** — 22.2 ns/pixel at 960×540 against 20.3 at 1280×720, so
larger frames are slightly cheaper per pixel. The first 1080p estimate here assumed linear and was 8,600 µs
too pessimistic; the exponent comes from the two measured points and the 1080p row is still marked `[X]`
because it is extrapolation, not measurement.

720p is comfortable. 1080p is roughly 121% of a 30 fps budget on openh264 — not a tuning problem.
PSL1GHT exposes the PS3's own H.264 decoder (`codec/vdec.h`, `libvdec.a`, `SYSMODULE_VDEC_H264`), which
is the path to 1080p and would free the PPE almost entirely, the same shape as the 3DS port's MVD
hardware path.

## Senkusha's measurement legs — done, and they were not the bitrate

At 720p the console sent ~1.35 Mbps while the launch spec asked for 8,000 kbps. The obvious suspect was
that the spec declared `rtt 0` and a default MTU — figures nobody had measured — so the echo and MTU legs
were implemented against the .NET side:

```
senkusha echo: 10/10 echoes, handshake round trip 21 ms
declared rtt 1 ms (from the echo probe), mtu 1454 (confirmed both directions)
```

Both legs work. The spec now declares measurements instead of assumptions, which is worth having on its
own. **And the bitrate did not move** — 1.35 Mbps before, 1.35 Mbps after. The hypothesis was wrong.

Three details came from the reference rather than from `ports/ripcord-3ds`, and are worth keeping:

- **The declared RTT is the MINIMUM of the samples**, rejecting non-finite and negative ones. A sample can
  only be inflated by delay, never deflated, so the smallest round trip is closest to true path time and
  an average would report the queue instead.
- **The sample set is seeded with the version handshake** — the purest sample available, because answering
  a version request asks the console to do essentially no work. Safe *only* because the result is a
  minimum.
- The reference warns that since handshake RTT is a fallback, **a non-null RTT does not mean the echo
  probe succeeded**. This port reports the echo count and the figure's provenance separately so that
  ambiguity does not exist here.

The MTU close is unconditional: past the open the console *is* in client-MTU mode with no other way of
being cleared, and the .NET side records that this was once a sequential send a timeout could unwind
past, leaving the console stuck.

## Congestion feedback — done, and the bitrate was never the protocol

The reference sends a sealed 15-byte type-5 packet every 200 ms carrying received/lost unit counts, and
describes it as what lets "the console's rate controller adapt the encoder bitrate to the link". This
port sent none, which made it the obvious remaining suspect. It now sends them — 135 in a 30-second run —
and the bitrate did not move. **1.35 Mbps before, 1.35 Mbps after.**

Three explanations were offered for that number and all three were wrong: decode headroom, then the
declared `rtt`/`mtu`, then congestion feedback. Looking at the frames instead of the protocol settles it:

| | |
|---|---|
| video payload | 3,141,755 bytes over 30 s = **0.84 Mbps** |
| keyframe | 43,923 bytes |
| average inter-frame | 3,473 bytes → **0.03 bits per pixel** at 1280×720 |

Typical H.264 at moderate motion is 0.05–0.15 bpp. **0.03 bpp is what a near-static picture costs.** The
encoder was never being throttled — it had nothing to spend bits on, because the console was showing
essentially still content throughout every measurement. No declared figure can change that.

The work was still right to do. A client that never reports what arrived is not a correct client
regardless of what the console does with it, and the same is true of the senkusha legs: a launch spec
declaring figures nobody measured is a defect whether or not it is the binding one. But neither was the
answer to the question that motivated them, and the honest measurement of throughput needs **moving
content on the console**.

**Where congestion feedback lives, and why.** In `ports/common`, on the sealer — because the reference is
explicit that the outgoing key position is one sequence shared by control DATA, SACKs and congestion
packets alike. A congestion path with its own counter would repeat a position control had already spent,
and a repeated position is a repeated GMAC nonce under one key. Three offset pairs that look alike and
are not: control tag@5/pos@9, congestion tag@7/pos@11, A/V tag@10/pos@14 — and A/V zeroes only the tag in
its AAD where the other two zero both. PSL1GHT exposes the PS3's own H.264 decoder
(`codec/vdec.h`, `libvdec.a`, `SYSMODULE_VDEC_H264`), which is the path to 1080p and would free the PPE
almost entirely — the same shape as the 3DS port's MVD hardware path.

**A lesson that cost a run.** The SPE/PPE agreement check originally ran on the first live frame. At
960×540 that was affordable; at 1920×1080 it became two 2-million-pixel hashes and a full PPE conversion,
stalling the receive loop for over a tenth of a second and killing the Takion channel — 58 packets where
the previous run saw 6,287. **Something added to prove correctness destroyed the thing it was proving.**
It now runs once before streaming on a synthetic picture, which is also a better test: the gradients
sweep both chroma channels and luma across their whole range including the values that clamp, and it
scales, so the scaler is part of what must agree.

**What is not done.** Incoming GMAC tags are
**not verified `[X]`**: GMAC authenticates without encrypting, so `STREAM_INFO` parses as it stands and this
build reads it without checking who wrote it. That is the receive half of the sealing mechanism and a real
gap rather than an oversight. Senkusha's echo and MTU measurement legs are not run, so
the launch spec declares `rtt 0` and a default MTU `[X]`; the 3DS port's note on that is worth heeding,
since it omitted the echo leg for five phases on the reasoning that it only tunes bitrate, and the
declared RTT turned out to be an input the console uses. Log rotation is implemented in `ports/common`
but **has not yet fired on hardware `[X]`** — the log has not reached the threshold.

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
