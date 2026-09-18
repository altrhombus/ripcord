# ripcord-ps3 — setting up a Linux build box

Same answer as the 3DS, for stronger reasons. [`ports/ripcord-3ds/SETUP.md`](../ripcord-3ds/SETUP.md) is
the doc this one is modelled on and most of it applies unchanged; what follows is the PS3-specific half.

**This has now been run, and section 2 is not what it used to say.** The first revision of this
document described building two cross-compilers from source, and marked every line of it `[X]`. That
turned out to be the hard way round: ps3dev publishes prebuilt nightly archives, and one of them is a
working toolchain in about two minutes. Sections 2 and 3 below record what was actually executed on
2026-09-11 — Ubuntu 26.04 under WSL2, ps3dev `nightly-2026-07-26`, all three gates green — and the
source build is kept at the end of section 2 as the fallback it turned out to be.

Fix this file as you go; that is still what it is for.

## Why Linux, and why more so here

The 3DS could plausibly be built from devkitPro's MSYS2 shell on Windows. This port has less choice:

- **Two cross-compilers, and the second one is the entire port.** Steps 6 and 7 of
  [`README.md`](README.md)'s order of work are SPU code: `spu-gcc`, embedding SPU images in the PPU
  binary, and a job model. Strictly more toolchain surface than either port before it. The prebuilt
  archive ships both, so this is an argument about what you will be *using*, not about a build cost —
  see the correction below.
- **Everything here already assumes a POSIX shell and GNU make.** `ports/common/tools/build-mbedtls.sh`
  is `#!/bin/sh`, and every Makefile in `ports/` is GNU make with a gcc flag set (`-Wconversion`,
  `-Wframe-larger-than`, `-ffunction-sections`). On a Windows box without them you end up hand-rolling
  equivalent MSVC command lines, which does produce answers but is not a workflow.
- **The numbers have to be comparable.** The bring-up program exists to produce a figure that can sit
  beside the 3DS's. Same box, same habits.

**One bullet that used to be here was wrong, and it is worth keeping the correction visible**, because
it was the argument that made this port look expensive to start. It read: *the toolchain is built, not
installed — devkitPro ships a pacman repository; ps3dev is a source build that compiles binutils and GCC
twice.* That is true of `ps3toolchain`, and it is not what you have to do. `ps3dev` publishes prebuilt
nightly archives and the Linux x64 one unpacks into a complete environment. The bullet was reasoned from
the repository's README rather than from its releases page, which is exactly the failure mode `[X]`
exists to flag — it was marked, and it was still load-bearing for a conclusion.

## 1. Getting the tree across

Everything in the 3DS doc's section 1 applies — decide about the dirty room, drop `bin/` and `obj/`,
and note that a bare `git clone` does not produce a buildable tree. Two additions for WSL.

**Prefer the Linux filesystem, but `/mnt/c` does work.** Everything in section 3 below was run against
a `/mnt/c` working copy, straight from the Windows checkout, and all three gates passed. The 9p boundary
is slow and `~/ripcord` is the better place for the core and any decoder work; it is a preference, not a
gate. The correctness reason that used to make it one has been checked and is no longer live — see the
next paragraph.

**Line endings, which bit this repository and were fixed on 2026-09-11.** `.gitattributes` now carries
`*.sh`, `Makefile` and `*.mk` as `text eol=lf`, so a Windows checkout no longer produces CRLF copies of
files only Linux runs. Before that rule existed, `build-mbedtls.sh` checked out with CRLF, and since
the 3DS doc moves the tree with **rsync** — which copies bytes rather than re-running a checkout —
whatever Windows wrote is exactly what Linux would have run. `#!/bin/sh\r` is not an interpreter any
kernel recognises and the error says so in none of those words; make is worse, because it mostly
tolerates CRLF and then passes the stray carriage return into a recipe's shell, so the failure appears
somewhere else entirely.

If you are copying a tree that predates that fix, or you are unsure:

```sh
file ports/common/tools/build-mbedtls.sh ports/*/Makefile
#    expect: ... ASCII text executable     (NOT "with CRLF line terminators")
```

A `/mnt/c` working copy *is* the Windows checkout, so the question is what Windows wrote — and with the
`eol=lf` rule in place, it writes LF. Checked on this tree on 2026-09-11: `build-mbedtls.sh` and all four
port Makefiles report plain `ASCII text`, and both host suites and the cross-compile ran from `/mnt/c`
unmodified. Run the `file` check above on a tree of unknown vintage; do not assume the path decides it.

