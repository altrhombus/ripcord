# Architecture

How Ripcord is arranged, and the rules that keep it that way. Read this before adding a project,
moving a type between layers, or touching anything in `Ripcord.Presentation`.

[`CLAUDE.md`](../CLAUDE.md) states the same rules tersely for AI coding agents. This file has the reasoning.
If the two ever disagree, this one is right and the other is stale.

## The one rule everything else follows

Dependency direction is strictly **platform-specific → shared → `Ripcord.Core`**. `Ripcord.Core` and
`Ripcord.Core.Net` carry no PlayStation *types and no references upward* — both declare zero project
references — and protocol and platform code sits above them and depends downward, never the reverse.

Precisely what that claims: their `ConsolePlatform` enum uses this project's own codenames (`Halyard`,
`Lanyard`) so the neutral layer never names a console, and some comments there cross-reference the layers
above to explain why a primitive is shaped as it is. A comment is not a dependency; a `using` or a type
would be, and `PresentationPortabilityTests` is what stops one appearing.

```
  FRONT ENDS      Ripcord.App            src/Ripcord.Mac          ports/ripcord-3ds  (C, devkitARM)
                  (WinUI 3, Windows)     (Swift: Mac, iPhone,     ports/ripcord-ps3  (C, ps3dev)
                         │                iPad, Apple TV)                 │
                         ▼                       │                        ▼
             Ripcord.Presentation.Halyard        ▼                    libripcord     portable C99,
                         │                    RipcordKit                  ╷          every port's core
                         ▼                       │                        ╷
  PORTABLE APP   Ripcord.Presentation            ▼                        ╷ ported from,
                         │                engine/ (Rust)                  ╷ not linked
                         ▼                ripcord-ffi → ripcord-net       ╷ against
                   Ripcord.Client           → ripcord-proto  ◀╶╶╶╶╶╶╶╶╶╶╶╶┤
                         │                                                ╷
                         ▼                                                ╷
  PROTOCOL     Ripcord.Protocol.Halyard  ◀╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╶╯   PS5/PS4 composition point
                    │            │
                    ▼            ▼
               .Takion       .Common      Ripcord.Cloud.Halyard
                    │            │                 │
                    └─────┬──────┴─────────────────┘
                          ▼
  FOUNDATION    Ripcord.Core  ·  Ripcord.Core.Net       no PlayStation knowledge
```

Solid arrows are assembly references on the managed side, Swift and Cargo dependencies on the Apple side, and
ordinary compilation on the ports side. Dotted lines are porting, not linking: the C core and the Rust engine
are this project's own re-implementations of the .NET protocol stack, and the .NET side stays the reference
for derivations (key schedules, KDFs, field ciphers, codecs). `ProtocolLab vectors` generates the known-answer
vectors the other two are tested against.

The ports exist partly as completeness tests for the specification: a spec is only as good as its ability to
produce a working implementation by someone who wasn't in the room when it was written. What a port contains
is its platform (video, audio, input, storage, a shell) and nothing else.

Media (`Ripcord.Media*`), input (`Ripcord.Input*`) and diagnostics (`Ripcord.Diagnostics`) hang off the
front end rather than the protocol stack, and reach native code through their `.Interop` halves.

## The projects

Each project, and what it is responsible for:

- **`Ripcord.Core`** — platform-neutral session/input/settings contracts (`IStreamingSession`,
  `IControllerSource`, credential store abstractions), plus local persistence for settings and the paired-
  console list (`Consoles/`, `Settings/`). No PS knowledge — `PairedConsole` is a plain credential holder, and
  turning one into a Halyard pairing record is an extension method on the Halyard side.
- **`Ripcord.Core.Net`** — transport primitives: TCP/UDP channels, mDNS, and thin wrappers over
  `System.Security.Cryptography` (AES-GCM, ECDH, HKDF). No PS knowledge, no key *schedule* — just
  primitives.
- **`Ripcord.Cloud.Halyard`** — the PSN cloud REST client (OAuth2, console list, session
  create/wake/signaling). Pure HTTPS/JSON.
- **`Ripcord.Protocol.Halyard.Common`** — transport-neutral PS5 types: the `/sess/rgst|init|ctrl` message
  codec, stream framing, controller-state → input-packet mapping, and **the crypto seam**
  (`IHalyardSessionCrypto` + `PassthroughHalyardSessionCrypto` stub + the real `HalyardV1SessionCrypto`
  implementation under `Crypto/V1/`). Shared by PS5 and PS4.
