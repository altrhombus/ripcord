# ripcord-ps3

A PS5 Remote Play client for PlayStation 3 homebrew, in C. It shows a PS5's picture on the PS3's television,
plays its audio and sends a DualShock 3's input back. Installing its `.pkg` needs a PS3 that runs homebrew; a
console on Sony's stock firmware won't install it.

## Status

Works, on hardware:

- **Streaming** up to 1280x720 at 60 fps, on the LAN. Without a saved record the port starts at 720p60,
  20,000 kbps, RGB from the decoder, RSX scaling, bilinear.
- **Video decode** on the console's own H.264 decoder, with openh264 on the PPE as the fallback.
- **Audio**: 48 kHz stereo Opus, decoded by libopus on the PPE.
- **Input** from a DualShock 3, including L3 and R3.
- **Pairing from the PS3**, using the system keyboard. The PSN account id is pre-filled when the PS3 is signed
  in to PSN.
- **Discovery and wake** of a console in standby, on the LAN.
- **A shell**: a home screen listing paired and discovered consoles (up to eight paired), and a settings screen
  for resolution, frame rate, bitrate and scaling.
- **Output modes**: 1080p, 1080i, 720p, and 480p at 16:9 and 4:3.

Does not work, or is not done:

- **1080p streams.** The console's 1080p stream needs a larger decoded picture buffer than the PS3's decoder
  offers (it stops at level 4.2). 1280x720 is the ceiling, and the settings screen does not offer 1920x1080.
- **PSN sign-in**, so no wake over the internet and no WAN sessions. The port works on the LAN only.
- **50 Hz displays** and **interlaced SD** (480i, 576i) are untested.
- **Wi-Fi** is 802.11 b/g on every PS3 model. Prefer wired.
- The DualShock 3 has no touchpad and no adaptive triggers. Motion is `[X]`.
- Incoming GMAC tags on control packets are not verified `[X]`. A/V packets are authenticated.
- Log rotation has not yet fired on hardware `[X]`.
- The shell redesign in [`SHELL-DESIGN.md`](SHELL-DESIGN.md) is not built.

## Installing

The `ps3-v1.1` package is built, and is published once it has run on a console. Until then, build the
`.pkg` from this directory (see Building). Copy `ripcord-ps3.pkg` to `/dev_hdd0/packages/` over FTP (webMAN MOD's FTP server is enough), then
install it from the XMB package manager. It installs to `/dev_hdd0/game/RIPC00003/`.

Installing replaces `USRDIR`, so back up `pairing.txt` first if the console is paired, or it has to be paired
again. An install that fails with `80028f21` usually means the installed title is in an inconsistent state:
delete the title from the XMB and install again.

## Building

[`SETUP.md`](SETUP.md) covers the toolchain: the ps3dev prebuilt archive on Linux, with `$PS3DEV` and
`$PSL1GHT` exported. The Makefile stops with an error if either is unset.

The third-party libraries are fetched and hash-verified, not vendored. Build them once, from this directory:

```sh
sh tools/build-openh264.sh
sh tools/build-opus.sh
PREFIX=third-party/mbedtls CROSS=powerpc64-ps3-elf- sh ../../libripcord/tools/build-mbedtls.sh
```

Then, from the repository root:

| Command | Does |
|---|---|
| `make -C ports/ripcord-ps3` | Builds `ripcord-ps3.self` |
| `make -C ports/ripcord-ps3 pkg` | Builds `ripcord-ps3.pkg`, with its third-party notices inside |
| `make -C ports/ripcord-ps3 pkg RELEASE_TAG=ps3-v<major>.<minor>` | The same, versioned by a release tag |
| `make -C ports/ripcord-ps3 notices` | Builds only the notices file |
| `make -C ports/ripcord-ps3 test` | Runs the host-side tests: this port's and `libripcord`'s. No console needed |
| `make -C ports/ripcord-ps3 run PS3LOAD=tcp:<console-ip>` | Sends the `.self` to a ps3load listener. See `SETUP.md` §7 before relying on it |
| `make -C ports/ripcord-ps3 clean` | Removes build output. `distclean` also removes the fetched openh264 tree |

## How it is put together

| Part | Where | What it does |
|---|---|---|
| Shared core | `libripcord/` | Protocol, transport, crypto, discovery, control session, input encoding. Shared with the other ports |
| Platform seam | `source/platform/` | The four functions `libripcord/platform/rc_platform.h` asks for: `rc_time_ms`, `rc_sleep_ms`, `rc_tick`/`rc_tick_hz`, `rc_random_bytes`. Sockets need no seam |
| Video decode | `source/media/` | The console's hardware H.264 decoder (cellVdec, through PSL1GHT's `libvdec.a`), opened at level 4.2. openh264 is the fallback. The choice is made at open and logged |
| Scaling and colour | `source/video/`, `source/spu/`, `spu/` | By default the decoder outputs RGB into RSX memory and the RSX scales it. The other path converts YUV to RGB and scales on the SPEs |
| Audio | `source/media/` | libopus on the PPE, played through PSL1GHT's audio port. No audio thread |
| Shell and pairing | `source/ui/`, `source/connect/` | Home and settings screens, pairing, the record file |