## 2. Toolchain

### ps3dev, from a prebuilt archive

This is the path that was taken and it is the one to take. `ps3dev` publishes nightly archives of the
whole environment — PPU and SPU GCC, PSL1GHT, ps3libraries, and the `fself`/`sfo`/`pkg` tools — built by
its own CI. Pick the newest `ps3dev-linux-X64.tar.gz` from
<https://github.com/ps3dev/ps3dev/releases>; what is recorded here is `nightly-2026-07-26`, 173 MB
compressed and 577 MB unpacked.

```sh
curl -sSLO https://github.com/ps3dev/ps3dev/releases/download/nightly-2026-07-26/ps3dev-linux-X64.tar.gz
tar xzf ps3dev-linux-X64.tar.gz -C "$HOME"      # the archive's top-level directory is ps3dev/
```

**It is relocatable, and that is worth knowing before you reach for `sudo`.** Upstream documents
`/usr/local/ps3dev` and `$PS3DEV/base_rules` defaults to that path when the variable is unset — but
nothing in the archive has the install prefix baked in, and unpacking it into `$HOME` produces a working
compiler. This tree was built from `~/ps3dev`. Use `/usr/local/ps3dev` if you want to match upstream's
default and the `vitasdk` install beside it; either works, and Ripcord's Makefiles hard-error on an
unset `$PS3DEV` rather than guessing, so there is no silent wrong answer available.

**`PSL1GHT` is `$PS3DEV`, not `$PS3DEV/psl1ght`.** An earlier revision of this document said the latter
and it is wrong: the archive puts `ppu_rules`, `base_rules` and `data_rules` at the top level beside
`ppu/` and `spu/`, and `ppu_rules` itself resolves `-I$(PSL1GHT)/ppu/include` against that. The two
variables are the same directory.

```sh
export PS3DEV=$HOME/ps3dev
export PSL1GHT=$PS3DEV
export PATH=$PATH:$PS3DEV/bin:$PS3DEV/ppu/bin:$PS3DEV/spu/bin
ppu-gcc --version    # expect: ppu-gcc (GCC) 7.2.0
spu-gcc --version    # expect: spu-gcc (GCC) 7.2.0
```

Add all three to your shell profile. `$PS3DEV/build.txt` in the archive names the exact ps3dev, PSL1GHT
and ps3libraries commits it was built from, which is the thing to quote when a build behaves oddly.

Two notes on what this does *not* need. The compiler binaries are self-contained — `ldd` resolves
everything against the host's own libraries, so no `libgmp`/`libmpfr` hunt. And the long apt list in
ps3dev's README is a list of build *dependencies*; none of it is required to use a prebuilt archive.
You still want `build-essential` for the host-side gates in section 3.

### Building the toolchain from source — the fallback

Only if no prebuilt archive suits you: a different host architecture, or a toolchain change you need to
carry. `ps3dev/prepare.sh` installs the apt dependencies and `./build-all.sh` drives it, with the same
two variables exported first. Expect several gigabytes and a long first run.

**Not attempted here, and there is a specific reason to expect friction `[X]`:** the source build
compiles GCC 7.2.0 and binutils 2.22, released in 2017 and 2011, using whatever host compiler you have.
This box has GCC 15. That combination is a well-known source of build failures in old autotools trees,
and it is the second argument for the prebuilt archive after the two minutes.

### .NET SDK, host C toolchain, ECDH backend

Identical to the 3DS doc's section 2 — the same SDK, the same `build-essential python3`, and the same
choice of ECDH backend. Nothing in this port changes any of it, and the PS3 has no equivalent of
devkitPro's `3ds-mbedtls` package, so the local build (`ECDH_BACKEND=local`, the default in
`ports/common/tests/Makefile`) is the path of least resistance here. It is what ran below, and
`ecdh_test` reported no skips.

None of it is needed for section 3 step 1.

## 3. Verify the setup

In order. Each is a real gate; do not skip ahead when one fails. **All four gates below have now been
executed except the last**, which needs a console.