- **`Ripcord.Protocol.Halyard.Takion`** — the SCTP-over-UDP ("Takion") transport: handshake
  (INIT/INIT_ACK/COOKIE_ECHO/COOKIE_ACK), reliable-delivery layer (DATA/SACK, reassembly), and
  `HalyardTakionStream`, the orchestrator that demuxes control vs. A/V and drives the crypto seam.
- **`Ripcord.Protocol.Halyard`** — the PS5 `IStreamingSession` composition point: wires cloud
  coordination + LAN discovery + transport + handshake + crypto + stream demux into one session
  lifecycle (`HalyardStreamingSession`), plus pairing/registration.
- **`Ripcord.Media` / `Ripcord.Media.Audio` / `Ripcord.Media.Interop`** — D3D12 video decode/present
  pipeline and WASAPI audio; `.Interop` is the native C++/WinRT half.
- **`Ripcord.Input` / `Ripcord.Input.Common` / `Ripcord.Input.Interop`** — controller sources
  (DualSense HID, GameInput via native interop, keyboard), merged into one composite input stream.
- **`Ripcord.Client`** — headless `SessionController`, the app-facing session lifecycle owner (connect,
  reconnect/watchdog, stats) independent of any UI.
- **`Ripcord.Presentation`** — the portable app layer: view-models, flow state machines, and everything that
  decides *what a surface says*. Plain `net10.0`, and **no UI framework type may cross into it** — no `Brush`,
  no `Visibility`, no `DispatcherQueue`. Presentation concerns are portable enums and bools that each front end
  maps to its own types (`AccentRole`, `StatusTone`, `ActionGlyph`). `PresentationPortabilityTests` enforces
  the rule by reflecting over referenced assemblies, because prose alone has not held elsewhere in this repo.
- **`Ripcord.Presentation.Halyard`** — the PlayStation backend for that layer, and the one place a front end
  names Halyard at all: implementations of the discovery, registration, reachability and wake seams, plus
  `HalyardAppServices.Create`, which builds the whole graph. Same seam-and-stub shape as the crypto seam.
- **`Ripcord.App`** — the WinUI 3 shell (pages, settings, the streaming session page/HUD). Increasingly thin:
  pages render an immutable state record and turn input back into view-model calls.
- **`Ripcord.Diagnostics`** — tracing/metrics (`RipcordEventSource`, `MetricHistory`) used directly by
  the app for the diagnostics overlay, and `IdentifierRedactor`, which makes the labs print placeholders
  instead of real addresses and ids.
- **`tools/Ripcord.ProtocolLab`** — the console harness: drives the connect flow against a real PS5 and
  replays captures through the parsers. The iteration/verification tool for every protocol stage.
- **`tools/Ripcord.HidCapture`** — standalone HID capture utility for controller RE work.
- **`tools/leak-guard`** — the commit- and push-time check against the real values the captures folder holds, and
  **`tools/third-party-notices`**, which generates the Windows build's notices from its packages.

## The Rust engine and the Apple clients

[`engine-plan.md`](engine-plan.md), settled 2026-09-25, gives the first-class clients one protocol engine in
Rust. The Mac moved onto it on 2026-09-26; Windows follows after its 1.0, one seam at a time behind the
interfaces `Ripcord.Presentation` already defines.

- **`engine/`** is a Cargo workspace: `ripcord-proto` (sans-IO, `#![forbid(unsafe_code)]`), `ripcord-net` (the
  `std::net` driver that runs the connect sequence on the caller's thread), `ripcord-ffi` (the C ABI and the only
  crate that uses `unsafe`; its `build.rs` generates `ripcord.h` and `NativeMethods.g.cs`, never committed),
  `ripcord-kat` (the known-answer runner), `ripcord-diff` (differential tests against the C core) and
  `hosts/dotnet/` (the .NET harness). [`engine/README.md`](../engine/README.md) has the build and the measured
  figures.
- **`src/Ripcord.Mac`** is one Xcode project for every Apple client. `RipcordKit` is the Swift layer over the
  engine's C ABI (no C type reaches its callers), plus the cloud tier and pairing stores. `RipcordApp/` is the
  Mac app, `RipcordWidgets/` its widgets, `RipcordAppLogic/` its window-free logic (compiled into the app and
  the host-less `RipcordAppTests`), `RipcordLab/` is `ripcord-lab`, and `RipcordMobile/` is the iPhone, iPad and
  Apple TV app. Its design is in [`src/Ripcord.Mac/DESIGN.md`](../src/Ripcord.Mac/DESIGN.md).

## The C core and the ports

`ports/` holds from-scratch C clients for consoles the .NET stack cannot run on. They share a protocol
core, `libripcord/`, with each other, and nothing at all with `src/` at link time.

