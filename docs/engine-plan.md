# One engine, native shells — the plan

**The question this answers: what runs where on Ripcord's first-class platforms, what the shared engine
is written in, and how the project gets there from the engines it has today?**

Settled 2026-09-25. The decisions are recorded under "Decisions" at the end; two are deliberately left
to the platform plans that need them, and the whole plan passes through one gate, the spike in Phase 1. This is a plan, and it is forward-looking.
Work that lands moves to [`journal.md`](journal.md) and the open items stay in
[`../ROADMAP.md`](../ROADMAP.md), the same way as everywhere else in the repository. Where this file and
the roadmap disagree about what is still open, the roadmap wins.

[`macos-plan.md`](macos-plan.md) predates this plan and chose the C core for the Mac. This plan changes
that choice *after* the Mac's first release work is underway, and says below how the Mac moves without
stopping.

## The position

**Native apps on every first-class platform, one protocol engine underneath them, written in Rust.**

The first-class platforms are Windows, macOS and Linux now, and iOS, iPadOS and Android later. They are
what this architecture is designed for. The console ports matter, but they do not get to shape it: they
stay on the C core they already run, and are revisited on their own terms (see "The console ports").

Two ideas, argued separately:

**One engine, not one per platform.** What makes an app feel native is everything a user can see or
feel: the UI, video decode and present, audio, controllers, where secrets live, and the system features.
That is native per platform by construction. The wire protocol is the opposite: nobody experiences it,
so a native implementation of it buys a user nothing, and it is where extra implementations cost most.
Every protocol change needs driving on a console once per engine, and the protocol is a reverse-
engineered target that a firmware update can move. Writing a port is cheap now; proving it on hardware
and keeping it in step is not, and that is the cost that multiplies.

**Rust, not C, for that engine.** Once one engine sits in front of the network on every first-class
platform, its memory safety stops being a nice property and becomes the main one. The engine parses
bytes from the LAN and from the open internet before anything is authenticated: SRCH replies from
whatever answers a broadcast, STUN responses, the UDP 9303 prelude, the Takion handshake. In C, a length
it trusts is a memory-safety bug; in Rust with no `unsafe`, it is at worst a failed session. Beyond
safety, the fit is good on every first-class platform: each has a well-supported Rust target, and there
are mature ways to reach a Rust library from Swift, C#, Kotlin and C++.

What the C core got right carries over rather than being lost: the threading contract (one host-owned
thread, no threads or locks inside), a hot path that does not allocate, caller-owned lifetimes, and the
test vectors. The Rust engine is designed to present the same contract, so a host cannot tell which
engine it is linked against.

## The layer map

| Layer | Windows | macOS · iOS · iPadOS | Linux | Android |
|---|---|---|---|---|
| Wire protocol: SRCH, wake, pairing, `/sess`, Takion, crypto, stream, FEC, STUN, the connect sequence | Rust engine | Rust engine | Rust engine | Rust engine |
| Session lifecycle: reconnect, watchdog, stats, bitrate policy | .NET (`Ripcord.Client`) | Swift (`SessionController`) | the shell's language | Kotlin |
| What each state means to a person | .NET (`Ripcord.Presentation`) | Swift | the shell's language | Kotlin |
| Cloud tier: OAuth, console list, cloud wake, signaling | .NET (`Ripcord.Cloud.Halyard`) | Swift (`RipcordKit/Cloud`) | open, see below | open, see below |
| mDNS | .NET (`Ripcord.Core.Net`) | Bonjour | Avahi | `NsdManager` |
| Video, audio, input | D3D12 + MFT, WASAPI, GameInput + HID | VideoToolbox, Core Audio, GameController | open (VA-API or Vulkan Video, PipeWire, SDL or hidraw) | MediaCodec, AAudio, `InputDevice` |
| Credentials | DPAPI | Keychain | Secret Service | Keystore |
| UI | WinUI 3 | SwiftUI/AppKit/UIKit | open (Qt or GTK) | Jetpack Compose |

