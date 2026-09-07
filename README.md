# Ripcord

An independent, clean-room **PlayStation Remote Play client for Windows**. Ripcord connects directly to a
PS5 or PS4 over your LAN, decodes the H.264/HEVC video stream on the GPU, plays audio through WASAPI, and
sends controller input back over the reverse-engineered wire protocol.

It is a from-scratch implementation. There is no vendor code in this repository, and the protocol
specification in [`docs/protocol/`](docs/protocol/) was derived independently — see
[Provenance](#provenance-and-legal-position) below, which is not boilerplate for this project.

> **Not affiliated with Sony Interactive Entertainment.** "PlayStation", "PS5", "PS4" and "Remote Play" are
> their trademarks, used here only to describe compatibility. See [`NOTICE`](NOTICE).

---

## Status

Phase 1 — LAN remote play — works end to end against real hardware, and as of 2026-09-05 so does play
**over the internet**: client and console on different networks, both behind NAT, no port forwarding,
direct peer to peer. A clone can pair with a console and stream from it; the protocol's interoperability
constants are included (see [Interoperability constants](#interoperability-constants) below). What is
missing is breadth, not basic function: no HDR output, no DualSense haptics or gyro.

### What works

Verified end-to-end against real hardware on a LAN, on **PS5 and PS4** alike:

- **Pairing from scratch** — PIN registration against the console, no cloud round-trip. Both families;
  PS4 registration uses the same table mechanism as PS5 with four differing constants.
- **Discovery** — LAN broadcast search and mDNS (UDP 9302 for PS5, 987 for PS4).
- **Connect, video, audio, and controller input**, live. The recorded figures are 1080p60 at 0.4% packet
  loss and 23.2 Mbps, with a 2.1 ms handshake, 6.5 ms RTT and 18 ms from demux to present.
- **Waking a sleeping console** over the LAN, and signing in to a locked console with its 4-digit user
  passcode — both derived from the project's own captures and confirmed against real hardware.
- **GPU video decode** via Media Foundation / DXVA with `MF_LOW_LATENCY`, decoder chosen by codec, HEVC and
  10-bit P010 supported; D3D12 present path.
- **Audio** through WASAPI with bounded latency.
- **Input** from Xbox-style pads (GameInput), DualSense over raw HID (USB and Bluetooth report formats,
  including PS button and touchpad click), and the keyboard, with configurable bindings. Multiple engines run
  simultaneously and merge, so swapping pads mid-session works.
- **PSN account sign-in** — reads the account's own console list, and can ask PSN to wake a console that the
  local broadcast cannot reach. Entirely optional: LAN play against an already-paired console needs none of it.
- **Play over the internet**, verified 2026-09-05 — client on a phone hotspot, console on a different
  network, both behind NAT, no port forwarding, direct peer to peer. Reaching a distant console needs
  classic STUN (RFC 3489 Binding Requests) to learn each leg's reflexive address; the control association
  and the A/V connection are separate mappings and need it separately. Throughput was indistinguishable from
  the same-LAN figures. Driven from the harness, not yet through the app's own UI.
- **962 unit tests** across two suites, pure managed and cross-platform — they need no console, no GPU and no
  Windows-only hardware. A clean checkout without the authors' captures sees more skips, by design.

There is also a **second client**: [`ports/ripcord-3ds`](ports/ripcord-3ds), a from-scratch C implementation
for modded New 3DS hardware that streams real video from a real PS5. It exists as a completeness test for the
specification — a spec is only as good as its ability to produce a working implementation by someone who
wasn't in the room when it was written, and every place that port had to guess is a defect in the document.

### What does not work yet

- **Internet play through the app's own UI.** The route itself works — the account (no-PIN) registration
  that used to block it is solved, and a full off-network session carried video at 60 fps on 2026-09-05.
  What is not done is the last wiring: route selection is injectable and unit-tested, but has only ever been
  driven from `tools/Ripcord.ProtocolLab`, not clicked through the app. Expect it to work; it has not been
  demonstrated that way.
- **Following a console paired by typed address.** A console added by hand-typed IP carries no `HostId`, so
  it cannot be relocated after a DHCP lease change. Every other pairing route can.
- **True HDR output.** HDR is negotiated correctly and the console sends it, but the swap chain is still SDR.
  The capability probe also answers "is any connected display HDR" rather than "is the display this window is
  on HDR", so it can offer HDR on a monitor that cannot present it.
- **DualSense output** — haptics, adaptive triggers, lightbar — and **gyro/motion input**.
- **Xbox.** Named in the UI so the shape of the app is legible, with nothing behind it. There is no protocol
  work, and none is claimed.
- **Trimmed publishing** is off (`PublishTrimmed=False`). The remaining blockers are six `IL2026` warnings in
  the cloud client; WinUI's own CsWinRT layer produces a further 37 that no app code can fix.

The open backlog is tracked in [`ROADMAP.md`](ROADMAP.md), which is the source of truth — this section
summarises it. [`docs/README.md`](docs/README.md) indexes the rest of the documentation.

---

## Building

**Windows is required.** The solution mixes `net10.0` managed projects with two native C++/WinRT projects
(`Ripcord.Media.Interop`, `Ripcord.Input.Interop`), and the app itself targets
`net10.0-windows10.0.26100.0` with WinUI 3.

Prerequisites:

- Windows 11 (or Windows 10 build 26100+ SDK)
- .NET 10 SDK
- Windows App SDK
- A C++ toolchain — MSBuild or Visual Studio with the Desktop C++ workload

Both **x64** and **ARM64** are supported build platforms — a Snapdragon/ARM64 Windows machine builds and
runs the whole stack natively, with no emulation. The carry-less GHASH path is written for each
(`PCLMULQDQ` on x64, `PolynomialMultiplyWidening` on ARM64), so session-crypto throughput does not depend on
which host you are running.

The native `.vcxproj` projects are **not** buildable with plain `dotnet build`. Build everything through
MSBuild, from a Developer Command Prompt or Visual Studio:

```
msbuild Ripcord.slnx -p:Platform=ARM64      # or -p:Platform=x64
```

`Platform` defaults to the solution's first platform (x64), so pass it explicitly on an ARM64 host.

Once the native interop DLLs exist under `<ARM64|x64>/<Config>/`, the managed app alone can be built and
run — the architecture is inferred from the host:

```
dotnet build src/Ripcord.App/Ripcord.App.csproj
dotnet run   --project src/Ripcord.App/Ripcord.App.csproj
```

The two test suites need none of the above and run anywhere, including WSL and Linux CI:

```
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
```

A handful of tests validate against real captured ground truth held outside the repository. They self-skip
when those fixtures are absent, so a full green run does not require them.

**Building from WSL against a Windows checkout:** pass `--artifacts-path` to keep build output off the
9p-mounted tree, or builds will be slow and may fail on file locking.

**Ship Release for handhelds.** Debug builds of the native DLLs link the non-redistributable Visual C++ debug
runtime and will not start on a machine without the Visual Studio redistributable installed.

---

## Layout

Dependency direction is strictly platform-specific → shared → core; `Ripcord.Core` knows nothing about
PlayStation.

| Project | Role |
|---|---|
| `Ripcord.Core` | Platform-neutral session, input, and settings contracts |
| `Ripcord.Core.Net` | Transport primitives: TCP/UDP, mDNS, STUN, WebSockets, crypto wrappers |
| `Ripcord.Cloud.Halyard` | PSN cloud client — OAuth2, console list, wake, and the WAN rendezvous |
| `Ripcord.Protocol.Halyard.Common` | Transport-neutral protocol types, message codec, and the crypto seam |
| `Ripcord.Protocol.Halyard.Takion` | The SCTP-over-UDP streaming transport |
| `Ripcord.Protocol.Halyard` | Session composition: discovery + transport + crypto + demux |
| `Ripcord.Media`, `.Media.Audio`, `.Media.Interop` | D3D12/MFT video pipeline and WASAPI audio |
| `Ripcord.Input`, `.Input.Common`, `.Input.Interop` | Controller sources, merged into one stream |
| `Ripcord.Client` | Headless session lifecycle owner (connect, reconnect, stats) |
| `Ripcord.Presentation` | The portable app layer — view-models and flow state machines, no UI framework types |
| `Ripcord.Presentation.Halyard` | The PlayStation backend for that layer, and the composition root |
| `Ripcord.App` | The WinUI 3 shell |
| `Ripcord.Diagnostics` | Tracing/metrics behind the diagnostics overlay |
| `tools/Ripcord.ProtocolLab` | Console harness — drives the connect flow and replays captures |
| `tools/Ripcord.HidCapture` | Standalone HID capture utility for controller work |
| `ports/ripcord-3ds` | A second client, in C, for New 3DS hardware — not part of the solution |

`Ripcord.Presentation` is plain `net10.0` and no UI-framework type may cross into it — no brush, no
visibility, no dispatcher. Presentation concerns are portable enums that each front end maps to its own
types, and a test enforces the rule by reflecting over referenced assemblies, because prose alone has not
held elsewhere in this repository.

`Halyard`, `Takion` and `Senkusha` are codenames used throughout the code. Only `Halyard` is our invention;
the other two are the vendor's own internal names, retained as protocol terminology. See
[`CLAUDE.md`](CLAUDE.md) for the naming table and the reasoning.

---

## Provenance and legal position

This section is load-bearing, so it is stated plainly rather than gestured at.

Ripcord is an **interoperability** implementation: it connects a user's own client to a user's own console,
under that user's own account. The specification was derived from:

- static analysis of the publicly distributed vendor client, on hardware the authors own;
- the authors' own packet and memory captures of their own console and account;
- public references — RFCs, NIST test vectors, platform crypto documentation.

It reproduces **interface facts** necessary for interoperability: protocol field numbers, enum values, byte
offsets, JSON configuration keys, and HTTP header names. Those cannot be changed without breaking
interoperability, and interface facts are not protectable expression. It does **not** reproduce vendor source
code, symbol names, or log strings; vendor code is cited only by relative virtual address.

**No other implementation of these protocols is used as a source** — not for implementation detail, not for
byte-level constructions, not for naming. Where a value in the spec is an *assumption* rather than something
our own evidence established, it carries an explicit `[X]` tag, and the open list of those is stated plainly in
[`ROADMAP.md`](ROADMAP.md) rather than buried. If you are evaluating this project's provenance, that list is a
better guide than this paragraph.

### Interoperability constants

Ripcord includes roughly **4 KB of protocol constants** — four key-derivation tables (a PS5 pair and a PS4
pair), two registration key tables, two material-wrap tables, the four field context keys and the
registration context key, and a byte offset. The console computes against these values; a client
cannot speak the protocol without them, and changing them breaks interoperability. They are interface facts,
not authored expression, and they are shipped as **data** read through the same configuration seam that accepts
a local override — not compiled into program logic.

They are **generic to the protocol**: identical for every user and every console.

**Nothing user-specific is included.** No registration key, pairing record, session key, device identifier, or
account identifier — not in the repository and not in its history. Those are generated or captured per user and
never leave the user's machine. The distinction matters and is enforced by a test: the bundled data is checked
for the absence of any per-console or per-account field.

A build without the constants can be produced at any time:

```
msbuild Ripcord.slnx -p:BundleInteropConstants=false
```

That omits the data entirely. The app then reports the constants as unavailable and declines to pair, exactly
as it behaves on a machine that has none — a clean failure, not a crash.

### The application OAuth credential

Ripcord also includes the OAuth 2.0 `client_id`/`client_secret` with which the vendor's own desktop client
authenticates to PSN. **This is a different kind of thing from the constants above, and the two arguments must
not be read as one.** Those constants are values the console computes against, and a client cannot speak the
protocol without them. This is an *access credential*, and a client demonstrably speaks the protocol without
it — a LAN session works against a console with no internet connection at all.

It is included for a narrower reason: PSN operates no third-party client registration, so there is no
credential an independent client could obtain instead. Without it the account tier — signing in, reading the
account's own console list, waking a console remotely — is unreachable by any client that is not Sony's.

It passes the same generic-versus-personal test: identical for every user, tied to no account or console,
authenticating an *application* rather than a person. It was recovered from the authors' own capture of their
own traffic; it was separately confirmed to be already published, but that confirmation came afterwards and
is not the basis for including it. **No user credential is bundled** — the signed-in account's tokens are
obtained per user at sign-in and stored encrypted on that user's own machine.

```
msbuild Ripcord.slnx -p:BundleOAuthClient=false
```

That produces a build with no credential: account sign-in reports itself unavailable and LAN play is
unaffected. At run time the bundled value is the last resort, overridden by `RIPCORD_CLIENT_ID` /
`RIPCORD_CLIENT_SECRET` or a `client.json`, so an operator with their own registered client can substitute it
without rebuilding.

Sony may rotate this credential at any time, which would disable the account features until it is updated.

*This is not legal advice.* Anti-circumvention law (DMCA §1201 and its interoperability exception §1201(f),
the EU Software Directive Art. 6) and platform terms of service are fact- and jurisdiction-specific.

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Contributions require a DCO sign-off and a clean-room attestation —
the latter matters more here than in most projects, and the reasons are explained there.

Security reports: [`SECURITY.md`](SECURITY.md).

---

## AI assistance disclosure

Ripcord has been developed with heavy use of AI coding assistants, including its protocol research,
implementation, tests, and documentation. This is disclosed deliberately: the project publishes under a real
identity in a legally sensitive area, and a consistent record of candour is worth more than the ambiguity of
silence.

It carries a specific obligation, which [`CONTRIBUTING.md`](CONTRIBUTING.md) spells out. A language model may
have another Remote Play implementation in its training data and can reproduce that implementation's
structure or constants without being asked to. Guarding against that is part of the clean-room discipline
here, not an afterthought.

---

## Licence

[Apache-2.0](LICENSE). See [`NOTICE`](NOTICE) for the interoperability and non-affiliation statement.

Apache-2.0 was chosen over a permissive alternative for its express patent grant and patent-retaliation
clause, which matter when implementing a protocol owned by a large patent holder, and over a copyleft licence
for compatibility with MSIX/Microsoft Store distribution.
