# libripcord — the portable protocol core

The PS5 Remote Play protocol in portable C99, shared by every Ripcord port and by the planned macOS
client. No console SDK, no platform headers, no `#ifdef` naming a target.

It lived at `ports/common` until 2026-09-24, and older records use that name. It moved to the top level
when the macOS client (see [`docs/macos-plan.md`](../docs/macos-plan.md)) chose it as its protocol core,
because a first-class client depending on a folder called "ports" misdescribes both.

This is not a library anyone designed. It is what was left over when the [3DS
port](../ports/ripcord-3ds) — written as a single-platform tree — was audited for a second target: **71 of
its 88 source files referenced no operating system at all.** The entire coupling was three libctru
calls in three files, plus sockets. Extracting it was mostly `git mv`.

## What's here

```
crypto/     AES-128, SHA-256, HMAC, GCM/GMAC, cipher modes, the ECDH seam
halyard/    the control KDF, field IV derivation, field ciphers, registration (PIN and account), the account seed
session/    /sess/rgst (PIN and account), /sess/init -> /sess/ctrl, the binary control channel, launchSpec, pairing records;
            halyard_dgram*.c, the UDP 9303 control transport (prelude, chunks, association, pump) the
            account route and internet play share, and halyard_dgram_session.c, what the session layers
            need from it (the control session's byte pipe, the rgst exchange, a rendezvous leg);
            halyard_wan_candidates.c, which candidates an internet connect offers and which of the
            console's it talks to
discovery/  the SRCH probe and its response parser, and the LAN wake datagram
takion/     the SCTP-over-UDP transport: handshake, DATA/SACK, reassembly, senkusha, key negotiation
stream/     A/V framing, Cauchy Reed-Solomon FEC over GF(2^8), packet crypto, frame reassembly
input/      controller state -> input packet
net/        rc_tcp.c, a minimal TCP client; rc_udp.c; rc_stun*.c, the STUN reflexive-address client and
            the NAT classification (rc_stun_mapping.c)
util/       base64, hex, text/header parsing, logging, program-dir resolution
platform/   rc_platform.h - the seam, and the ONLY thing here that names an OS
tests/      the host-side known-answer suite (4,702 assertions on 2026-09-25), plus the host seam implementation
tools/      gen_constants.py, udp_link_test_sender.py, build-mbedtls.sh + its minimal config
```

Dependency direction matches the .NET side's: `halyard/` depends on `crypto/`, never the reverse, and
`crypto/` knows nothing about PlayStation. `discovery/` and `net/` answer network questions rather than
protocol ones and depend on neither.

## The seam

[`platform/rc_platform.h`](platform/rc_platform.h) is the complete list of what this core asks of an
OS: a monotonic millisecond clock, a sleep, a high-resolution tick, and a CSPRNG. Sockets are *not* in
it — they are called as plain BSD names, which was expected to need a ~12-call seam on Vita and turned
out not to: vitasdk ships POSIX socket headers whose names also link. The header records what that does
and does not settle.

The rule is: **nothing goes in that header that only one platform needs.** A seam earns its place by
having at least two real implementations. Anything a single port wants belongs in that port's tree,
reached through a callback the port installs — which is how the media path works, and why decode/audio/
present are absent from the seam despite being the largest platform surface any port has.

Three implementations have been written. Two are in this tree:

| | |
|---|---|
| `ports/ripcord-3ds/source/platform/rc_platform_3ds.c` | libctru — the hardware-verified one |
| `tests/rc_platform_host.c` | plain POSIX, for the host tests |
| *`rc_platform_vita.c`, on the Vita branch* | vitasdk — compiles and links; never run |

The third is where several of the findings recorded below actually came from — that sockets need no
seam, that `inet_aton` is a BSD extension rather than C99 — so the evidence for this header's shape is
real even though the file is not here yet. It arrives when that port does.

The host one is not decoration. A header with one caller and one implementation is indirection, not a
seam; the host build is what notices when a "portable" file quietly grows a dependency on a console.

## Building and testing

No console, no cross-compiler, just a C compiler:

```sh
# 1. Generate the known-answer vectors from the .NET implementation (from the repo root)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

# 2. Run them
make -C libripcord/tests

# 3. Compile EVERY portable file, including the ones no runner links
make -C libripcord/tests compile

# 4. Fuzz the parsers that face the network (see "Fuzzing" below)
make -C libripcord/tests fuzz-replay                 # any compiler, ASan+UBSan
make -C libripcord/tests fuzz-run FUZZ_CC=clang      # needs a clang with libFuzzer
```

