# ripcord-3ds

A PS5 Remote Play client for modded Nintendo 3DS hardware, in C. It runs from the Homebrew Launcher as
`.3dsx` programs. **New 3DS only:** the original 3DS has no video decode hardware and is not a target.

## Status

`ripcord-3ds-connect.3dsx` streams from a real PS5. It runs the whole connect flow: discovery,
`/sess/init` → `/sess/ctrl`, the binary control channel, senkusha, the Takion handshake on UDP 9296, and
the `SESSION_REQUEST`/`SESSION_REPLY` ECDH exchange. It then derives the per-direction keys, seals its own
control traffic with GMAC and acks `STREAM_INFO`.

| Area | State |
|---|---|
| Video | H.264 decoded by the New 3DS's MVD block onto the top screen. H.264 only; HEVC must be refused at negotiation. The stream is requested at 960x540. |
| Audio | Opus decodes to NDSP. |
| Input | The pad sends state snapshots and button history back up the stream socket. |
| Senkusha | Measures RTT and MTU, so the launch spec declares measured figures. Measured: RTT 2 ms from 10/10 echoes, MTU 1454 in both directions. |
| Pairing | Not done on the device. The connect flow reads a `pairing.txt` from the SD card; see below. |

The last measured window is from the 2026-08-13 build at 640x360, over 60 seconds: 1,782 pictures, 0
process errors, 0 render errors, 29.7 fps presented, 2 units lost in 5,397. It has not been re-measured at
960x540.

Not done:

- On-device PIN pairing.
- Pairing-record import from a desktop Ripcord install. The `pairing.txt` the programs read is a
  provisional format; its header comment describes it.
- The standalone Takion probe (`ripcord-3ds-takion.3dsx`) has not been run against a peer we control. The
  transport itself is confirmed through the connect flow.

Limits set by the hardware:

- The top screen has 240 rows, so most of the source rows are discarded on the way down.
- The C-stick is a poor right stick, ZL/ZR are digital so there are no analog triggers, and there is no
  L3/R3. The touchscreen maps to the DualSense touchpad and the gyro to the gyro.

## Why this exists

A PS5 streaming to a handheld you already own is useful in itself. The main reason is that this port is a
completeness test for [`docs/protocol/`](../../docs/protocol): a second client, in a different language,
on a different OS and CPU, shows which parts of the spec are enough to build from. Every place the port
had to guess is a defect in the spec, which is why the port lives in this repository.

## Pairing happens on a PC, not here

Registration needs PSN OAuth and TLS, which is a lot of weight for a handheld. Pair with desktop Ripcord,
then copy the pairing record across. `PairedConsole` is a plain credential holder rather than a Halyard
type, so the record carries over easily. The registration constants never need to exist in this binary,
and `gen_constants.py` does not emit them for this port.

## Layout

The protocol core is in [`libripcord`](../../libripcord): crypto, the Halyard control KDF, session,
discovery, Takion, stream framing/FEC, input, and the portable half of `util/`. It is shared with every
other Ripcord port and reached through the seam in
[`libripcord/platform/rc_platform.h`](../../libripcord/platform/rc_platform.h). What is here is the 3DS
side.

```
source/platform/    the rc_platform.h seam, libctru side (clock, sleep, tick)
source/net/         SOC service lifecycle (rc_soc.c)
source/media/       MVD hardware H.264 decode (rc_mvd.c) + NDSP audio (rc_audio.c)
source/util/        rc_stack.c (the main-thread stack size), rc_random.c (CSPRNG), rc_profile.c
source/app/         on-device crypto smoke test (ripcord-3ds.3dsx)
source/linktest/    UDP link test (ripcord-3ds-linktest.3dsx)
source/discovery/   LAN discovery app (ripcord-3ds-discovery.3dsx)
source/session/     /sess app (ripcord-3ds-session.3dsx)
source/takion/      transport probe (ripcord-3ds-takion.3dsx)
source/connect/     the connect flow, the only program that reaches the stream plane
                    (ripcord-3ds-connect.3dsx)
source/mvdreplay/   decoder replay harness (ripcord-3ds-mvdreplay.3dsx)
```

