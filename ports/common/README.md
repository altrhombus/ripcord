# ports/common — the portable protocol core

The PS5 Remote Play protocol in portable C99, shared by every Ripcord port. No console SDK, no
platform headers, no `#ifdef` naming a target.

This is not a library anyone designed. It is what was left over when the [3DS
port](../ripcord-3ds) — written as a single-platform tree — was audited for a second target: **71 of
its 88 source files referenced no operating system at all.** The entire coupling was three libctru
calls in three files, plus sockets. Extracting it was mostly `git mv`.

## What's here

```
crypto/     AES-128, SHA-256, HMAC, GCM/GMAC, cipher modes, the ECDH seam
halyard/    the control KDF, field IV derivation, field ciphers
session/    /sess/init -> /sess/ctrl, the binary control channel, launchSpec, pairing records
discovery/  the SRCH probe and its response parser
takion/     the SCTP-over-UDP transport: handshake, DATA/SACK, reassembly, senkusha, key negotiation
stream/     A/V framing, Cauchy Reed-Solomon FEC over GF(2^8), packet crypto, frame reassembly
input/      controller state -> input packet
net/        rc_tcp.c, a minimal TCP client over BSD sockets
util/       base64, hex, text/header parsing, logging, program-dir resolution
platform/   rc_platform.h - the seam, and the ONLY thing here that names an OS
tests/      the host-side known-answer suite (3,243 assertions), plus the host seam implementation
tools/      gen_constants.py, udp_link_test_sender.py
```

Dependency direction matches the .NET side's: `halyard/` depends on `crypto/`, never the reverse, and
`crypto/` knows nothing about PlayStation. `discovery/` and `net/` answer network questions rather than
protocol ones and depend on neither.

## The seam

[`platform/rc_platform.h`](platform/rc_platform.h) is the complete list of what this core asks of an
OS: a monotonic millisecond clock, a sleep, a high-resolution tick, and a CSPRNG. Sockets are called
directly as BSD names today, which is an open question for non-3DS targets — the header says so at
length.

The rule is: **nothing goes in that header that only one platform needs.** A seam earns its place by
having at least two real implementations. Anything a single port wants belongs in that port's tree,
reached through a callback the port installs — which is how the media path works, and why decode/audio/
present are absent from the seam despite being the largest platform surface any port has.

Three implementations exist:

| | |
|---|---|
| `ports/ripcord-3ds/source/platform/rc_platform_3ds.c` | libctru — the verified one |
| `ports/ripcord-vita/source/platform/rc_platform_vita.c` | vitasdk — never compiled |
| `tests/rc_platform_host.c` | plain POSIX, for the host tests |

The host one is not decoration. A header with one caller and one implementation is indirection, not a
seam; the host build is what notices when a "portable" file quietly grows a dependency on a console.

## Building and testing

No console, no cross-compiler, just a C compiler:

```sh
# 1. Generate the known-answer vectors from the .NET implementation (from the repo root)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

# 2. Run them
make -C ports/common/tests

# 3. Compile EVERY portable file, including the ones no runner links
make -C ports/common/tests compile
```

`make compile` exists because the runners stop at the socket boundary, which used to leave
`net/rc_tcp.c`, `session/halyard_control_session.c`, `takion/takion_reliable_channel.c` and parts of
`util/` compiled by the cross-compiler and by nothing else — precisely the set where a platform
assumption hides. Adding it immediately found two: `inet_aton` is a BSD extension rather than C99 (and
vitasdk's documented helper is `sceNetInetPton` instead), and `clock_gettime` needs an explicit
`_POSIX_C_SOURCE` under strict `-std=c99`.

`ECDH_BACKEND=none` builds the suite without mbedtls; `ecdh_test` then reports a skip and exits 0, which
keeps "any machine with a C compiler" true.

## The interop constants are generated, never copied

`src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` is committed under a specific, narrow
argument in the repository `NOTICE`, made once about one file. This tree keeps **no copy**:
`tools/gen_constants.py` reads that one file at build time and generates a C translation unit into a
gitignored `build/`. A second checked-in copy would quietly turn one bounded exception into two, and the
second would carry no argument at all. Everything under `tests/vectors/` is generated and gitignored on
the same reasoning.

## Not part of `Ripcord.slnx`

`dotnet build` cannot build this and never will. Sharing a repository with Ripcord buys shared specs,
shared constants and shared test vectors; it does not mean sharing a build system with cross-compilers
for other CPUs and other operating systems.