- **Windows stays a .NET app.** The shell, Presentation, the cloud tier, media and input interop and the
  session lifecycle stay where they are. What moves is the roughly 13,000 lines of protocol engine under
  `Ripcord.Protocol.Halyard*`, behind seams that already exist. Windows moves once, straight onto the
  Rust engine; it never passes through the C core.
- **mDNS is not protocol.** It is DNS-SD, and every platform has a native service for it that handles
  permissions and interface changes better than a hand-written responder would. The engine keeps the
  SRCH probe, which is the console's own protocol.

## The engine

### Shape

A Cargo workspace at **`engine/`**, beside `libripcord/`:

| Crate | What it is | `unsafe` |
|---|---|---|
| `ripcord-proto` | The protocol as pure state machines: parsers, codecs, crypto, Takion, stream, FEC, pairing, STUN, the connect sequence. **Sans-IO**: it owns no socket, reads no clock and starts no thread. The host (the crate below) hands it datagrams and the current time; it hands back datagrams to send, frames and events | `#![forbid(unsafe_code)]` |
| `ripcord-net` | The I/O driver: sockets (`std::net`), deadlines, the connect and pump loop that runs `ripcord-proto` on the caller's thread | forbidden |
| `ripcord-ffi` | The C ABI. Every exported function, the generated header `ripcord.h`, `staticlib` and `cdylib` outputs | the only crate that may use it |
| `ripcord-kat` | Runs the `.kat` files `ProtocolLab vectors` generates, unchanged | forbidden |
| `fuzz/` | `cargo-fuzz` targets, one per surface, mirroring `libripcord/tests/fuzz/` | — |

**Why sans-IO**, when the ports no longer force it: it is the right design on its own merits. It is what
lets the engine be tested deterministically (the scripted console, time controlled by the test), it keeps
the "hosts own the threads" contract honest by construction, and it keeps all I/O in one small crate that
is easy to review. It also happens to be what would let a port adopt the engine later without the engine
changing, but that is a side effect, not the reason.

**`std` is allowed** everywhere. Nothing in this plan constrains the engine to `no_std`.

### Dependencies

Few, and each one argued for. `cargo-deny` enforces the list and the licences.

- **Symmetric crypto: RustCrypto** (`aes`, `aes-gcm`, `ghash`, `sha2`, `hmac`, `hkdf`). Pure Rust,
  maintained, and able to use AES-NI, PCLMULQDQ and the ARMv8 crypto extensions where the CPU has them.
  This retires the C core's hand-written AES and GCM.
- **Key agreement: the operating system's, where it has one.** The curve arithmetic is the one primitive
  the project has always declined to own (`rc_ecdh.h` says why), and the same reasoning prefers the
  OS vendor's implementation over a library's where both exist. `ripcord-proto` defines an `Ecdh` trait
  and the host supplies the backend:

  | Platform | Backend |
  |---|---|
  | macOS, iOS, iPadOS | CryptoKit, as `macos-plan.md` already decided |
  | Windows | CNG (`BCrypt`) |
  | Linux, Android | RustCrypto (`p256`, `p521`) |
  | Tests and fuzzing | RustCrypto, so the suite runs anywhere |

  Every backend runs the same known-answer tests against the .NET vectors in CI, on its own platform,
  as `ecdh_test.c` does for CryptoKit today. A backend that has not passed them on its platform is not
  used there.
- **No async runtime.** The engine does not use Tokio or any other executor; hosts own their threads, and
  the host's own concurrency model is where async belongs.
- **Nothing else by default.** `socket2` if `std::net` falls short on socket options.

### The interop constants

The rule in `libripcord/README.md` holds unchanged: the bundle in
`src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` is the only committed copy. `ripcord-proto`'s
`build.rs` reads that one file and generates a Rust module into `OUT_DIR`, as `tools/gen_constants.py`
does for C. No copy is checked in.