Decode, audio and present are not part of the platform seam. Each port installs them as callbacks.

## Further reading

- [`BRINGUP.md`](BRINGUP.md): the dated record of bringing the port up, with the measurements.
- [`SETUP.md`](SETUP.md): the build box, the toolchain, packaging, and getting a build onto a console.
- [`DECODE.md`](DECODE.md): the decode path, and the later work: audio, input, scaling, output modes, pairing.
- [`SHELL-DESIGN.md`](SHELL-DESIGN.md): a design plan for the shell. Not built yet.

## Licensing

Ripcord is Apache-2.0 and its ports hold a permissive-only line: mbedtls (Apache-2.0), Opus (BSD-3).

- **FFmpeg does not fit it.** `libavcodec` is LGPL-2.1-or-later, which the FSF treats as incompatible with
  Apache-2.0, and the relink provision is awkward on a statically linked homebrew target. `DECODE.md` §1 sets
  out the options.
- **The decoder base is openh264.** Its `LICENSE` was read directly: two clauses, no endorsement clause, so
  BSD-2-Clause, which is Apache-2.0-compatible without qualification. CABAC (`DECODE.md` §2) ruled out writing
  the entropy decoder ourselves.
- **ffmpeg as a development tool is fine**, and this project uses it that way: `mvdreplay`'s own comment cites
  `ffmpeg -i video.264` for ground truth. The constraint is only on what gets linked into a shipped client.

### Shipped notices: the reasoning behind `NOTICE`

`NOTICE` records the inventory, the two licence elections and what ships in the package. The reasoning is here
so that file stays short enough to read in full.

- **Why the elections are written down.** Mbed TLS and FreeType are each dual-licensed, and the arms are not
  interchangeable: Ripcord is distributed under Apache-2.0, and GPLv2 is not compatible with it. A build that
  drifted into the GPL arm unchosen would be a licensing problem found late, so the choice is stated. openh264
  and Opus are single-licence, checked against their own `LICENSE` and `COPYING` files.
- **The GPL text that travels anyway.** Upstream's Mbed TLS licence file carries both arms in full, so roughly
  250 lines of the GNU General Public License ride inside every package as upstream's unmodified text. The
  generated notices say so at the head of that section, because a GPL text under an Apache heading with nothing
  joining the two invites the wrong conclusion.
- **Why PSL1GHT's and FreeType's texts come from the generator, not the toolchain.** The ps3dev prebuilt
  archive ships no licence file for any component this port links (the whole unpacked toolchain has exactly
  one, for a library this port does not link). The generator originally looked under `$PS3DEV` and emitted
  nothing when it found none, which was every build. MIT's notice requirement and the FTL's disclaimer are
  unconditional, so what satisfies them cannot depend on an optional file. zlib asks nothing of a binary
  distribution and is included as a courtesy when a licence file is available, which at present it is not.
- **The FTL asks for two things, and a binary triggers both.** Section 2 asks for a credit. Section 1
  separately requires, of binary redistribution, a disclaimer that the software is based in part on the work of
  the FreeType Team, "in the distribution documentation". Until 2026-09-18 only the credit was emitted, and it
  carried no year. Both lines now ship inside every package, because the distribution documentation for someone
  who downloads a `.pkg` is what is inside the `.pkg`.
- **Why the `.self` is not published.** A `.self` is a bare executable with no container: it links every
  component statically and carries no notice text, so publishing one means publishing the notices beside it and
  depending on the downloader to take both files. An earlier revision shipped a `.self` in CI artifacts with no
  notices file, on the reasoning that the notices travel inside the package; that is true of the package, not
  of the `.self` beside it. It remains a local build product, what `ps3load` loads, and anyone who wants one
  builds it.
- **Whose obligations these are.** This project publishes `ripcord-ps3` packages itself, on every CI run and on
  tagged releases, so the notice obligations are its own. They apply equally to anyone who builds and publishes
  their own package.
- **Why generated rather than committed.** The same reason as the interop constants: a checked-in copy of
  somebody else's licence is a second copy that drifts, and the one that matters is the licence of the version
  actually linked. The generator exits non-zero if a licence that requires reproduction is missing, and the
  package does not build without it.