```sh
# 1. The H.264 front end - steps 2 and 3 of the order of work. No console, no cross-compiler,
#    no .NET.
make -C ports/ripcord-ps3/tests
#    expect: 142 passed, 0 failed   (h264_test)
#            36 passed, 0 failed    (random_test - the CSPRNG seam's host-testable half)
#    and, printed along the way:
#            SPS: profile=77 level=31 640x368 ...
#            stream: 110 bytes -> 3 access units, 3 pictures, 4 slices
#            sizeof(rc_h264_au) = 19320 bytes

# 2. The shared core's known-answer suite, which is what actually verifies the crypto and transport.
#    Emit the vectors first - the C runners read .kat files, they do not generate them.
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#    expect: wrote ports/common/tests/vectors/control-crypto.kat  (and three more beside it)
make -C ports/common/tests
#    expect: 3,287 assertions across eleven runners, 0 failed, 0 skipped
#    if ecdh_test reports a skip, the ECDH backend is missing; that is a configuration, not a failure

# 3. The cross-compile.
make -C ports/ripcord-ps3
#    expect: ripcord-ps3.self, ~296 KB, and build/ripcord-ps3.elf beside it

# 4. The bring-up program, on hardware. `make -C ports/ripcord-ps3 pkg`, install the .pkg, run it,
#    and read ps3-bringup-lv2.log from /dev_hdd0/game/RIPC00003/USRDIR/.
#    expect: "all checks passed", three ok lines, ~5 seconds of wall time, and a measured
#            time base within a hair of 79,800,000 Hz (it came out at 79,800,986)
#    CHECK THE BUILD ID on the first log line against what `make` printed. See section 6.
```

**Gate 1 needed a one-line fix to pass at all.** `tests/Makefile` was written when the bitstream reader
and parameter-set parser were the only sources; the commit that added `rc_h264_annexb.c` added its
tests but not the file, so the link failed on every `rc_h264_annexb_next` and `rc_h264_au_feed` in the
suite. The figures this document quotes were therefore real — someone built them — but not reproducible
from the committed Makefile until now. Worth remembering when a gate is described as known-good: what is
known-good is the code, and the thing that rots is the build that reaches it.

**Gate 3 needed two changes, and only one of them was a failure.** `-std=c99` does not work against
PSL1GHT at all — its syscall wrappers use `register u64 p1 asm("3")`, and `asm` is not a keyword in
strict ISO mode, so the first include of `<sys/systime.h>` produces a page of errors pointing into the
SDK's own headers. It reads like a broken installation and is not one; the fix is `-std=gnu99` and it is
commented in the Makefile. The other change was the architecture flags, which *compiled fine* as the
written-blind `-mcpu=cell` — they were changed to `-mhard-float -fmodulo-sched`, copied from `MACHDEP`
in `$PSL1GHT/ppu_rules`, on the rule the Makefile already stated: get this from the SDK, not from the
hardware. A guess that happens to work is still a guess.

The output is a stripped PowerPC64 big-endian `EXEC` at entry `0x10000000`, wrapped by `fself` into a
SELF with an `SCE\0` header. That is the right shape for something a homebrew-enabled console loads, and
it is as far as "it builds" can take you.

**Step 4 is the one to take seriously**, and it is the reason that program exists rather than a
hello-world. It measures the PPE time base against a known sleep, because `rc_platform_ps3.c` carries
exactly one magic number and a wrong value there corrupts nothing loudly — it scales every timeout in
the core, so a handshake deadline quietly runs short and the console reads as flaky.

The measurement tests two independent suspects at once, `sysUsleep`'s units and the constant, and cannot
say which is wrong. **Look at a wall clock while it runs.** The program sleeps about five seconds in
total: roughly five seconds means the sleep is right and any discrepancy is the constant; instantly or
an hour and a half means the sleep is what is wrong. That settles it in one boot and needs no equipment.

`main.c` itself has been compiled and run on a host against a stand-in seam — clean at `/W4`, five
seconds wall time, the failure branch confirmed by feeding it a frequency wrong by a factor of a
thousand. So a failure at step 4 is evidence about `rc_platform_ps3.c`, not about the program around it.

## 4. The daily loop

```sh
make -C ports/ripcord-ps3/tests      # after any change under source/media/ or the CSPRNG seam — fast
make -C ports/common/tests           # after any change to the shared core
make -C ports/ripcord-ps3            # when you want a .self to try on hardware
```

**Decoder work does not need a PS3**, and that is the whole shape of this port's plan. Steps 1 to 3 of
the order of work were chosen so that the hard part — H.264 — could be built and checked on a host
before any console was involved, and 142 host checks is where that stands. The same will be true of the
decoder proper: the elementary streams the 3DS port already dumped are the input, and
`ffmpeg -i video.264 frame%03d.png` is the ground truth for what a correct decoder produces.

What genuinely needs hardware starts at step 6, SPU bring-up, and the honest summary of everything
before it is that a toolchain unblocks more of this port than a console does.