The `Client-Type` value is the one constant that already has two homes (`HalyardRegistrationMessage` and
`halyard_regist_message.h`), and `BundledInteropConstantsTests` asserts that they agree. The Rust engine
adds a third, and `CLAUDE.md` and that test have to name it in the same change, as `CLAUDE.md` requires.

### The C ABI

Every host reaches the engine through one C ABI. It starts as **the contract `libripcord` already
exposes**: `halyard_client.h` (connect, pump, the callbacks, the rendezvous calls) plus what the Mac's
`CLibripcord.h` pulls in for discovery, wake, pairing and STUN. Presenting the same contract is what lets
the Mac switch engines by relinking.

The rules:

1. **The header is generated** by `cbindgen` from `ripcord-ffi`. Nobody writes or edits `ripcord.h` by
   hand.
2. **Host bindings are generated from that header where a generator exists:** Swift imports it directly
   through a module map, as it does `CLibripcord.h` today; .NET uses ClangSharp or `csbindgen`; Kotlin's
   route (JNI stubs or UniFFI) is decided in the Android plan.
3. **API stability, not ABI stability.** Every host lives in this repository and ships the engine built
   from the same commit. The rule is that every host builds in CI whenever `ripcord.h` changes.
4. **A layout check at load.** `ripcord_api_version()` and `ripcord_struct_size(id)` for every struct
   that crosses the boundary. Bindings compare them against their own view and refuse to run on a
   mismatch.
5. **No panic crosses the boundary.** Every export runs inside `catch_unwind`. A panic ends that session
   with an error the host can report; it never unwinds into Swift or .NET, and never aborts the app.
   Panics are also treated as bugs: parsers return errors, arithmetic on wire values is checked, and the
   fuzzers flag any panic.
6. **The threading contract is unchanged.** One host-owned thread calls connect and pump, the engine
   creates no threads and takes no locks, and everything the host tells the session is pulled through a
   callback on that thread.
7. **Buffers passed to a callback are borrowed;** the host copies what it keeps.
8. **Sockets cross as `intptr_t`,** because `SOCKET` is pointer-sized on Windows.
9. **Logging is per session,** not process-wide as `rc_log_set_sink` is today.

### Builds

| Host | How | Output |
|---|---|---|
| Apple | `cargo` for `aarch64-apple-darwin`, and the iOS device and simulator targets later, packaged as an XCFramework the Xcode project links | static |
| Windows | a small MSBuild project in `Ripcord.slnx` that runs `cargo` for `x86_64-pc-windows-msvc` or `aarch64-pc-windows-msvc` and places the DLL where `$(RipcordNativePlatform)` expects it | `ripcord.dll` per architecture |
| Linux | `cargo` | static or shared |
| Android | `cargo-ndk`, per ABI | `.so` |

The Windows developer still runs `msbuild Ripcord.slnx`; `cargo` is a prerequisite, the way the C++
toolchain is today.

CI for the workspace: `cargo test` (which includes the vector runner), `clippy -D warnings`, `cargo-deny`,
Miri over `ripcord-ffi`'s tests, and a coverage-guided fuzz run that keeps its corpus between runs. The
C core's fuzzing never had a first real run; the Rust engine's fuzzing starts on day one.

## The reference implementation

`libripcord`'s tests read known-answer vectors that `ProtocolLab vectors` generates from the .NET code.
**Those vectors cannot be committed**, because they are derived from the bundled constants
(`libripcord/README.md`, "The interop constants are generated, never copied"). A vector therefore exists
only while .NET code exists to generate it, and freezing vectors as files is not an option. The Rust
engine reads the same `.kat` files, so this carries over as it is.

The rule:

