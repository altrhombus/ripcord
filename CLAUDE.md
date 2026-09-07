# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Ripcord is a from-scratch, clean-room PS5 Remote Play client for Windows (WinUI 3). It connects directly to a
PS5 over LAN, decodes the H.264/HEVC video stream via D3D12, plays audio via WASAPI, and sends controller
input back over the reverse-engineered wire protocol. The protocol was derived independently from the
project's own captures and from static analysis of **our own lawfully-obtained, installed copy** of the vendor
client — the possession point is load-bearing, so keep that framing wherever this is restated. No vendor code
is reproduced here — but a ~4 KB set of interoperability *constants* **is** bundled, deliberately and with an
argument attached; see "Bounded exception 1" below and `NOTICE`. Read "Clean-room rules" before touching
anything protocol- or crypto-related.

For the active backlog see [`ROADMAP.md`](ROADMAP.md), which is forward-looking only and is the source of
truth for what is left. When work lands, move the item into [`docs/journal.md`](docs/journal.md) rather than
deleting it. [`docs/README.md`](docs/README.md) maps which document answers which question.

## Commands

The solution (`Ripcord.slnx`) mixes .NET (`net10.0`) and native C++/WinRT projects, and the app itself
(`Ripcord.App`) is `net10.0-windows10.0.26100.0` + WinUI 3 — **build/run requires Windows** with the
Windows App SDK and a C++ toolchain (the native `Ripcord.Media.Interop` / `Ripcord.Input.Interop` vcxproj
projects are only buildable via MSBuild/Visual Studio, not plain `dotnet build`).

Both **x64** and **ARM64** are first-class build platforms — an ARM64 machine builds and runs the whole
stack natively, and either host can cross-build the other. Native interop output is per-architecture, in
`<ARM64|x64>/<Config>/<Project>/`.

```
# Build everything (Visual Studio / MSBuild, from a Developer Command Prompt or VS itself).
# Platform defaults to the solution's first (x64), so pass it explicitly on an ARM64 box.
msbuild Ripcord.slnx -p:Platform=ARM64

# Build/run just the managed app. Needs the native interop DLLs to exist already (the dotnet CLI cannot
# build vcxproj at all), so run the msbuild line above once first; the architecture is inferred from the
# host unless -p:Platform / -r says otherwise.
dotnet build src/Ripcord.App/Ripcord.App.csproj
dotnet run --project src/Ripcord.App/Ripcord.App.csproj

# The test suites (both pure managed, cross-platform, no console/hardware/GPU needed)
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj

# Run a single test
dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj --filter "FullyQualifiedName~BundledInteropConstantsTests.Control_BundlePs4KdfReproducesKnownVector"

# The console harness — the primary iteration/verification tool for protocol work (replay captures,
# talk to a real console: discover/login/wake/connect). See docs/history/phase1-lan-build-plan.md for its role.
dotnet run --project tools/Ripcord.ProtocolLab -- <command>
```

`Directory.Build.props` at the repo root owns the cross-architecture wiring, and is the place to look when
a build resolves the wrong native DLLs. It defines `$(RipcordRepoRoot)` (a `$(SolutionDir)` that is also
defined outside solution builds) and `$(RipcordNativePlatform)`, which answers "whose native interop output
does this build consume?" for the AnyCPU managed projects that cannot read `$(Platform)` themselves. It
resolves `$(Platform)` → RID → host architecture, and `-p:RipcordNativePlatform=<arch>` overrides the lot.
**Never hard-code an architecture into a path**; use those properties.

Some tests (`LiveControlVectorTests` and similar) validate against real captured ground truth loaded from
gitignored fixtures under `docs/protocol/captures/`; they self-skip (`Xunit.SkippableFact`) when the
fixture isn't present, so a full green run is possible without the dirty-room materials.

## Architecture

**Dependency direction is strictly platform-specific → shared → `Ripcord.Core`.** `Ripcord.Core` (and
`Ripcord.Core.Net`) know nothing about PlayStation; protocol/platform code lives above them and depends
down, never the reverse.

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

### The crypto seam pattern

