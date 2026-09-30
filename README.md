# Ripcord

**Your PlayStation, on more screens than you'd think.**

Ripcord is an independent PlayStation Remote Play client. It talks straight to your PS5 or PS4,
over your home network or across the internet, and puts the game on whatever you're holding: a Windows PC, a
Mac, and, because it seemed like a good idea at the time, a **PlayStation 3**.

All of it was built from scratch. There is no vendor code here, and the protocol specification in
[`docs/protocol/`](docs/protocol/) was worked out independently. [Provenance](#provenance),
below, explains how, and it's worth reading rather than skipping.

> **Not affiliated with Sony Interactive Entertainment.** "PlayStation", "PS5", "PS4" and "Remote Play" are
> their trademarks, used here only to describe compatibility. See [`NOTICE`](NOTICE).

---

## The good stuff

### A PlayStation 3 streams a PS5

Yes, really. The PS3 port streams **720p at 60 frames a second from a PS5 or a PS4**. The console's own
hardware decoder handles the video and the RSX scales it, so the whole video path costs **113 µs of a
16,667 µs frame**, which is 0.68%. It pairs itself with the PS3's on-screen keyboard. If the PS3 is signed in
to your PlayStation Network account, it finds your account id on its own, so you never type 19 digits with
a controller. It even has a home screen that looks at home on the XMB. Download it:
[`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0).

On the way, it measured the clock every timeout in the protocol depends on: **79,800,986 Hz against the
79,800,000 we expected**, 12 parts per million out.

### The dotnet client, on Windows

The flagship, and the one heading for 1.0. Verified against real consoles, **PS5 and PS4** alike:

- **1080p60 at 23 Mbps**, with 0.4% packet loss, a 2.1 ms handshake, 6.5 ms round trips, and 18 ms from
  unpacking the stream to the frame on screen. Video is decoded on the GPU (H.264 and HEVC, 10-bit included) and
  presented through D3D12.
- **Play from anywhere.** Across the internet, both ends behind NAT, no port forwarding: it finds a direct
  peer-to-peer path, and a stream from a phone hotspot looks just like one on the LAN. Pair and connect from
  the app itself.
- **Pairing every way.** Type the console's code, or sign in to PlayStation Network and pick your console. It
  wakes a console from rest, locally or over the internet, and handles a locked profile's passcode.
- **The controller you already have.** An Xbox pad, a DualSense over USB or Bluetooth (PS button and
  touchpad click included), or the keyboard, with your own bindings, and you can swap between them mid-game.
- **A Windows 11 app that looks like one,** designed as a Fluent showcase rather than a sample.

### One engine to run them all

The protocol now also lives in a **Rust engine** (`engine/`), which will be the one engine under every
first-class client. It is about **fourteen times faster per packet** than the C core it grew from (0.53 µs
against 7.7 µs). It's fuzzed nightly across ten targets, with Miri watching over its C interface. Its first
session on real hardware streamed 1080p60 HEVC for twenty seconds without losing a packet.

### A Mac app, and an iPhone and Apple TV on the way

The **Mac app** runs on that engine: a library of your consoles, pairing by sign-in or code, a stream window
with an inspector, Picture in Picture, recording, a menu bar extra, widgets, and Siri and Shortcuts actions.
The Mac has streamed from a real PS5 on the LAN and over the internet through its lab tool, `ripcord-lab`.
The app itself hasn't met a console yet; that's the next hardware session. **iPhone, iPad and Apple TV** share
the same code, and their app builds but hasn't run on a device yet ([the plan](docs/ios-plan.md)).

### And the rest

- **A New 3DS** streams a PS5. It was the first port, and the proof that someone other than the spec's
  author could build a client from it.
- **A PS Vita** port is on its own branch.
- **Built with care.**
  - Every pull request runs twelve CI jobs across Windows, Linux and macOS. Nightly runs fuzz the engine
    and check it with Miri.
  - The C core is fuzzed on every PR, and its first CI run found a real bug.
  - A published-tree sweep and a commit-time leak guard keep anyone's personal details out of the history.
  - The labs print placeholders instead of real addresses and ids, so a test run can be written up safely.

---

## Where things stand

| Client | State |
|---|---|
| **Windows** (the dotnet client) | Streams on the LAN and over the internet, PS5 and PS4. Working toward **1.0**: no installer yet, and three known bugs to fix |
| **PlayStation 3** | **Released:** [`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0), 720p60 from a PS5 or PS4 |
| **New 3DS** | Streams a PS5 |
| **macOS** | Built on the Rust engine. Streams from its lab tool; the app is waiting for its first hardware session |
| **iPhone, iPad, Apple TV** | Scaffolding that builds for all three; not yet run on a device |
| **PS Vita** | On its own branch |
| **Linux** | Planned, on the Rust engine |

### What doesn't work yet

- **IPv6-only networks.** Everything is IPv4, including the STUN path internet play depends on.
- **True HDR output.** HDR is negotiated and the console sends it, but the picture is still presented in SDR.
  The HDR check also asks whether *any* display is HDR rather than the one the window is on.
- **DualSense output and motion:** haptics, adaptive triggers, lightbar and gyro.
- **Following a console paired by typed address** after its DHCP lease changes. Every other pairing route can.
- **An Xbox pad alongside a DualSense.** With both connected, the Xbox pad goes quiet. It's one of the three
  bugs 1.0 fixes.
- **Xbox consoles.** They're named in the app so the design has room for them, and there's nothing behind
  that yet.
- **A Windows installer.** Build from source for now; the release zip and MSIX are part of 1.0.
- **Internet play to a console in rest mode** probably fails in the dotnet client. Wake it first, or play on
  the same network. The Mac showed why on 2026-09-25, and the fix is in [`ROADMAP.md`](ROADMAP.md).

### Your first ten minutes with the dotnet client

What a first run meets, so none of it is a surprise:

- **Windows 11.** The package declares Windows 10 1809 as its minimum, but only Windows 11 has been run.
- **English only.** The strings are ready for translation; no other language ships yet.
- **Turn on Remote Play on the console first.** Ripcord finds a console with it off and says so, but cannot
  turn it on. The app tells you where the setting is.
- **Pairing by code** needs the console awake and on the same network, with its 8-digit code on screen. The
  app says where to find it. **Pairing by sign-in** works from anywhere, and needs a PlayStation Network
  sign-in, which opens inside the app.
- **Waking from rest** needs the console's rest-mode network options turned on. A wake sent through your
  account is reported as *asked*, not *woken*, because PlayStation Network accepting it says nothing about
  whether the console heard.
- **An Xbox pad has no PS button,** and Windows keeps its Guide button for itself. The on-screen controls
  have a PS button for that.

### What's next

1. **The dotnet client's 1.0:** a download a stranger can install, pair and play with. The definition and
   the checklist are in [`ROADMAP.md`](ROADMAP.md#10--scope).
2. **The Mac app's first hardware session**, then its first release.
3. **iPhone, iPad and Apple TV** on real devices ([`docs/ios-plan.md`](docs/ios-plan.md)).
4. **A Linux client,** on the same Rust engine as the Mac ([`docs/engine-plan.md`](docs/engine-plan.md)).
5. **Pairing a console port from your desktop,** so a PS3 never needs its own keyboard dance
   ([`docs/port-pairing.md`](docs/port-pairing.md)).
6. **Windows on the Rust engine,** after its 1.0 ([`docs/engine-plan.md`](docs/engine-plan.md)).

[`ROADMAP.md`](ROADMAP.md) is the whole backlog, and [`docs/journal.md`](docs/journal.md) is the dated story
of how everything above came to work. [`docs/README.md`](docs/README.md) maps the rest of the documentation.

---

## Building

### Windows (the dotnet client)

Windows 11 with the .NET 10 SDK, the Windows App SDK and a C++ toolchain (Visual Studio's Desktop C++
workload, or MSBuild). **x64 and ARM64** are both first-class: an ARM64 machine builds and runs everything
natively. The two native C++/WinRT projects need MSBuild, not `dotnet build`:

```
msbuild Ripcord.slnx -p:Platform=x64        # or -p:Platform=ARM64
dotnet run --project src/Ripcord.App/Ripcord.App.csproj
```

Build Release for handhelds: Debug builds of the native DLLs link the Visual C++ debug runtime, which a
machine without Visual Studio doesn't have. From WSL, pass `--artifacts-path` to keep build output off the
mounted tree.

### macOS, iPhone, iPad and Apple TV

macOS 26, Xcode and a stable Rust toolchain. The Xcode project builds the engine itself.
[`src/Ripcord.Mac/README.md`](src/Ripcord.Mac/README.md) has signing and release notes.

```
cd src/Ripcord.Mac
xcodebuild -project Ripcord.xcodeproj -scheme Ripcord -derivedDataPath build build        # the app
xcodebuild -project Ripcord.xcodeproj -scheme RipcordLab -derivedDataPath build build     # ripcord-lab
```

### The engine, the C core, and the tests

```
cd engine && cargo test --workspace --all-features                  # the Rust engine
make -C libripcord/tests                                            # the C core
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
```

The .NET suites need no console, GPU or Windows, and run anywhere, Linux and WSL included. A few tests check
against real captures that aren't published. They skip themselves in a clean clone, so a green run doesn't
need them. The console ports build with their own toolchains; each port's README explains how.

---

## Layout

Dependencies run one way: platform-specific, then shared, then core. `Ripcord.Core` knows nothing about
PlayStation.

| Where | What |
|---|---|
| `src/Ripcord.Core`, `Ripcord.Core.Net` | Platform-neutral contracts, and transport and crypto primitives |
| `src/Ripcord.Cloud.Halyard` | The PSN cloud client: OAuth2, the console list, wake and the internet rendezvous |
| `src/Ripcord.Protocol.Halyard*` | The protocol: message codec, crypto, the Takion transport, and the session |
| `src/Ripcord.Media*`, `Ripcord.Input*` | D3D12 video and WASAPI audio; controllers, merged into one stream |
| `src/Ripcord.Client`, `Ripcord.Presentation*` | The headless session owner, and the portable app layer with its PlayStation backend |
| `src/Ripcord.App` | The WinUI 3 app |
| `src/Ripcord.Diagnostics` | Metrics, and the redactor behind the labs' output |
| `src/Ripcord.Mac` | The Apple clients: the Mac app, `ripcord-lab`, and iPhone, iPad and Apple TV |
| `engine/` | The Rust engine, its C interface, and its fuzzers |
| `libripcord/` | The protocol in portable C, the console ports' core |
| `ports/ripcord-ps3`, `ports/ripcord-3ds` | The console ports |
| `tools/` | `ProtocolLab` (the harness), `HidCapture`, and the leak guard |

`Halyard`, `Takion` and `Senkusha` are the codenames used throughout. Only `Halyard` is ours; the other two
are the vendor's own names, kept as protocol terminology. [`CLAUDE.md`](CLAUDE.md) has the naming table and
the reasoning.

---

## Provenance

This section is load-bearing, so it is stated plainly rather than gestured at.

Ripcord is an **interoperability** implementation: it connects a user's own client to a user's own console,
under that user's own account. The specification was derived from:

- static analysis of the publicly distributed vendor client, on hardware the authors own;
- the authors' own packet and memory captures of their own console and account;
- public references — RFCs, NIST test vectors, platform crypto documentation.

It reproduces **interface facts** necessary for interoperability: protocol field numbers, enum values, byte
offsets, JSON configuration keys, and HTTP header names. Those cannot be changed without breaking
interoperability. It does **not** reproduce vendor source
code, symbol names, or log strings; vendor code is cited only by relative virtual address.

**No other implementation of these protocols is used as a source** — not for implementation detail, not for
byte-level constructions, not for naming. Where a value in the spec is an *assumption* rather than something
our own evidence established, it carries an explicit `[X]` tag, in the spec text itself rather than buried in a
separate list; they live throughout [`docs/protocol/`](docs/protocol/). If you are evaluating this
project's provenance, reading those tags where they sit — next to the value each one qualifies — is a better
guide than this paragraph.

### Interoperability constants

Ripcord includes roughly **4 KB of protocol constants** (8.7 KB on disk: the JSON stores them
hex-encoded) — four key-derivation tables (a PS5 pair and a PS4
pair), two registration key tables, two material-wrap tables, four field context keys — the registration
context key is one of those four, stored a second time under its own name, so the file holds four distinct
16-byte keys rather than five — and a byte offset. The console computes against these values; a client
cannot speak the protocol without them, and changing them breaks interoperability. They are interface facts,
and they are **data** in one committed file that no client copies. The dotnet client
reads it at run time through the same configuration seam that accepts a local override; the Rust engine (the
Apple clients) and the C core (the console ports) generate lookup tables from it at build time, never committed.

They are **generic to the protocol**: identical for every user and every console.

**Nothing user-specific is included.** No registration key, pairing record, session key, device identifier, or
account identifier — not in the repository and not in its history. Those are generated or captured per user and
never leave the user's machine. The distinction matters and is enforced by a test: the bundled data is checked
for the absence of any per-console or per-account field.

A build without the constants can be produced at any time:

```
msbuild Ripcord.slnx -p:BundleInteropConstants=false
cargo build --release -p ripcord-ffi --no-default-features   # the Rust engine, in engine/
```

That omits the data entirely. For the Apple clients, `RIPCORD_BUNDLE_INTEROP_CONSTANTS = NO` does the same in the
engine they build; the C core's console ports have no such switch. The app then reports the constants as unavailable and declines to pair, exactly
as it behaves on a machine that has none — a clean failure, not a crash.

### The application OAuth credential

Ripcord also includes the OAuth 2.0 `client_id`/`client_secret` with which the vendor's own desktop client
authenticates to PSN. **This is a different kind of thing from the constants above.** Those constants are values the console computes against, and a client cannot speak the
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

*Nothing here is legal advice, and Ripcord makes no claim about how any law applies to it.*

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Contributions require a DCO sign-off and a independence attestation —
the latter matters more here than in most projects, and the reasons are explained there.

Security reports: [`SECURITY.md`](SECURITY.md).

---

## AI assistance disclosure

Ripcord has been developed with heavy use of AI coding assistants, including its protocol research,
implementation, tests, and documentation. This is disclosed deliberately: the project publishes under a
consistent, attributable identity in a sensitive area, and a consistent record of candour is worth
more than the ambiguity of silence.

**The assistant works under the same provenance rules as everyone else, and they are why this disclosure
matters.** Everything in the protocol comes from this project's own work:
- the authors' own captures of their own console and account;
- static analysis of the authors' own installed copy of the vendor client;
- public references: RFCs, NIST test vectors and platform documentation.

The assistant is never used to obtain implementation detail from another Remote Play implementation: not
its source, its constants, its byte layouts or its naming. That is the same line
[Provenance](#provenance) draws for people, and routing around it through a model would be
the same breach.

It also carries a specific obligation, which [`CONTRIBUTING.md`](CONTRIBUTING.md) spells out. A language
model may have another implementation in its training data and can reproduce that implementation's
structure or constants without being asked to, fluently enough to pass for derivation. So the method here
is to **derive first, then confirm against our own evidence**. A value that can't be traced to a capture,
our own analysis or a public reference is not kept: it is marked `[X]` and derived properly. Guarding against
this is part of the independent-implementation discipline, not an afterthought.

---

## Licence

[Apache-2.0](LICENSE). See [`NOTICE`](NOTICE) for the interoperability and non-affiliation statement.

Apache-2.0 was chosen over a permissive alternative for its express patent grant and patent-retaliation
clause, which matter when implementing a protocol owned by a large patent holder, and over a copyleft licence
for compatibility with MSIX/Microsoft Store distribution.
