# ripcord-3ds — setting up a Linux build box

The 3DS toolchain is happiest on Linux and the .NET side is cross-platform, so a Linux box can drive the
whole loop: generate vectors, verify the C on the host, and cross-compile the `.3dsx`. This is what that
takes, in order.

## 1. Getting the tree across

This is a direct copy of the working directory, not a clone, so the gitignored material comes along and
there is nothing to reassemble. Two things are still worth doing deliberately.

**Decide about the dirty room.** A wholesale copy brings `docs/protocol/captures/` — real captures, session
keys and account identifiers — onto another machine by default. That is the opposite of the usual risk, and
it is a decision rather than an oversight. None of this work needs it: the tests that read it
(`LiveControlVectorTests` and friends) self-skip when it is absent, and `ports/ripcord-3ds` never touches
it. Exclude it unless you have a specific reason:

```sh
rsync -av --exclude 'captures/' --exclude 'bin/' --exclude 'obj/' \
    /path/to/ripcord/ user@linuxbox:~/ripcord/
```

**Drop `bin/` and `obj/`.** They carry Windows-built artifacts and baked-in probing paths, and a stale
`obj/` is what lets a build appear to succeed while silently reusing generated files instead of running
codegen. If you have already copied them:

```sh
find ~/ripcord -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

<details>
<summary>If a future setup ever starts from <code>git clone</code> instead</summary>

A clone does **not** produce a buildable tree. `docs/` is gitignored in full, and
`Ripcord.Protocol.Halyard.Takion` compiles `docs/protocol/*.proto` at build time via Grpc.Tools. Without
them you get:

```
error CS0246: The type or namespace name 'Avstream' could not be found
error CS0246: The type or namespace name 'ControlMessage' could not be found
```

which names *types*, not missing files, several projects downstream of the cause. You would also be
missing the spec itself and `CLAUDE.md` / `ROADMAP.md`. Copy them out of band — and never `git add -f`
them through a branch, since those ignore rules are the publication-review gate.

</details>

## 2. Toolchain

Install instructions drift; check <https://devkitpro.org/wiki/Getting_Started> against what follows.

### devkitPro (Debian / Ubuntu)

```sh
wget https://apt.devkitpro.org/install-devkitpro-pacman
chmod +x install-devkitpro-pacman
sudo ./install-devkitpro-pacman

sudo dkp-pacman -Syu
sudo dkp-pacman -S 3ds-dev        # devkitARM, libctru, 3dsxtool, picasso, tex3ds
```

Then pick up the environment (or just log out and back in):

```sh
source /etc/profile.d/devkit-env.sh
echo "$DEVKITPRO $DEVKITARM"      # expect /opt/devkitpro /opt/devkitpro/devkitARM
```

`ports/ripcord-3ds/Makefile` hard-errors if either variable is unset, so a missed `source` is a clear
message rather than a confusing compiler failure.

Optional, and only when you want the faster on-device crypto backend later:

```sh
sudo dkp-pacman -S 3ds-mbedtls
```

### .NET SDK

Needed only to generate the vectors and run the managed test suites. There is no `global.json`, so any
10.0 SDK works.

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"   # add to your shell profile
dotnet --version                     # expect 10.0.x
```

The `NUGET_PACKAGES` / `NuGetPackageRoot` dance documented for the Windows box does **not** apply here —
that is an artifact of a redirected `USERPROFILE` in a particular sandbox, not of this repository.

### Host C toolchain

```sh
sudo apt install build-essential python3   # gcc, make, and the constants generator
```

## 3. Verify the setup

Run these in order. Each one is a real gate; do not skip ahead when one fails.

```sh
# 1. The .NET side builds and the vector emitter runs.
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
#    expect: wrote ports/ripcord-3ds/tests/vectors/control-crypto.kat
#            kdf=80 ctxkey=21 iv=13 mode=33 field=14  (ps4 tables present)

# 2. The C compiles and agrees with it.
make -C ports/ripcord-3ds/tests
#    expect: self-test: AES-128 matches FIPS-197 C.1
#            171 passed, 0 failed

# 3. The cross-compile produces a homebrew binary.
make -C ports/ripcord-3ds
#    expect: ripcord-3ds.3dsx

# 4. Optional sanity on the managed suites.
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
#    expect: all pass, with skips where the dirty room is absent
```

**Steps 2 and 3 are now a known-good configuration**, not just a starting point: both have been run against
a real devkitPro install. Two fixes were needed to get there, in case a from-scratch checkout hits the same
class of issue again — a naming collision between the SHA-256 context typedef and the one-shot hash
function in `source/crypto/rc_crypto.h`/`rc_sha256.c` (illegal in C; the function is now `rc_sha256_hash`),
and libctru's own headers needing `-isystem` rather than `-I` so this project's `-Werror -Wconversion
-Wsign-conversion` policy doesn't get applied to code it doesn't own.

## 4. The daily loop

```sh
make -C ports/ripcord-3ds/tests      # after any change under source/ — fast, no hardware
make -C ports/ripcord-3ds            # when you want a .3dsx to try on hardware
```

Regenerate vectors only when the .NET crypto changes:

```sh
dotnet run --project tools/Ripcord.ProtocolLab -- vectors
```

The `.kat` file is deterministic, so an unexpected diff there means the crypto changed — treat it as a
signal, not noise.

## 5. The plan from here

Wi-Fi has been measured and is **not** the blocker it was assumed to be: `ftpd` sustained ~10 Mbps,
dipping to 8 and peaking near 13. The console's bottom rung is 640×360 @ 60, which wants roughly 1.5–3
Mbps. That is comfortable headroom, so the transport work is worth doing.

**Phase 0 — first green build. Done.** Step 2 (host KAT runner, 171/171) and step 3 (ARM11 cross-compile)
both pass. This proves the control-plane crypto is correct in C, and everything else depends on it.

**Phase 1 — first boot.** Get `ripcord-3ds.3dsx` running on the handheld. `source/app/main.c` round-trips
the ciphers on real hardware and prints how long a field encryption costs on an ARM11. Record that number
— it is the input to every later decision about whether software AES can keep up with the A/V path.

**Phase 2 — confirm the link properly.** The `ftpd` result is TCP, upload direction, idle CPU. Before
committing to transport work, run the UDP receive test: 1426-byte payloads with sequence numbers and
timestamps, ramped 1→12 Mbps, measuring goodput, loss, and p99 inter-arrival — then repeat it with the CPU
busy. Two traps that will invalidate the results: call `osSetSpeedupEnable(true)` or you are measuring the
wrong clock speed entirely, and give `socInit` a 0x100000 buffer aligned to 0x1000 or you will measure your
own buffer and see the drops during keyframe bursts, where they look plausible.

**Phase 3 — sockets and discovery.** libctru's SOC service, then LAN discovery.

**Phase 4 — `/sess/ctrl`.** The first exchange that talks to a real console, and the first end-to-end use
of the crypto from Phase 0.

**Phase 5 — Takion.** Handshake, reliable delivery, reassembly.

**Phase 6 — media.** MVD H.264 decode → Y2R → PICA200, Opus audio, input mapping.

Pairing is not on this list: pair with desktop Ripcord and copy the record across. See the README.

## 6. Gotchas worth knowing before you hit them

- **`osSetSpeedupEnable(true)`** — without it a New 3DS runs at the old clock. Any performance number taken
  without it describes a machine you are not targeting.
- **`socInit` buffer size** — 0x100000, aligned to 0x1000. Undersizing it produces drops that look exactly
  like a Wi-Fi ceiling.
- **No per-packet `printf`** — console output alone will cap throughput well below the link. Counters only.
- **HEVC must be refused at negotiation** — the MVD decoder does H.264 only.
- **The constants are generated, never copied.** `tools/gen_constants.py` reads the one committed bundle at
  build time and writes into `build/`, which is gitignored. If you ever find yourself checking a generated
  constants file in, stop and read that script's header.