The one genuinely unsolved-at-design-time piece (session crypto) is isolated behind an interface
(`IHalyardSessionCrypto` in `Ripcord.Protocol.Halyard.Common`) with two implementations:
`PassthroughHalyardSessionCrypto` (a no-op stub) lets the rest of the pipeline — transport, framing,
demux, decode, input — be built and tested independently of the crypto research. `HalyardV1SessionCrypto`
is the real implementation. Swapping stub → real is a DI change. If you're extending the protocol stack,
prefer this seam-and-stub pattern over coupling new work to not-yet-solved crypto.

### The app layer: four rules

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

### Naming

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
stated. **This is a stated exception to the "never the vendor's own symbol/string names" rule below** — the
rule still governs everything else. Do not extend the exception without recording it here.

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

## Clean-room rules (hard constraint for any protocol/crypto work)

This project's legal footing depends on the protocol spec (`docs/protocol/`) being derived independently.

**What follows is the working discipline, not the legal argument, and the two are not interchangeable.**
The legal position — interoperability scope, the interface-facts reasoning, DMCA §1201(f) and EU Software
Directive Art. 6, and the standing caveat that none of it is a determination of compliance and that anything
distributed should be reviewed by qualified counsel — is set out in
[`docs/protocol/README.md`](docs/protocol/README.md) ("Legal note") and [`NOTICE`](NOTICE), with a summary in
[`README.md`](README.md). The rules below are how the work is actually carried out so that position stays
true. Do not read this file as the project's legal reasoning, and do not restate its rules as though they
were legal conclusions.

When working in `Ripcord.Protocol.Halyard*`, `Ripcord.Cloud.Halyard`, or anything crypto-related:

- Derive behavior **only** from this project's own dated spec docs (`docs/protocol/`) and its own captures,
  plus public references (RFCs, NIST test vectors, .NET crypto docs). **Never open, quote, or reproduce
  another implementation of these protocols to obtain implementation detail** — no source, no constants, no
  byte-level layouts, no naming — and don't use an AI agent as an indirect route to the same thing. There is
  exactly one bounded exception, a provenance audit, and it never supplies a fact we lack: see "Auditing is
  a distinct, permitted activity" below, and treat the two bullets as one rule. The
  boundary is *what gets obtained*, not whether a model was involved: AI assistance is normal here, and the
  method is the one this project has used throughout — **derive independently first, then confirm.**
- **"Another implementation" means another project's, not Ripcord's own.** A same-project port —
  `ports/ripcord-3ds` today, any future one — may read, port, and directly adapt code from `src/` at will;
  it is this project's own reference implementation, not the external source this rule exists to keep out.
  There is no independent-derivation ritual to perform between Ripcord's own front ends. Citing what a
  file was ported from is still good practice (it tells the next reader where to look when the two
  diverge), but it is a courtesy, not a legal requirement the way it is for `docs/protocol/` itself.
- **The AI-specific risk is laundered provenance, not the tool.** A model may hold another implementation in
  training data and can reproduce its structure, constants, or invented vocabulary *without being asked and
  without saying so*, so you can end up with contaminated detail believing you derived it. If a value appears
  that you cannot trace to a capture, our own binary analysis, or a public reference, **do not keep it** —
  mark it `[X]` and derive it. Be wary of fluent, confident specificity about wire formats; that is what
  recall looks like, and it reads like competence.
- **Marking uncertainty is mandatory, not optional.** `[X]` means *assumed, never confirmed against the
  console*. An `[X]` value with no accompanying `[C]`/`[V]`/`[W]` tag is provisional and must never be
  described in code or docs as confirmed — an over-confident comment stops the next person checking. The open
  list lives in `ROADMAP.md`; when you can't settle something, that list is where it belongs.
- **Auditing is a distinct, permitted activity.** Reading another implementation *to compare it against ours*
  — a provenance/similarity audit — is allowed and occasionally worth doing, since an independence claim is
  only as good as its last check. Two conditions: the output is a **findings report, never code or spec
  text**, and it must not become a back door for a fact we hadn't already derived (if the comparison reveals
  an answer we lack, that's an open question to mark `[X]` and derive — not a value to adopt). Keep such
  reports in the gitignored dirty room, never in the committed tree.
