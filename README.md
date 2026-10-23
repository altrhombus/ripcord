# Ripcord

**Your PlayStation, on more screens than you'd think.**

Ripcord is an independent PlayStation Remote Play client. It talks straight to your PS5 or PS4,
over your home network or across the internet, and puts the game on whatever you're holding: a Windows PC, a
Mac, and (because it seemed like a good idea at the time) a **PlayStation 3**.

All of it was built from scratch. There's no vendor code here, and the protocol specification in
[`docs/protocol/`](docs/protocol/) was worked out independently. [Provenance](#provenance)
below explains how. It's worth a read, I promise.

> **Not affiliated with Sony Interactive Entertainment.** "PlayStation", "PS5", "PS4" and "Remote Play" are
> their trademarks, used here only to describe compatibility. See [`NOTICE`](NOTICE).

---

## The good stuff

### A PlayStation 3 streams a PS5

Yes, really! The PS3 port streams **720p at 60 frames a second from a PS5 or a PS4**. The console's own
hardware decoder handles the video and the RSX scales it, so the whole video path costs **113 µs of a
16,667 µs frame** (that's 0.68%). It pairs using the PS3's on-screen keyboard, and if the PS3 is signed in to
your PlayStation Network account it finds your account id on its own, so you never have to type 19 digits
with a controller. It even has a home screen that looks right at home on the XMB.
Grab it here: [`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0).

Along the way it measured the clock every timeout in the protocol depends on: **79,800,986 Hz against the
79,800,000 we expected**. Twelve parts per million out. Not bad for a console from 2006.

### The dotnet client, on Windows

This is the flagship, and the one heading for 1.0. It's been run against real consoles, **PS5 and PS4**:

- **1080p60 at 23 Mbps,** with 0.4% packet loss, a 2.1 ms handshake, 6.5 ms round trips, and 18 ms from
  unpacking the stream to the frame on screen. Video is decoded on the GPU (H.264 and HEVC, 10-bit and HDR
  included) and presented through D3D12.
- **Play from anywhere.** Across the internet, both ends behind NAT, no port forwarding: it finds a direct
  peer-to-peer path, and a stream from a phone hotspot looks just like one on the LAN.
- **Pairing every way.** Type the console's code, or sign in to PlayStation Network and pick your console. It
  wakes a console from rest, locally or over the internet, and handles a locked profile's passcode.
- **The controller you already have.** An Xbox pad, a DualSense over USB or Bluetooth (PS button and touchpad
  click included), or the keyboard with your own bindings. Use two pads at once, or swap mid-game.
- **A Windows 11 app that looks like one,** designed as a Fluent showcase rather than a sample.
- **No telemetry.** Ripcord talks to your console, to PlayStation Network if you sign in, and to two public STUN
  servers when you play over the internet. That's it. See [`PRIVACY.md`](PRIVACY.md).

### One engine to run them all

The protocol also lives in a **Rust engine** (`engine/`), which will become the one engine under every
first-class client. It's about **fourteen times faster per packet** than the C core it grew from (0.53 µs
against 7.7 µs), it's fuzzed every night across ten targets with Miri watching over its C interface, and its
first session on real hardware streamed 1080p60 HEVC for twenty seconds without losing a packet.

### A Mac app, with iPhone and Apple TV on the way

The **Mac app** runs on that engine: a library of your consoles, pairing by sign-in or code, a stream window
with an inspector, Picture in Picture, recording, a menu bar extra, widgets, and Siri and Shortcuts actions.
The Mac has streamed from a real PS5, on the LAN and over the internet, through its lab tool `ripcord-lab`.
The app itself hasn't met a console yet (that's the next hardware session). **iPhone, iPad and Apple TV**
share the same code, and their app builds but hasn't run on a device yet ([the plan](docs/ios-plan.md)).

### And the rest

- **A New 3DS** streams a PS5. It was the first port, and the proof that someone other than the spec's author
  could build a client from it.
- **A PS Vita** port lives on its own branch.
- **Built with care.** Every pull request runs a dozen CI jobs across Windows, Linux and macOS, and the engine
  gets fuzzed and checked with Miri every night. The C core is fuzzed too (its very first CI run found a real
  bug). A published-tree sweep and a commit-time leak guard keep personal details out of the history, and the
  lab tools print placeholders instead of real addresses, so a test run can be written up safely.

---

## Where things stand

| Client | State |
|---|---|
| **Windows** (the dotnet client) | Streams PS5 and PS4, on the LAN and over the internet. Heading for **1.0**: no download yet |
| **PlayStation 3** | **Released:** [`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0), 720p60 from a PS5 or PS4 |
| **New 3DS** | Streams a PS5 |
| **macOS** | On the Rust engine. Streams from its lab tool; the app is waiting for its first hardware session |
| **iPhone, iPad, Apple TV** | Builds for all three; not yet run on a device |
| **PS Vita** | On its own branch |
| **Linux** | Planned, on the Rust engine |

### What doesn't work yet

- **Your PlayStation Network account.** Signing in uses your account from software Sony didn't make and
  doesn't support. Sony decides what it allows on its network, and Ripcord can't promise how it treats an
  unofficial client. If that's a concern, play on your own network without signing in: pairing by code and
  local streaming need no account.
- **IPv6-only networks.** Everything is IPv4, including the STUN path internet play depends on.
- **DualSense output and motion:** haptics, adaptive triggers, lightbar and gyro.
- **Following a console paired by typed address** after its DHCP lease changes. Every other pairing route can.
- **Internet play to a console in rest mode** probably fails in the dotnet client. Wake it first, or play on
  the same network. (The Mac showed why on 2026-09-25, and the fix is in [`ROADMAP.md`](ROADMAP.md).)
- **HDR after dragging the window** between an HDR display and an SDR one mid-stream hasn't been checked since
  Ripcord started tone-mapping HDR itself. Starting the stream on the display you'll watch it on is the tested
  path.
- **Xbox consoles.** They're named in the app so the design has room for them, and there's nothing behind that
  yet.
- **A Windows download.** Build from source for now; the release zip is part of 1.0.

### Your first ten minutes with the dotnet client

Here's what a first run meets, so none of it is a surprise:

- **Unzip it somewhere it can stay,** not Downloads, and run `Ripcord.exe` from the `Ripcord` folder. Desktop
  shortcuts and the taskbar's jump list remember where it is.
- **Windows 11, 25H2 or later.** That's what Ripcord is built and tested for. The release zip doesn't check, so
  an older Windows 11 may start it, but that isn't supported.
- **SmartScreen will warn you** the first time you run it, because the zip isn't signed yet. Choose **More
  info**, then **Run anyway**.
- **Smart App Control blocks it outright.** If it's on, which it can be on a new PC or a fresh Windows 11
  install, Windows refuses unsigned apps with no way to run them anyway. Ripcord won't start on that PC until
  it's signed, which is planned.
- **If your PS5 has HDR on,** choose **Best picture** when Ripcord first asks, or turn on **HEVC** and
  **Request HDR** in Settings. With HDR on, the console's ordinary (SDR) stream loses the detail in bright skies
  and highlights; asking for HDR keeps it, and Ripcord converts it for an SDR screen. HEVC needs Microsoft's HEVC Video Extensions: many PCs have them already, and
  they're in the Microsoft Store otherwise. Without them, Ripcord connects with H.264 and says so.
- **Xbox controllers need Microsoft's GameInput.** If one does nothing, run `GameInputRedist.msi` from the
  Ripcord folder once (it asks for administrator rights), then restart Ripcord. **About**, beside Settings, says
  whether it's installed, and so does the first-run setup. A DualSense doesn't need it.
- **English only.** The strings are ready for translation, but no other language ships yet.
- **Windows Firewall may ask about Ripcord** the first time it looks for your console. Allow it, at least on
  private networks. If the search finds nothing, check Windows Security > Firewall & network protection > Allow
  an app through firewall, and whether your network is set to Public.
- **Turn on Remote Play on the console first.** Ripcord notices when it's off and tells you where the setting
  is, but it can't flip it for you.
- **Pairing by code** needs the console awake, on the same network, with its 8-digit code on screen (the app
  tells you where to find it). **Pairing by sign-in** needs no code, using a PlayStation Network sign-in that
  opens inside the app; Ripcord still finds the console on your network first.
- **Waking from rest** needs the console's rest-mode network options turned on. A wake sent through your
  account shows as *asked*, not *woken*, because PlayStation Network accepting it doesn't mean the console
  heard.
- **An Xbox pad has no PS button,** and Windows keeps the Guide button for itself. The on-screen controls have
  a PS button for exactly this.

### What's next

1. **The dotnet client's 1.0:** a download a stranger can install, pair and play with. The short version and
   the checklist are in [`ROADMAP.md`](ROADMAP.md#10-the-short-version).
2. **The Mac app's first hardware session,** then its first release.
3. **iPhone, iPad and Apple TV** on real devices ([`docs/ios-plan.md`](docs/ios-plan.md)).
4. **A Linux client,** on the same Rust engine as the Mac ([`docs/engine-plan.md`](docs/engine-plan.md)).
5. **Pairing a console port from your desktop,** so a PS3 never needs its own keyboard dance
   ([`docs/port-pairing.md`](docs/port-pairing.md)).
6. **Windows on the Rust engine,** after its 1.0.

[`ROADMAP.md`](ROADMAP.md) is the whole backlog, and [`docs/journal.md`](docs/journal.md) is the dated story of
how everything above came to work. [`docs/README.md`](docs/README.md) maps the rest of the documentation.

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

To make the self-contained release build (it carries its own .NET and Windows App SDK runtimes):

```
msbuild Ripcord.slnx -p:Platform=x64 -p:Configuration=Release
dotnet publish src/Ripcord.App/Ripcord.App.csproj -c Release -p:Platform=x64
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

Ripcord is an interoperability implementation: it connects your own client to your own console, under your
own account. The protocol was worked out from the authors' own installed copies of the vendor client, the
authors' own captures of their own console and account, and public references (RFCs, NIST test vectors,
platform documentation). **No other implementation of these protocols is used as a source**, not for
implementation detail, constants, byte layouts or naming. Where a value is an assumption rather than something
our own evidence established, it carries an `[X]` tag next to it in [`docs/protocol/`](docs/protocol/).
Vendor code is cited by address only; no vendor source, symbol names or log strings are reproduced.

Two generic values recovered from the vendor client are committed on purpose: about 4 KB of protocol
constants the console computes against, and the application OAuth credential the vendor's own client signs
in with. Each is identical for every user and console, each can be left out of a build
(`-p:BundleInteropConstants=false`, `-p:BundleOAuthClient=false`), and nothing tied to any user, console or
account is included. [`NOTICE`](NOTICE) describes the methods and both values in full, and
[`docs/protocol-research-log.md`](docs/protocol-research-log.md) records each reference consulted.

*Nothing here is legal advice, and Ripcord makes no claim about how any law applies to it.*

---

## Reporting a problem

Open an issue saying what you did and what happened. If Ripcord crashed, attach
`%LocalAppData%\Ripcord\state\crash.log`; for a stream that misbehaved, the newest `session-trace-*.csv` from the
same folder, or the diagnostics file **F8** saves there. Read a file before you attach it:
[`PRIVACY.md`](PRIVACY.md) says what each one can contain. Security problems go to [`SECURITY.md`](SECURITY.md)
instead.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Contributions require a DCO sign-off and an independence attestation —
the latter matters more here than in most projects, and the reasons are explained there.

Security reports: [`SECURITY.md`](SECURITY.md).

---

## AI assistance disclosure

Ripcord has been developed with heavy use of AI coding assistants, including its protocol research,
implementation, tests, and documentation. This is disclosed deliberately: this is a sensitive area, and a
consistent record of candour is worth more than the ambiguity of silence.

The assistant works under the same rules as everyone else: everything in the protocol comes from this
project's own evidence (see [Provenance](#provenance)), and the assistant is never used to obtain
implementation detail from another Remote Play implementation, not its source, constants, byte layouts or
naming. A language model may have another implementation in its training data and can reproduce it fluently
without being asked, so the method is **derive first, then confirm against our own evidence**: a value that
can't be traced to a capture, our own analysis or a public reference is marked `[X]` and derived properly.
[`CONTRIBUTING.md`](CONTRIBUTING.md) spells out the habits that go with that.

---

## Licence

[Apache-2.0](LICENSE). See [`NOTICE`](NOTICE) for the interoperability and non-affiliation statement.

Apache-2.0 was chosen over a permissive alternative for its express patent grant and patent-retaliation
clause, which cover what contributors license to each other (they grant nothing on anyone else's patents),
and over a copyleft licence
for compatibility with MSIX/Microsoft Store distribution.