## 5. The plan from here

[`README.md`](README.md) holds the order of work and [`DECODE.md`](DECODE.md) the decoder decisions.

**Closed on hardware, 2026-09-11.** The time base is 79,800,986 Hz against 79,800,000 expected — 12 ppm,
so the documented figure was right, and `rc_tick_hz()` now asks lv2 rather than depending on that having
been true. `sysUsleep` is microseconds, confirmed by the same measurement. `rc_time_ms` is monotonic.
`sysGetRandomNumber` returns zero for success and accepts an unaligned destination — both of the CSPRNG's
`[X]`s, settled by a probe that deliberately asks for bytes at an odd address. And the writable-directory
question is answered: a packaged title can write to its own `USRDIR`, to `/dev_hdd0/tmp/` and to
`/dev_hdd0/` itself.

**Still open, and now genuinely about work rather than tooling:**

1. **The loader's default main-thread stack**, when `SYS_PROCESS_PARAM` is absent — never measured.
2. ~~**Sockets on hardware.**~~ **Answered on hardware.** `bind()` to port 0 was the specific thing worth
   testing rather than assuming, because the 3DS rejects it outright. **The PS3 accepts it** — the run
   that established that is the one where discovery found a PS5 from the console.
3. ~~**Step 6, SPU bring-up.**~~ **Done on hardware.** Dispatch ~65 µs, DMA ~10 GB/s single-buffered;
   `DECODE.md` §3 has what that does to the decoder's design. The second compiler works, an SPE image
   embeds into the PPU binary, and a job model exists.

## 6. Packaging for this console — read this before you debug anything

Five hardware runs were needed to get the bring-up program to start. **Four of them produced no output
of any kind** — installed, launched, exited, no crash report, no file anywhere. Both causes were in the
packaging, not the program, and both are the sort of thing that is obvious afterwards.

**`sprxlinker` is not optional.** `$(PSL1GHT)/ppu_rules`' own `%.self` rule runs `$(STRIP)`, then
`$(SPRX)`, then the self tool. Our Makefile was written from that rule and dropped the middle line,
because it looked like an optional post-processing pass. It is not: `sprxlinker` patches the PRX import
stubs in `.sceStub.text`, and until it has run, every call through a stub — `sysGetRandomNumber`, and
whatever `lv2-crt0` touches on the way to `main()` — is unresolved. The program dies before its first
instruction. Note that `templates/simple/Makefile` in the PSL1GHT tree omits it, which is a hand-rolled
standalone Makefile rather than the boilerplate path; do not take it as licence.

**`SYS_PROCESS_PARAM`'s stack size is a raw byte count.** Not the `SYS_PROCESS_SPAWN_STACK_SIZE_*`
enumeration, whatever the names suggest — those are `flags` for `sysProcessExitSpawn`. Passing
`SYS_PROCESS_SPAWN_STACK_SIZE_1M` sets the field to `0x70`, i.e. a 112-byte stack, and the program
crashes before `main()` exactly as if the macro were absent. Every PSL1GHT sample writes
`SYS_PROCESS_PARAM(1001, 0x100000)`.

**The single most useful habit: check the SDK's own samples.** Both bugs above were sitting in
`ppu_rules` and in six sample `main.c` files the whole time. An afternoon of `objdump` on our own binary
found a real and correct fact about newlib's devoptab layer that was entirely beside the point, because
the program was not running at all. Reading the reference implementation of the thing you are copying is
cheaper than deriving what it must have meant.

**Two SPU-specific traps, learned in step 6.** `sysSpuThreadInitialize` takes four `u64` arguments and
lv2 delivers three: `arg3` arrives as zero, the SPE DMAs to address 0, and the thread dies before its
next instruction. Pass a job block by effective address instead - README.md argues it in full, and
PSL1GHT's own samples never populate past `arg1`, which is why nothing in the SDK reveals the limit. And
`sysSpuImage` as PSL1GHT declares it does not match what lv2 writes on a 64-bit process, so `entryPoint`
and `segmentCount` read as garbage; an earlier revision of the bring-up program concluded from them that
the image was wrong, confidently and incorrectly.

**A build id, for the same reason.** Every build stamps a number into the XMB title, the beacon file and
the first line of both logs; `make` prints it. For all five runs above, the only evidence of which binary
had executed was the byte size of the installed EBOOT read off an FTP listing — a coincidence away from
being wrong, and a wrong answer there sends you debugging code that never ran. If the three do not agree
with what `make` printed, the console is running a stale install and nothing else you are looking at
means anything.