`libripcord` was the macOS client's core too until 2026-09-26, when the Mac moved to the Rust engine. It stays
as the console ports' core, taking fixes and port-driven work.

- **`libripcord`** — the protocol in portable C99: `crypto/`, `halyard/` (the control KDF and the field
  ciphers), `session/`, `discovery/`, `takion/`, `stream/` (A/V framing and Cauchy Reed-Solomon FEC over
  GF(2^8)), `input/`, `net/`, `util/`, plus `tests/` — a host-side known-answer suite that needs no
  console, no GPU and no hardware. It is not a library anyone designed: it is what was left over when the
  3DS port was audited for a second target and **71 of its 88 source files turned out to reference no
  operating system at all**.
- **`libripcord/platform/rc_platform.h`** — the seam, and the only header in the core that names an OS:
  a monotonic millisecond clock, a sleep, a high-resolution tick, a CSPRNG. Sockets are deliberately not
  in it; they are called as plain BSD names, which was expected to need a seam on Vita and did not.
  **Nothing goes in that header that only one platform needs** — a seam earns its place by having at
  least two real implementations, and anything a single port wants belongs in that port's tree behind a
  callback the port installs. That is why decode, audio and present are absent from it, despite being the
  largest platform surface any port has.
- **`ports/ripcord-3ds`** — modded New 3DS, devkitARM. Streams decoded video from a real PS5.
- **`ports/ripcord-ps3`** — PS3 homebrew, ps3dev/PSL1GHT. Decodes on the console's own `cellVdec`, scales
  on the SPEs, presents through the RSX, and pairs from the console with the system keyboard.

Dependency direction inside the core matches the .NET side's: `halyard/` depends on `crypto/` and never
the reverse, and `crypto/` knows nothing about PlayStation.

**The independence rule does not apply between Ripcord's own front ends.** A port may read, port and
directly adapt code from `src/` at will — that is this project's own reference implementation, not the
external source the rule exists to keep out. [`CLAUDE.md`](../CLAUDE.md) states the exception. Citing what
a file was ported from is good practice, because it tells the next reader where to look when the two
diverge, but it is a courtesy rather than an obligation.

A Vita port lives on its own branch (`feat/vita-port`), well behind `main`, and isn't in this tree;
[`journal.md`](journal.md) records what it established.

## The crypto seam pattern

The one genuinely unsolved-at-design-time piece (session crypto) is isolated behind an interface
(`IHalyardSessionCrypto` in `Ripcord.Protocol.Halyard.Common`) with two implementations:
`PassthroughHalyardSessionCrypto` (a no-op stub) lets the rest of the pipeline — transport, framing,
demux, decode, input — be built and tested independently of the crypto research. `HalyardV1SessionCrypto`
is the real implementation. Swapping stub → real is a DI change. If you're extending the protocol stack,
prefer this seam-and-stub pattern over coupling new work to not-yet-solved crypto.

## The app layer: four rules

If you are touching a page, a view-model or anything in `Ripcord.Presentation`, these are the standing rules.
The rationale for each lives in the file that implements it; what follows is what you have to obey.

1. **One immutable state record per view-model, recomposed whole.** Derive from `ObservableState<TState>` and
   override `Compose()`; mutate through `Mutate(...)`, which is the only place a change is applied and notified.
   There is exactly one notifying property (`State`) plus an `IObservable<TState>`, so a half-updated view-model
   is unrepresentable and record value equality answers "did anything change?" once, centrally. Do **not**
   reintroduce per-property notification, and do not reach for an MVVM framework to generate the cascade this
   pattern exists to delete. Collections are the one exception: `ObservableCollection<T>` stays, because
   in-place list updates are what keep gamepad focus from being destroyed on every refresh.
2. **One threading seam, and nothing in the layer takes a lock.** `IUiDispatcher` is the only marshalling
   point; view-models never marshal at their call sites. Every field is therefore read and written on the
   dispatcher thread alone. `Mutate` runs inline when already on that thread and **posts otherwise** — so
   anything a posted closure reads is read *later*. Decide freshness questions synchronously and capture the
   answer; a guard evaluated inside a posted closure is a bug, and has been twice.
   *The one lock in the layer is at its boundary, not inside it:* `ScanSink` in `RipcordAppServices`
   accumulates `IObserver` callbacks that genuinely arrive off the dispatcher thread, so the "one thread
   only" premise does not hold for it and a lock is the correct answer. That is the exception and it is
   named here so the rule stays literally true — if you want a lock on a *view-model* field, the answer is
   the dispatcher, not the lock.
   *Outside the layer,* where the UI thread meets a device or session thread (`SimpleObservable`,
   `CompositeControllerSource`, `MergedInputSource`, `Subject`), the guard is `SpinGate`, never `lock`. A
   contended `lock` on the WinUI UI thread waits in `CoWaitForMultipleHandles`, which pumps messages, and XAML
   re-entered that way fails fast with a stowed `E_UNEXPECTED`. That was the 2026-09-30 start-up crash.