- **Derivations keep two implementations.** Key schedules, KDFs, field ciphers, registration wraps,
  message codecs: anything where a byte-exact answer is computed from constants. The .NET side stays the
  reference for these, and new ones are written in both, with vectors, as now.
- **Sequencing does not.** Connect ordering, retry budgets, the rendezvous choreography, pump timing.
  The engines are ports of each other there and share their mistakes; what catches errors is a console.
  New sequencing lands in the Rust engine only, tested against the scripted console, the gitignored
  captures where they exist (self-skipping, like the `Live*VectorTests`), and hardware.

**During the transition, the C core is a free second implementation of everything**, sequencing included.
Differential testing uses that: the same scripted-console scenario and the same fuzz input run through
both engines, and any difference in output is a finding.

After Windows moves (Phase 4), the .NET protocol projects stop shipping in the app. They stay as a
test-only reference for `ProtocolLab` and the vector generator, cut down to what those two need.

## The phases

Each phase has an exit criterion. A phase is done when that criterion is met, not when its code is
written.

### Phase 0 — record the direction

*Done 2026-09-25.* See the journal.

- `CLAUDE.md` and `architecture.md`: the Rust engine is the engine the first-class clients run; the .NET
  protocol code is the reference for derivations; `libripcord` is the console ports' core.
- `docs/README.md`: a row for this file.
- `macos-plan.md`: a note under "Why the C core" pointing here.
- `libripcord/README.md`: its new scope (see "The console ports").
- `ROADMAP.md`: a section for this plan, holding its open items.

**Exit:** the documents agree with each other.

### Phase 1 — the spike, and the gate

A vertical slice through the hottest path: **stream packet crypto, the demuxer, and FEC**, in
`ripcord-proto`, exported through `ripcord-ffi`.

- It passes `stream-crypto.kat` and the stream tests' scenarios.
- It is benchmarked the way `ripcord-lab bench` measured the C core: **8.1 µs per packet** (GMAC verify plus
  CTR decrypt, 1,426 bytes) on an M4 Max. It is also measured on Windows x64 and ARM64, where the C core
  never was.
- It links into the Mac lab through the generated header, and into a .NET test harness through the
  generated bindings. Both are the real integration paths, not stand-ins.
- Binary size is recorded.

**Exit, and the gate:** the vectors pass; per-packet cost is at or below the C core's on the same machine;
both hosts link and call it. If the gate fails, the fallback is the C core as the engine: the same phases,
with Phase 2 becoming "close the C core's gaps" and Phase 3 falling away.

### Phase 2 — the engine at parity

Port bottom-up in the order the C core layers itself: crypto and the Halyard derivations, then Takion,
the stream, discovery and wake, `/sess` and registration, STUN and the 9303 transport, the rendezvous
route, and last the connect sequence behind the `halyard_client` contract.

The crypto layer includes the `Ecdh` backends: CryptoKit carried over from the Mac, CNG written new, and
RustCrypto, each run against the vectors by a CI job on its own platform.

Each layer is done when its `.kat` files pass, its fuzz target is running, and differential fuzzing
against the C core has run clean. The scripted console (`libripcord/tests/fake_dgram_console.h`) is
ported to Rust early, since every layer above Takion is tested against it, and is exported by
`ripcord-ffi` behind a test feature so the Mac and .NET test suites can drive it too.

Parity is measured, not asserted. The matrix is every capability the Windows client uses today, which
engines provide it, and how each has been verified (vectors, captures or hardware). The gaps already
known, from `ROADMAP.md` and `halyard_client.h`, are closed in Rust only:

- senkusha echo and MTU probes inside the connect sequence;
- CORRUPT_FRAME;
- sending CONNECTION_QUALITY (the bitrate policy stays in the host, as `AdaptiveBandwidthController`
  does on Windows, but the engine needs a call to send its answer);
- discovery and wake inside the sequence;
- a round-trip time in the stats, which `SessionStatistics.RoundTripTimeMs` needs and the C core's stats
  do not carry;