**What was NOT wrong, recorded so nobody re-investigates it:** newlib's `fopen`. It works, and both log
channels wrote on the successful run. `_open_r` does dispatch through a devoptab and does return
`-1`/`ENOSYS` when the entry is null — that reading of the disassembly was correct and irrelevant.

## 7. Getting a build onto the console

### `make run` / ps3load — **does not work on the console this was developed against**

> Tried over six cycles and abandoned. The `run` target is kept because it is correct and may well work
> elsewhere, but on this console it does not, and the reason is not in our program.
>
> **ps3load's own UI never draws.** Its `main()` calls `init_screen()` and `ioPadInit()` before it ever
> listens, and on this console that produces a black screen unresponsive to a controller — from the
> moment it is launched, with nothing sent to it yet. The network half works well enough to accept a
> transfer and stage the file correctly, and then the spawn does not produce a running program.
>
> The cost of learning that was high, and the reason is worth recording: the black screen was assumed to
> be *our* program failing to draw, so six rounds of increasingly careful instrumentation went into a
> binary that was never started. What settled it was asking what the screen showed **before** anything
> was sent. That question was available from the first run and was not asked for five of them.
>
> Three real bugs were fixed on the way and none of them was the problem: the target sent the ELF rather
> than the `.self`, the `.self` was built with `fself` where `ppu_rules` uses `make_self`, and `-mcpu=cell`
> was missing. All three were the same mistake — asserting something about the SDK with the SDK's own rule
> sitting there to be read.
>
> If you have a console where ps3load works, the target is `make run PS3LOAD=tcp:<ip>` and the rest of
> this section applies.

### The loop that does work — a package, plus the network log

`make pkg`, FTP it to `/dev_hdd0/packages/`, install from the XMB. One install per iteration, which is
the real cost, and it has worked every time.

What takes most of the sting out is that the bring-up program logs over **UDP** as well as to disk
(`source/net/rc_netlog.c`), so the output arrives live on the development machine instead of needing an
FTP fetch per question:

```sh
# on the development machine, before running anything on the console
nc -u -l -p 9000          # or: socat -u UDP-RECV:9000 -

# the destination is compiled in; override it if this machine is not 192.168.1.10
make -C ports/ripcord-ps3 pkg RC_NETLOG_HOST=192.168.1.10
```

### The fast loop — `make run`

```sh
make -C ports/ripcord-ps3 run PS3LOAD=tcp:<console-ip>
```

One command. No package, no install, no XMB round trip, and the program's stdout comes back over the
network. Use it for iteration and keep `make pkg` for a build you want to survive a reboot.

**Relaunch the listener before each run.** It ends in `sysProcessExitSpawn2`, which replaces the listener
process with the program it just received, so one launch serves one run. A `connection refused` almost
always means the previous run consumed it. Still much cheaper than the packaging route below; just not
zero-touch.

**Send the `.self`, not the ELF.** `make run` does. The listener writes whatever arrives to
`/dev_hdd0/tmp/ps3load.self` and launches that, so a raw ELF transfers perfectly and then does not start
— and `ps3load` reports success either way, because from its end the transfer worked. The build id on the
first log line is what catches it.

It needs a listener on the console, on **TCP 4299** — a port read out of the `ps3load` client binary's
`sin_port = 0xcb10` and independently confirmed by `#define PORT 4299` in PSL1GHT's own
`samples/network/ps3load/source/main.c`. The toolchain ships only the client half. Two ways to get the
other: multiMAN runs one while it is open, or build the sample, which takes about a minute:

```sh
cd <PSL1GHT source>/samples/network/ps3load && make pkg   # install ps3load.pkg, then launch it
```

**This should have been the first thing set up.** Step 6 took five install-and-run cycles to find one
wrong argument, and every one of them would have been answered in seconds against live output — the four
silent runs especially, where the whole difficulty was that the program had no way to say anything.
Packaging is the right way to ship a build. It is a poor way to ask a question.

### The durable route — FTP and a package

FTP to `/dev_hdd0/packages/`, then the XMB package manager. webMAN MOD's FTP server is enough and nothing
else is needed. This is what every run in section 6 used, and it is still what you want for a build that
has to persist, or when no listener is running.

Two practical notes from those runs. The console takes a new DHCP lease across a power cycle, so check
the address before concluding anything is wrong — it moved twice here. And webMAN's FTP and web server
come up a little after the XMB does, so a console that answers ping but refuses port 21 is usually still
settling rather than broken.