`source/util/rc_stack.c` is 3DS code despite including no libctru header: it overrides libctru's weak
`__stacksize__` symbol.

## Building

### Host tests

These need no 3DS and no devkitPro, only a C compiler. They verify the crypto. The tests are in
[`libripcord/tests`](../../libripcord/tests) and cover every port.

```sh
# 1. Generate the vectors from the .NET implementation (once, from the repo root)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

# 2. Build and run the C against them
make -C libripcord/tests

# 3. Compile every portable file, including the ones no runner links
make -C libripcord/tests compile
```

`make -C ports/ripcord-3ds test` forwards to the same place.

### The 3DS build

Requires [devkitPro](https://devkitpro.org/wiki/Getting_Started) with devkitARM and libctru. On Windows,
run it from the devkitPro MSYS2 shell, which is where `make` and the ARM toolchain live. The connect flow
also links mbedtls for ECDH (`dkp-pacman -S 3ds-mbedtls`) and Opus from the devkitPro portlibs.
`ECDH_BACKEND=none` builds without mbedtls; the key agreement then fails cleanly.

```sh
make -C ports/ripcord-3ds
```

This produces seven Homebrew Launcher binaries. `make -C ports/ripcord-3ds <target>` builds one.

| Binary | Target | What it does |
|---|---|---|
| `ripcord-3ds.3dsx` | | Round-trips the ciphers on the device and times a control-field encryption. |
| `ripcord-3ds-linktest.3dsx` | `linktest` | UDP link test against `tools/udp_link_test_sender.py`; see [`SETUP.md`](SETUP.md). |
| `ripcord-3ds-discovery.3dsx` | `discovery` | Broadcasts the SRCH probe for both console families and lists each console that answers within four seconds. |
| `ripcord-3ds-session.3dsx` | `session` | Arms the control listener, runs `/sess/init` → `/sess/ctrl` from `pairing.txt`, and answers heartbeats on the control channel. |
| `ripcord-3ds-takion.3dsx` | `takion` | Drives the SCTP handshake against a `takion.txt`-configured host:port and exchanges a synthetic message. |
| `ripcord-3ds-connect.3dsx` | `connect` | The connect flow through to video, audio and input. |
| `ripcord-3ds-mvdreplay.3dsx` | `mvdreplay` | Feeds a captured elementary stream to MVD with no console and no network. |

Other targets: `constants` regenerates the interop constants only, `crosscheck` compiles the modules that
have no `.3dsx` of their own for ARM11, and `clean` removes the build output.

Each program writes everything it prints on the top screen to a `.log` file next to its `.3dsx` on the SD
card.

The build enforces `-Wframe-larger-than=8192` under `-Werror`. libctru's default main-thread stack for a
`.3dsx` is 32 KB, so large state belongs in `.bss` or on the heap.

### Not part of `Ripcord.slnx`, deliberately

`dotnet build` cannot build this. Sharing a repository with Ripcord gives shared specs, shared constants
and shared test vectors. It does not mean sharing a build system with a cross-compiler targeting a
different CPU and a different OS.

## The interop constants are generated, never copied

This port keeps no copy of `src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json`, the dotnet
client's bundled constants file. `libripcord/tools/gen_constants.py` reads that file at build time and
generates a C translation unit into `build/`, which is gitignored.

The vector files are handled the same way. Everything under `libripcord/tests/vectors/` is generated,
gitignored, and regenerated with the `vectors` command above.

## Further reading

- [`BRINGUP.md`](BRINGUP.md): the dated record of bringing the port up and what was measured on hardware.
- [`HARDWARE-PROBES.md`](HARDWARE-PROBES.md): the run sheet and triage for the on-device probes.