- Captured secrets never get committed. They live in the gitignored `docs/protocol/captures/` "dirty
  room" (raw captures, the RE session log, and this project's own Python analysis and reference-
  reimplementation scripts, which have real constants embedded)
  and are supplied to the implementation as out-of-band config.
  `docs/protocol/captures/lab-notebook.md` in particular is the live RE session journal (the
  unredacted working copy of `docs/protocol-research-log.md`) — useful context if it's present locally,
  but never assume it exists or commit to it.
- **Bounded exception 1: the v1 interoperability constants** in
  `src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` are committed deliberately (~4 KB of constant
  data — 8.7 KB on disk, since the JSON stores it hex-encoded: four
  control KDF tables — a PS5 pair and a PS4 pair — four field context keys, two registration key tables
  (PS5 and PS4), two material-wrap tables (PS5 and PS4), a byte offset). The
  test for whether something qualifies is **generic vs. personal**, not extracted vs. derived: these are
  identical for every console and every account, and a client cannot speak the protocol without them,
  which is the interface-fact argument `NOTICE` makes in full. Anything tied to a specific console, user,
  or account — registration keys, pairing records, session keys, device or account ids — stays in the
  dirty room, and `BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial` enforces that line.
  Do not widen this exception without amending `NOTICE` and this file together.
- **Bounded exception 2: the application OAuth credential** in
  `src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json` (added 2026-08-07, deliberately, by the project
  owner's decision — this one had sat unresolved as "the OAuth decision" for months). It is the vendor desktop
  client's own `client_id`/`client_secret`, recovered from **our own capture of our own traffic** and
  independently confirmed to be already published. PSN offers no third-party client registration, so there is
  no credential of ours to obtain: without it the account tier is unreachable by anyone but Sony.
  - **It passes the generic-vs-personal test** — identical for every user, tied to no account or console,
    authenticating an *application* rather than a person — which is why it qualifies at all. No user
    credential is ever bundled; the signed-in account's refresh token lives encrypted in the user's own
    `account.json`.
  - **But it is NOT an interface fact, and do not let the two arguments merge.** The v1 constants are values
    the console computes against; a client cannot speak the protocol without them. This is an *access
    credential*, and a client demonstrably speaks the protocol without it — a LAN session works against a
    console with no internet at all. `NOTICE` therefore argues it in a separate section, on separate grounds.
    If you are ever tempted to justify a third bundle by pointing at this one, that is the moment to stop.
  - **"Other implementations ship it too" is not the rationale and must never be recorded as one.** Our
    provenance is our own capture; the comparison against other projects came *afterwards* and is a
    permitted audit (see the auditing bullet above), not the source. Adopting a value *because* another
    implementation has it is exactly the contaminated route this section exists to close.
  - Omittable with `-p:BundleOAuthClient=false`, and overridden at runtime by `RIPCORD_CLIENT_ID`/
    `RIPCORD_CLIENT_SECRET` or a `client.json`. Repopulate from a capture with
    `tools/extract-oauth-client.py`; a checkout without the dirty room keeps the inert placeholder.
  - Same rule as above: do not widen **this** exception without amending `NOTICE` and this file together.
- Code names vendor internals by relative virtual address (`FUN_<rva>`) only, never by the vendor's own
  symbol/string names.
- Keep `docs/protocol-research-log.md` current when derivation work progresses.

See `docs/protocol/README.md` for the full provenance/legal-posture writeup.

## Where to look next

- `ROADMAP.md` — the open backlog (start here for "what's next").
- `docs/README.md` — the documentation index: which document answers which question.
- `docs/architecture.md` — the canonical architecture writeup (this file's Architecture section in full).
- `docs/journal.md` — the dated engineering record; completed backlog items land here.
- `docs/history/phase1-lan-build-plan.md` — the historical build plan and seam architecture rationale.
- `docs/protocol/IMPLEMENTATION.md` — the crypto/protocol build order and "definition of done" checklist.
- `docs/protocol/ps5-remoteplay-v1-spec.md` — the authoritative wire-protocol spec.