3. **A device gets a seam; data does not.** Anything touching a GPU, a driver, a presenter or a native handle
   is reached through an interface implemented in the front end (`IVideoPipelineStats`,
   `IVideoCapabilitiesProbe`, `IConsoleReachabilityProbe`, `IConsoleScanner`, `IConsoleRegistrar`,
   `IConsoleWakeCoordinator`, `IShellNavigator`). Plain values are passed as parameters instead — see
   `SessionTelemetry`. Implementations may throw; the portable side guards each answer independently and
   degrades, because these surfaces are where a user goes to fix a bad configuration.
4. **The composition root owns construction.** `RipcordAppServices` (portable) is built once by
   `HalyardAppServices.Create(...)` at startup. Nothing else `new`s a store, a scanner or a probe; surfaces
   take what they need and hold that. There is deliberately no container and no `Resolve<T>()`.

## Naming

| Code name | Origin | Real-world referent |
|---|---|---|
| Halyard | **Ours** — invented | The Sony console backend (`Ripcord.Cloud.Halyard`, `Ripcord.Protocol.Halyard*`) |
| HalyardLegacy | **Ours** — invented | The same backend's older console generation (`ConsolePlatform`) |
| Lanyard | **Ours** — invented, provisional | The Xbox streaming backend — reserved in `ConsolePlatform`, not implemented |
| Takion | **Vendor's own codename**, retained | The SCTP-over-UDP transport layer inside Halyard |
| Senkusha | **Vendor's own codename**, retained | The echo/MTU/bandwidth probe sub-protocol |

`Halyard` replaces Sony's product name and is our invention. `Takion` and `Senkusha` are **Sony's own
internal codenames**, recovered from strings in our own copy of the vendor binary — not names we coined, and
not borrowed from any third-party implementation, which uses them for the same reason. They are retained deliberately as protocol terminology: they are load-bearing
in namespaces, ~10 class names, and assembly names, and renaming them buys little once their provenance is
stated. **This is a stated exception to the "never the vendor's own symbol/string names" rule** in
[`CLAUDE.md`](../CLAUDE.md) — the rule still governs everything else. Do not extend the exception without recording it here.

For protobuf **message and enum** names the rule is two-tier, and `docs/protocol/README.md` states it in
full: the vendor's *coined or arbitrary* names are renamed (`BIG`/`BANG` → `SESSION_REQUEST`/
`SESSION_REPLY`, `SENKUSHA` → `BANDWIDTH_PROBE`, `XMBCOMMAND` → `SYSTEM_MENU_COMMAND`, `GKTRACE` →
`TRACE`), while names that are *purely the plain-English description* of the field (`CursorPayload`,
`PacketLossPayload`, `VIDEO_DECODE`) are retained as interface labels — about two-thirds of the schema.
Field numbers and enum values are never changed. Vendor **symbol** names (C++ classes, functions, log
strings) are never used: name the thing for what it does and cite the vendor by RVA.

Required on-wire values (hostnames, the `"PS5"` platform tag, `RP-*` HTTP headers, JSON config keys like
`handshakeKey`) stay verbatim in code as protocol constants — those are interoperability facts, not
naming choices.

## Where the rules are enforced

Prose has not held in this repo, so the load-bearing rules have tests behind them. If you are changing
one of these, expect a failing build rather than a review comment:

| Rule | Enforced by |
|---|---|
| No UI framework type enters `Ripcord.Presentation` | `PresentationPortabilityTests` (reflects over referenced assemblies) |
| The bundled constants carry nothing per-console or per-account | `BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial` |
| Each native interop class is named only by its owner file | `NativeCapabilityAccessTests` (reads the class names from the IDL) |
| The UI thread never waits in a pumping lock | `SpinGateTests` (exclusion); the four sites use `SpinGate` |
| `Client-Type` has exactly two homes, and the engine reads rather than copies it | `BundledInteropConstantsTests.ClientType_*` |
| No real address, account id or long hex value in the published tree | `PublishedTreeSweepTests`, plus `tools/leak-guard` at commit and push |
| Every user-facing string is in the catalogue, with a translator comment | `LocalizationTests` |