`make compile` exists because the runners stop at the socket boundary, which used to leave
`net/rc_tcp.c`, `session/halyard_control_session.c`, `takion/takion_reliable_channel.c` and parts of
`util/` compiled by the cross-compiler and by nothing else — precisely the set where a platform
assumption hides. Adding it immediately found two: `inet_aton` is a BSD extension rather than C99 (and
vitasdk's documented helper is `sceNetInetPton` instead), and `clock_gettime` needs an explicit
`_POSIX_C_SOURCE` under strict `-std=c99`.

The ECDH backend needs no package installed. `rc_ecdh.c` delegates P-256/P-521 to Mbed TLS (see
`crypto/rc_ecdh.h` for why), and `tools/build-mbedtls.sh` cross-builds **only its ECP/MPI layer** — five
translation units — from a pinned, SHA-256-verified 2.28.8 release into a gitignored directory. Nothing
third-party is vendored into this tree, on the same reasoning that keeps the interop constants generated
rather than copied.

That is the default (`ECDH_BACKEND=local`). `ECDH_BACKEND=mbedtls` links a system `libmbedcrypto`
instead, and `ECDH_BACKEND=none` skips the ECDH cases and exits 0. Before the local build existed,
`ecdh_test` skipped on any machine without `libmbedtls-dev`, which quietly meant the curve agreement and
the derived stream keys went unchecked exactly where nobody would notice — so "any machine with a C
compiler and nothing else installed" is now true rather than aspirational.

**A build can supply its own backend instead.** Defining `RC_ECDH_EXTERNAL_BACKEND` compiles only the
backend-neutral half of `rc_ecdh.c`, and the build provides the five backend entry points itself. The macOS
client does this with CryptoKit (`src/Ripcord.Mac/RipcordKit/Crypto/CryptoKitECDH.swift`), and its
`libripcord-ecdh-kat` target runs this tree's `tests/ecdh_test.c`, unmodified, against it. Mbed TLS and
CryptoKit are therefore two real backends behind one seam, and both are checked against the same vectors.

The version is pinned to 2.28.8 because that is what devkitPro packages as `3ds-mbedtls`: one version,
one `rc_ecdh.c`, two ports.

## Fuzzing

This core parses bytes from the LAN and from a console, much of it before anything is authenticated:
discovery replies from whatever answers a broadcast, the Takion handshake, SACK, DATA and reassembly,
and the stream headers. It is C, so a length it trusts is a memory-safety bug rather than an exception.
`tests/fuzz/` has one harness per surface:

| Harness | Reaches |
|---|---|
| `fuzz_discovery.c` | The SRCH reply parser |
| `fuzz_takion.c` | Message framing, every handshake chunk, SACK, DATA, and a reassembler that lives across datagrams |
| `fuzz_control.c` | The `/sess/ctrl` byte stream and every Takion control-message parser |
| `fuzz_stream.c` | Stream headers, frame assembly and FEC recovery, with the passthrough crypto seam so it also reaches what an authenticated console could drive |
| `fuzz_stun.c` | The STUN Binding Response parser - the one surface that faces the open internet rather than the LAN |
| `fuzz_account.c` | Account pairing: the console's `customData1` seed, and the `/sess/rgst` reply - completeness (`halyard_dgram_http_complete`, the one check), split, decrypt, pairing-record parse |
| `fuzz_dgram.c` | The 9303 prelude and chunk parsers, HTTP completeness, and an association fed datagrams in every phase - what the internet path's control plane and A/V hole-punch read before anything is authenticated |
| `fuzz_wan.c` | Candidate addresses as the cloud tier hands them over, unterminated fields included, and the choice between them |
| `fuzz_rendezvous.c` | The control session over the 9303 byte pipe: frames arriving as datagram payload in any split, a raw datagram or a Close between them, the parser, resync and field decrypt behind it |

Stateful harnesses read their input as a sequence of length-prefixed records, one per datagram
(`fuzz/fuzz_input.h`), so a second packet can find the state the first one left.

There are two modes because Apple's clang ships the sanitizers but not libFuzzer. `fuzz-replay` links each
harness against `fuzz/replay_main.c`, a deterministic driver that is not a fuzzer. It proves every harness
runs cleanly under the sanitizers on every host and replays a crash file found elsewhere. `fuzz-run` is the
coverage-guided run, and CI does it on Linux for 60 seconds per harness.

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