- the rendezvous route from another network (on the same LAN it has streamed 1080p from the Mac,
  journal 2026-09-25);
- PS4's account route `[X]`;
- the account-id encoding, where the C core wraps silently past `UINT64_MAX` and .NET falls back to
  UTF-8.

**Exit:** every row in the matrix is either in the Rust engine and verified at least as well as .NET's,
or recorded as deliberately host-side. The Mac lab streams through the Rust engine with the figures it
recorded on the C core (1080p60, 0 lost of 2,404, first picture in under 5 s).

### Phase 3 — the Mac switches engines

The Mac keeps building on `libripcord` until Phase 2's exit. Protocol work it needs before then lands in
C as it does today, and the port to Rust picks it up; after Phase 1 passes, new protocol work that the Mac
does *not* need immediately lands in Rust first.

The switch itself is a relink: `CLibripcord.h` is replaced by the generated `ripcord.h`, and the Xcode
project links the XCFramework instead of compiling `libripcord/`. For a period, `ripcord-lab` can run
either engine against the same console, which is the comparison that matters most.

**Exit:** the Mac's parity checklist in `macos-plan.md` passes on the Rust engine, on hardware.

**Where it stands (2026-09-26).** The relink is done, ahead of Phase 2's formal exit, at the project
owner's direction: RipcordKit reaches the protocol only through `ripcord.h`, and the Mac project no longer
compiles `libripcord/`. The Rust engine links as a static library built by cargo from a build phase; the
XCFramework waits for the iOS targets. Instead of one lab that runs either engine, the comparison run
uses a lab built from the commit before the relink, which is the C core's last Mac build, against the
same console. The exit criterion, the parity checklist on hardware, is still open.

### Phase 4 — Windows onto the engine

**After the Windows 1.0, not before.** 1.0's scope was settled on 2026-09-11, and changing the engine
under it would put every hardware check done for it back to zero. This is a 1.x change.

The new project is **`src/Ripcord.Protocol.Halyard.Native`**. It holds the generated bindings and native
implementations of the seams `Ripcord.Presentation` already defines, alongside the managed ones in
`Ripcord.Presentation.Halyard`. Nothing above that layer changes: not `Ripcord.Presentation`, not the
WinUI shell, not the media or input interop.

How a session is hosted:

- A dedicated thread per session calls connect, then pump, waiting on the session's sockets between
  calls.
- Callbacks are `[UnmanagedCallersOnly]` statics; the `user` pointer is a `GCHandle` to the session object.
- Video and audio callbacks copy into pooled buffers and publish to the observables `IStreamingSession`
  already exposes, so the decode pipeline sees the same `EncodedVideoFrame` it does today.
- `poll_input` reads the latest state the input sink published, swapped in atomically; `poll_commands`
  reads an `Interlocked` flags word; cancellation comes from the `CancellationToken`.
- The session's storage is `NativeMemory.AlignedAlloc`, outside the GC.

One seam at a time, lowest risk first, each behind a switch with the managed implementation still
present, and each driven on hardware before the next starts:

| Order | Seam | Replaces | Stays in .NET |
|---|---|---|---|
| 1 | `IConsoleScanner`, `IConsoleReachabilityProbe`, `IConsoleWakeCoordinator` | the SRCH probe and LAN wake | mDNS; cloud wake |
| 2 | `IConsoleRegistrar` | PIN pairing | the pairing UI and the credential store |
| 3 | `IStreamingSessionSource`, LAN route | `HalyardStreamingSession`, Takion, stream crypto | `SessionController`, `AdaptiveBandwidthController`, media, input |
| 4 | `IAccountConsolePairing` and the account route (`AccountRouteSession`, `HalyardAccountConsoleSession`) | the 9303 transport, rendezvous, account registration | `Ripcord.Cloud.Halyard`, which drives the engine's rendezvous calls the way `RipcordKit/Cloud` does |

