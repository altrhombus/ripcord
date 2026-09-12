# ripcord-ps3 — setting up a Linux build box

Same answer as the 3DS, for stronger reasons. [`ports/ripcord-3ds/SETUP.md`](../ripcord-3ds/SETUP.md) is
the doc this one is modelled on and most of it applies unchanged; what follows is the PS3-specific half.

**Nothing in this document has been run.** The 3DS setup doc can say "known-good configuration" because
both its gates have been executed against a real devkitPro install. This one cannot say that about
anything past section 3 step 2. Every version number, package name and command below that touches
PSL1GHT is `[X]` — written from documentation, never executed — and the *first* person to run it should
expect to correct it rather than to follow it. Fix this file as you go; that is what it is for.

## Why Linux, and why more so here

The 3DS could plausibly be built from devkitPro's MSYS2 shell on Windows. This port has less choice:

- **The toolchain is built, not installed.** devkitPro ships a pacman repository; ps3dev is a source
  build that compiles binutils and GCC — and does it **twice**, because the PPE and the SPUs are
  different architectures with different compilers. That is a Linux-native workflow with a real time and
  disk cost `[X]`.
- **Two cross-compilers, and the second one is the entire port.** Steps 6 and 7 of
  [`README.md`](README.md)'s order of work are SPU code: `spu-gcc`, embedding SPU images in the PPU
  binary, and a job model. Strictly more toolchain surface than either port before it.
- **Everything here already assumes a POSIX shell and GNU make.** `ports/common/tools/build-mbedtls.sh`
  is `#!/bin/sh`, and every Makefile in `ports/` is GNU make with a gcc flag set (`-Wconversion`,
  `-Wframe-larger-than`, `-ffunction-sections`). On a Windows box without them you end up hand-rolling
  equivalent MSVC command lines, which does produce answers but is not a workflow.
- **The numbers have to be comparable.** The bring-up program exists to produce a figure that can sit
  beside the 3DS's. Same box, same habits.

## 1. Getting the tree across

Everything in the 3DS doc's section 1 applies — decide about the dirty room, drop `bin/` and `obj/`,
and note that a bare `git clone` does not produce a buildable tree. Two additions for WSL.

**Put the tree on the Linux filesystem, not `/mnt/c`.** `~/ripcord`, not `/mnt/c/Users/.../ripcord`.
Building two cross-compilers and then the core is I/O-heavy and the 9p boundary is slow, but the
correctness reason matters more than the speed one — see the next paragraph.

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

A `/mnt/c` working copy is the Windows checkout, so it reintroduces this whatever `.gitattributes` says.
That is the correctness reason to stay on ext4.

## 2. Toolchain

Install instructions drift, and these have never been run. Check
<https://github.com/ps3dev/ps3toolchain> and <https://github.com/ps3dev/PSL1GHT> against everything
here `[X]`.

### ps3dev and PSL1GHT

The expected shape `[X]`: build the toolchain from the `ps3toolchain` scripts, which fetch and compile
binutils and GCC for `powerpc64-ps3-elf` (the PPE) and for the SPUs, then build PSL1GHT against it.
Expect build dependencies of the usual autotools kind, several gigabytes of disk, and a long first run.

The Makefile hard-errors if either variable is unset, so a missed export is a clear message rather than
a confusing compiler failure — the same property the 3DS Makefile has:

```sh
export PS3DEV=/usr/local/ps3dev
export PSL1GHT=$PS3DEV/psl1ght     # add both to your shell profile
echo "$PS3DEV $PSL1GHT"
```

**Check the compiler flags against PSL1GHT's own rules before trusting the build.**
`ports/ripcord-ps3/Makefile` sets `-mcpu=cell` and marks it `[X]`, and the reason is a lesson both
earlier ports paid for in opposite directions: the 3DS needs its architecture flags spelled out because
libctru is built with them, while the Vita needs none because a faithful-looking description of the
hardware (`-mfloat-abi=softfp`) is a *different calling convention* and failed the link on every
translation unit. Read `$PSL1GHT/ppu_rules` and match whatever it compiles its own libraries with. That
is the only thing this has to agree with.

### .NET SDK, host C toolchain, ECDH backend

Identical to the 3DS doc's section 2 — the same SDK, the same `build-essential python3`, and the same
choice of ECDH backend. Nothing in this port changes any of it, and the PS3 has no equivalent of
devkitPro's `3ds-mbedtls` package, so the local build (`ECDH_BACKEND=local`, the default in
`ports/common/tests/Makefile`) is the path of least resistance here.

None of it is needed for section 3 steps 1 and 2 below.

## 3. Verify the setup

In order. Each is a real gate; do not skip ahead when one fails.

```sh
# 1. The H.264 front end - steps 2 and 3 of the order of work. No console, no cross-compiler,
#    no .NET. This is the one gate that is known to pass.
make -C ports/ripcord-ps3/tests
#    expect: 142 passed, 0 failed
#    and, printed along the way:
#            SPS: profile=77 level=31 640x368 ...
#            stream: 110 bytes -> 3 access units, 3 pictures, 4 slices
#            sizeof(rc_h264_au) = 19320 bytes

# 2. The shared core's known-answer suite, which is what actually verifies the crypto and transport.
#    Emit the vectors first - the C runners read .kat files, they do not generate them.
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#    expect: wrote ports/common/tests/vectors/control-crypto.kat  (and three more beside it)
make -C ports/common/tests
#    expect: ~3,244 assertions, 0 failed - ports/ripcord-3ds/SETUP.md has the per-runner breakdown
#    if ecdh_test reports a skip, the ECDH backend is missing; that is a configuration, not a failure

# 3. [X] The cross-compile. NEVER RUN - this is the first genuinely unverified step.
make -C ports/ripcord-ps3
#    expect: ripcord-ps3.self
#    Treat the first failure here as information about this Makefile, not about your install.

# 4. [X] The bring-up program, on hardware. Copy ripcord-ps3.self across, run it, and read
#    ps3-bringup.log from the fallback directory the Makefile names.
#    expect: "all checks passed", and a measured time base within 10% of 79,800,000 Hz
```

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
make -C ports/ripcord-ps3/tests      # after any change under source/media/ — fast, no hardware
make -C ports/common/tests           # after any change to the shared core
make -C ports/ripcord-ps3            # [X] when you want a .self to try on hardware
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
The short version of what a working toolchain unblocks, in order:

1. **Step 4 completes** — the seam compiles, the `.self` builds, the time base is confirmed or corrected.
   Everything timing-dependent in this port rests on that number.
2. **`rc_random_bytes` gets an implementation.** It is deliberately absent; the port will not link once
   anything asks for key material, which is correct until the call is confirmed against real headers.
   Confirming it is a toolchain job, and it is the last thing standing between this port and a session.
3. **The main thread's stack** — open `[X]`, see README.md. The deepest measured path in shared code is
   ~12.4 KB across five frames of audio loss concealment, and it only runs when a packet is *lost*.
   Settle it before the connect flow runs, not after a dump.
4. **Step 6, SPU bring-up** — one SPE running a trivial DMA job, measured. That is where this port stops
   resembling the other two.
