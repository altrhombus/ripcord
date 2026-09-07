# Architecture

How Ripcord is arranged, and the rules that keep it that way. Read this before adding a project,
moving a type between layers, or touching anything in `Ripcord.Presentation`.

The same material is stated more tersely in [`CLAUDE.md`](../CLAUDE.md), which is the brief loaded by
AI coding agents working on this repo. **This file is the canonical version**; if the two ever disagree,
this one is right and the other is stale.

## The one rule everything else follows

Dependency direction is strictly **platform-specific → shared → `Ripcord.Core`**. `Ripcord.Core` and
`Ripcord.Core.Net` know nothing about PlayStation; protocol and platform code sits above them and depends
downward, never the reverse.

```
  FRONT ENDS          Ripcord.App                ports/ripcord-3ds   (C, devkitARM)
                      (WinUI 3, Windows)         ports/ripcord-vita  (C, started)
                             │                            ╷
                             ▼                            ╷
                   Ripcord.Presentation.Halyard           ╷
                             │                            ╷
                             ▼                            ╷ ported from,
  PORTABLE APP       Ripcord.Presentation                 ╷ not linked
                             │                            ╷ against
                             ▼                            ╷
                       Ripcord.Client                     ╷   headless session lifecycle
                             │                            ╷
                             ▼                            ╷
  PROTOCOL         Ripcord.Protocol.Halyard  ◀╶╶╶╶╶╶╶╶╶╶╶╶╯   PS5/PS4 composition point
                        │            │
                        ▼            ▼
                   .Takion       .Common      Ripcord.Cloud.Halyard
                        │            │                 │
                        └─────┬──────┴─────────────────┘
                              ▼
  FOUNDATION        Ripcord.Core  ·  Ripcord.Core.Net       no PlayStation knowledge
```

Solid arrows are assembly references. The ports are **separate from-scratch C implementations** that port
logic from `src/` rather than linking against it — they exist as completeness tests for the specification,
on the principle that a spec is only as good as its ability to produce a working implementation by someone
who was not in the room when it was written.

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
  implementation under `Crypto/V1/`). Shared by PS5 now, PS4 later.
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
  the app for the diagnostics overlay.
- **`tools/Ripcord.ProtocolLab`** — the console harness: drives the connect flow against a real PS5 and
  replays captures through the parsers. The iteration/verification tool for every protocol stage.
- **`tools/Ripcord.HidCapture`** — standalone HID capture utility for controller RE work.

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
| Takion | **Vendor's own codename**, retained | The SCTP-over-UDP transport layer inside Halyard |
| Senkusha | **Vendor's own codename**, retained | The echo/MTU/bandwidth probe sub-protocol |

`Halyard` replaces Sony's product name and is our invention. `Takion` and `Senkusha` are **Sony's own
internal codenames**, recovered from our binary (`takion.proto`, `tak-c::parseMessage`,
`TakionNetConnection`) — not names we coined, and not borrowed from any third-party implementation, which
uses them for the same reason. They are retained deliberately as protocol terminology: they are load-bearing
in namespaces, ~10 class names, and assembly names, and renaming them buys little once their provenance is
stated. **This is a stated exception to the "never the vendor's own symbol/string names" rule** stated in
[`CONTRIBUTING.md`](../CONTRIBUTING.md) — the rule still governs everything else. Do not extend the exception without recording it here.

For protobuf **message and enum** names the rule is two-tier, and `docs/protocol/README.md` states it in
full: the vendor's *coined or arbitrary* names are renamed (`BIG`/`BANG` → `SESSION_REQUEST`/
`SESSION_REPLY`, `SENKUSHA` → `BANDWIDTH_PROBE`, `XMBCOMMAND` → `SYSTEM_MENU_COMMAND`, `GKTRACE` →
`TRACE`), while names that are *purely the plain-English description* of the field (`CursorPayload`,
`PacketLossPayload`, `VIDEO_DECODE`) are retained as interface labels — about two-thirds of the schema.
Field numbers and enum values are never changed. Vendor **symbol** names (C++ classes, functions, log
strings — e.g. `RpCryptAes`) are never used: name the thing for what it does and cite the vendor by RVA.

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
| Native capability queries have exactly one call site | `NativeCapabilityAccessTests` |