The switch is a developer setting (`RIPCORD_ENGINE=managed|native`), never a user-facing control,
because the standing rule is that no user-visible control is inert. `ProtocolLab` gains the same switch
(`--engine native`), so a capture replay or a live connect can run through both engines and be compared.

Check first: `src/Ripcord.App/Pages/SessionPage.xaml.cs` imports three `Ripcord.Protocol.Halyard*`
namespaces, and the App project references those assemblies directly. Either the usings are stale or
the shell has a dependency that goes around the seams; either way it is resolved before step 3.

**Exit:**

- The 1.0 hardware checklist passes on the native engine, on x64 and ARM64.
- The recorded LAN figures hold: 1080p60 at 0.4% loss and about 23 Mb/s, with demux to present at or
  under 18 ms.
- One release ships with native as the default and managed as the fallback.

**Then:** the managed engine leaves the App's graph, and the `Ripcord.Protocol.Halyard*` projects are
reduced to what `ProtocolLab` and the vector generator need.

### Phase 5 and on — iOS and iPadOS, Linux, Android

Each gets its own plan file, in the shape of `macos-plan.md`, when it starts.

- **iOS and iPadOS** extend RipcordKit to a multi-platform framework. *Started 2026-09-27* in
  [`ios-plan.md`](ios-plan.md), with Apple TV and a watch remote beside them: RipcordKit builds for iOS and tvOS,
  over the engine built per SDK rather than an XCFramework for now, and one app target runs on all three.
- **Linux** starts with two decisions: the toolkit (GTK4 and libadwaita, or Qt and Kirigami) and the
  shell's language. Neither affects the engine or any phase before Linux. If the shell is Rust, it can
  depend on `ripcord-net` and `ripcord-cloud` directly rather than through the C ABI.
- **Android** starts with one: JNI stubs over the C ABI, or UniFFI. Everything above the engine is
  Kotlin, and the cloud tier is `ripcord-cloud`.

## The console ports

**The ports do not shape this architecture.** Retro consoles are a much smaller audience than the
first-class platforms, and their toolchains are the least stable part of the project, so they do not get
a veto over the engine's language or design.

- **`libripcord` becomes the ports' core.** It keeps its tests, its fuzz harnesses and its CI job. It
  gets fixes, and new protocol work only when a port needs it and someone is working on that port.
  It no longer promises parity with the first-class clients.
- **The ports keep their own version lines.** `ps3-v1.0` stays published as the reference build of the
  PS3 port as it was made.
- **Moving a port onto the Rust engine is a later, per-port decision.** The 3DS and Vita have community
  Rust targets. The PS3 has had a tier 3 target (`powerpc64-sony-ps3`) since 5 September 2026,
  with `core` and `alloc` only, nightly, and a post-link patcher. Because the engine is sans-IO, a port
  would supply its own I/O and link the protocol crate. Whether that is worth doing is for when a port
  is next worked on, not now.
- **When the last port moves or is retired, `libripcord` is retired.** Until then it stays.

## What each client must agree on

With the session lifecycle and the Presentation layer written again in each ecosystem, what has to stay
shared is **what is true**, not the code that says it. `Ripcord.Presentation` holds lessons learned on
hardware: "asked remotely" is not "woken", a failure names its cause, and a paused scene is not a dead
console.

- **A behaviour contract, `docs/session-contract.md`.** It maps the engine's facts (the stages, the end
  reasons, the stats clocks) to what a person must be told, as a table with no wording, since wording is
  each platform's own. It is the file a new client is written from.
- **One scripted console for every client's tests.** The Rust port of the scripted console, exported
  behind `ripcord-ffi`'s test feature, drives the same scenarios in every host's suite. A scenario added
  once (a refused passcode, a console that closes mid-stream) is then checked by every client.

## The cloud tier

This is the duplication this plan does not remove. It is about 2,700 lines of HTTPS and JSON, it changes
rarely, and each platform's own sign-in tools matter to how the app feels. Windows keeps
`Ripcord.Cloud.Halyard`; Apple keeps `RipcordKit/Cloud`, which is done.

**Linux and Android share a `ripcord-cloud` crate in the engine workspace.** That makes three cloud
implementations rather than four, and keeps JSON parsing in Rust. Its test is the Mac's: request bodies
compared byte for byte against what the .NET code sends.

The crate does the HTTPS, the JSON, the push WebSocket and the signaling. It does not own sign-in: the
OAuth redirect is shown by each shell with its platform's own tool (a Custom Tab on Android, the portal
or the default browser on Linux), and the crate receives the result. It keeps to the engine's rule of no
async runtime, so its HTTP and WebSocket clients are blocking ones run on a host thread. It is built when
the first of Linux or Android starts, not before.

## Risks

- **Rewriting a core that works.** The C core reached hardware parity on the Mac this week. The mitigations
  are the gate in Phase 1, the same test vectors, differential testing against the C core, and the Mac
  switching only when Phase 2 exits.
- **Two engines during the transition.** For a while, the Mac's in-flight protocol work lands in C and is
  then ported. The rule in Phase 3 keeps that window short.
- **Windows regressions in a client that already works.** This is why Phase 4 comes after 1.0, moves one
  seam at a time, keeps both engines switchable and has `ProtocolLab` A/B runs.
- **Sequencing loses its second implementation** once the C core is no longer maintained in step. That
  is accepted deliberately: the second implementation was never independent. The scripted console and
  hardware runs replace it and have to keep up with new work.
- **The C ABI becomes the most depended-on file in the project.** Rules 1 to 4 exist for this.
- **Panics as a denial of service.** Safe Rust turns a trusted-length bug into a panic instead of memory
  corruption, which is the point, but a panic reachable from the network still ends a session. Rule 5 and
  the fuzzers treat any panic as a bug.

## Clean-room

Nothing here changes the rules. Rewriting this project's own C and C# in Rust is porting within one
project, which `CLAUDE.md` permits explicitly. Other projects' implementations of this protocol stay
out, in Rust as in any other language, and new protocol behaviour is still derived from this project's
own captures and spec before it is written anywhere.

## Decisions (settled 2026-09-25)

| Question | Decision |
|---|---|
| What shapes the architecture | **The first-class platforms**: Windows, macOS and Linux now; iOS, iPadOS and Android later. The console ports do not |
| The engine | **One engine for every first-class client, in Rust**, sans-IO, with `std`. Subject to the Phase 1 gate; if the gate fails, the C core is the engine |
| What the ports get | **`libripcord`**, limited to fixes and port-driven work. Moving any port to the Rust engine is decided per port, later. `ps3-v1.0` stays published |
| When Windows moves | **After 1.0**, as a 1.x change, straight onto the Rust engine, one seam at a time |
| When the Mac moves | **At Phase 2's exit**, by relinking. It keeps building on `libripcord` until then |
| What keeps two implementations | **Derivations** keep a .NET reference with vectors; **sequencing** lives in the Rust engine only |
| Symmetric crypto | **RustCrypto** |
| Key agreement | **The OS's where it has one**: CryptoKit on Apple, CNG on Windows; RustCrypto on Linux and Android. Each backend passes the .NET vectors on its own platform in CI |
| mDNS | **Each platform's native service** |
| The Linux and Android cloud tier | **One shared crate, `ripcord-cloud`**, built when the first of the two starts |

**Deferred, on purpose:**

| Question | Decided in |
|---|---|
| Kotlin's route to the engine: JNI stubs or UniFFI | The Android plan |
| The Linux toolkit (GTK4 and libadwaita, or Qt and Kirigami) and the shell's language | The Linux plan |

Neither affects the engine, its C ABI, or any phase before that platform starts.
