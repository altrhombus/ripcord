# CLAUDE.md

Guidance for Claude Code working in this repository. The reasoning behind these rules lives elsewhere and is
linked; this file is the rules.

## What this is

Ripcord is a from-scratch, independent PlayStation Remote Play client. The protocol was derived independently
from the project's own captures and from static and dynamic analysis of **our own
installed copies** of the vendor client. Keep that possession framing wherever this is restated; `NOTICE` lists
the methods. No vendor code is reproduced. About 4 KB of interoperability constants **is** bundled,
deliberately: see "Bounded exception 1" below. Read "Independent-implementation rules" before touching anything protocol- or
crypto-related.

- **What's left:** [`ROADMAP.md`](ROADMAP.md). When work lands, record it in [`docs/journal.md`](docs/journal.md)
  and take it off the roadmap.
- **Which document answers what:** [`docs/README.md`](docs/README.md).
- **How the code is arranged, and why:** [`docs/architecture.md`](docs/architecture.md).
- **The cross-machine handoff:** if `docs/protocol/captures/HANDOFF.md` exists, read it at the start of a
  session and update it before the end. It is private and never committed; the repo stays the source of truth.

## Commands

The solution (`Ripcord.slnx`) mixes .NET 10 and native C++/WinRT. The WinUI 3 app needs Windows, the Windows
App SDK and a C++ toolchain; the two native interop `.vcxproj` projects build only with MSBuild. x64 and
ARM64 are both first-class, and either host can cross-build the other (ARM64 needs the VS "C++ ARM64/ARM64EC
build tools" component).

```
msbuild Ripcord.slnx -p:Platform=x64          # or ARM64; run once before dotnet build/run
dotnet run --project src/Ripcord.App/Ripcord.App.csproj
dotnet publish src/Ripcord.App/Ripcord.App.csproj -c Release -p:Platform=x64   # the self-contained release

dotnet test tests/Ripcord.Protocol.Halyard.Tests/Ripcord.Protocol.Halyard.Tests.csproj
dotnet test tests/Ripcord.Presentation.Tests/Ripcord.Presentation.Tests.csproj
dotnet test <suite> --filter "FullyQualifiedName~<Class>.<Method>"

dotnet run --project tools/Ripcord.ProtocolLab -- <command>   # the console harness for protocol work
cd engine && cargo test --workspace --all-features            # the Rust engine (skip ripcord-diff on Windows)
make -C libripcord/tests                                      # the C core
```

- `Directory.Build.props` owns the cross-architecture wiring: `$(RipcordRepoRoot)` and
  `$(RipcordNativePlatform)` (override with `-p:RipcordNativePlatform=<arch>`). Never hard-code an
  architecture into a path.
- After switching branches, a stale incremental XAML build can crash the app at start-up. Rebuild with
  `-t:Rebuild` before blaming the code.
- Tests that need captured ground truth read gitignored fixtures under `docs/protocol/captures/` and skip
  themselves (`SkippableFact`) when it's absent.
- The Apple clients (`src/Ripcord.Mac`) are an Xcode project outside the solution; see its `README.md`.

## Architecture

Dependencies run one way: platform-specific → shared → `Ripcord.Core`. `Ripcord.Core` and `Ripcord.Core.Net`
have zero project references and no PlayStation types; their `ConsolePlatform` enum uses our codenames so the
neutral layer never names a console. A comment explaining a layer above is fine; a `using` or a type is not.

| Project | Holds |
|---|---|
| `Ripcord.Core` | Platform-neutral contracts, settings and paired-console persistence |
| `Ripcord.Core.Net` | TCP/UDP, mDNS, and thin crypto primitives (no key schedule) |
| `Ripcord.Cloud.Halyard` | The PSN REST client: OAuth2, console list, wake, rendezvous |
| `Ripcord.Protocol.Halyard.Common` | The `/sess` codec, stream framing, input mapping, and the crypto seam |
| `Ripcord.Protocol.Halyard.Takion` | The SCTP-over-UDP transport |
| `Ripcord.Protocol.Halyard` | The PS5 session composition point, pairing and registration |
| `Ripcord.Media*`, `Ripcord.Input*` | D3D12 video, WASAPI audio, controllers; `.Interop` is native C++/WinRT |
| `Ripcord.Client` | `SessionController`: the headless session lifecycle |
| `Ripcord.Presentation` | View-models and flows, plain `net10.0`; **no UI framework type may cross into it** (`PresentationPortabilityTests`) |
| `Ripcord.Presentation.Halyard` | The PlayStation backend for it, and `HalyardAppServices.Create` |
| `Ripcord.App` | The WinUI 3 shell, increasingly thin |
| `tools/Ripcord.ProtocolLab` | The harness: drives a real console and replays captures |
| `src/Ripcord.Mac` | The Apple clients on the Rust engine (Mac app, `ripcord-lab`, iPhone, iPad, Apple TV) |
| `libripcord/` | The protocol in portable C99, the console ports' core |
| `engine/` | The Rust engine succeeding it for the first-class clients ([`docs/engine-plan.md`](docs/engine-plan.md)) |

- **The crypto seam.** `IHalyardSessionCrypto` has a no-op stub and the real `HalyardV1SessionCrypto`; swapping
  is a DI change. Prefer this seam-and-stub shape for new unsolved work.
- **.NET is the reference for derivations** (key schedules, KDFs, field ciphers, codecs), and
  `ProtocolLab vectors` generates the known-answer vectors the C core and the engine test against. When porting,
  read both the .NET code and the C port; .NET wins unless C is strictly safer or .NET is wrong
  (`engine/README.md` records which).

### The app layer: four rules

1. **One immutable state record per view-model, recomposed whole.** Derive from `ObservableState<TState>`,
   override `Compose()`, change state only through `Mutate(...)`. No per-property notification, no MVVM
   framework. `ObservableCollection<T>` is the one exception (it keeps gamepad focus alive).
2. **One threading seam.** `IUiDispatcher` is the only marshalling point, and nothing in the layer takes a lock.
   `Mutate` posts when off the UI thread, so decide freshness synchronously and capture the answer; a guard
   evaluated inside a posted closure is a bug. The exception is `ScanSink` in `RipcordAppServices`, at the
   boundary. Where the UI thread meets a device thread outside this layer, use `SpinGate`, never `lock`: a
   contended `lock` on the UI thread pumps messages and XAML fails fast.
3. **A device gets a seam; data does not.** GPU, driver, presenter and native-handle access goes through an
   interface implemented in the front end (`IVideoCapabilitiesProbe`, `IConsoleScanner`, `IShellNavigator`,
   ...); plain values are parameters. Implementations may throw; the portable side degrades per answer.
   Each native class is reached from one owner file (`NativeCapabilityAccessTests`).
4. **The composition root owns construction.** `HalyardAppServices.Create(...)` builds `RipcordAppServices`
   once at start-up. Nothing else `new`s a store, scanner or probe. No container, no `Resolve<T>()`.

## Naming

| Name | Origin | Refers to |
|---|---|---|
| Halyard | Ours | The Sony console backend |
| HalyardLegacy | Ours | Its older console generation |
| Lanyard | Ours, provisional | The Xbox backend, reserved, not implemented |
| Takion | The vendor's own codename, recovered from our binary | The SCTP-over-UDP transport |
| Senkusha | The vendor's own codename, recovered from our binary | The echo/MTU/bandwidth probe |

- Takion and Senkusha are the **one stated exception** to "never use the vendor's own names". Don't extend it
  without recording it here.
- Protobuf message and enum names: rename the vendor's coined ones, keep the plain-English descriptive ones,
  never change field numbers or enum values. `docs/protocol/README.md` has the rule in full.
- Never use vendor symbol names (classes, functions, log strings). Name things for what they do; cite the
  vendor by RVA (`FUN_<rva>`).
- Required on-wire values (hostnames, the `"PS5"` tag, `RP-*` headers, JSON keys) stay verbatim.

## Independent-implementation rules

These are the working discipline. Nothing here is legal advice, and Ripcord makes no claim about how any law applies to it.

When working in `Ripcord.Protocol.Halyard*`, `Ripcord.Cloud.Halyard`, `engine/`, `libripcord/`, `ports/` or
anything crypto-related:

- **Derive only from our own work:** the dated specs in `docs/protocol/`, our own captures, our own binary
  analysis, and public references (RFCs, NIST vectors, platform docs). **Never open, quote or reproduce another
  implementation of these protocols** for implementation detail, constants, byte layouts or naming, and never
  use an AI model as an indirect route to the same. The method is **derive first, then confirm**.
- **"Another implementation" means another project's.** Ripcord's own front ends and ports may port and adapt
  each other freely; citing what a file came from is a courtesy.
- **Laundered provenance is the AI risk.** A model can reproduce another implementation's structure or constants
  unasked. A value you can't trace to a capture, our own analysis or a public reference is not kept: mark it
  `[X]` and derive it. Fluent, confident specificity about wire formats is what recall looks like.
- **Mark uncertainty in place.** `[X]` means assumed, never confirmed against a console; never describe an `[X]`
  value as confirmed. There is no central list: tag the value where it sits. Open research questions go in
  `ROADMAP.md`.
- **Audits are allowed, and separate.** Reading another implementation to compare against ours produces a
  findings report only, kept in the captures folder. It must never supply a fact we lack; a gap it reveals becomes
  an `[X]` to derive.
- **Secrets never get committed.** Raw captures, the lab notebook, our analysis scripts and anything with real
  constants live in the gitignored captures folder, `docs/protocol/captures/`. Keep the leak guard on
  (`git config core.hooksPath .githooks`) and rebuild its denylist (`tools/leak-guard/build-denylist.py`) after
  adding a capture or editing anything in the captures folder.
- **Records carry placeholders from the start.** A journal entry, test, comment or commit message about a
  hardware run never holds a console name, a real address, an account or device id, or a MAC. Use the
  placeholders in `docs/README.md` ("Redaction placeholders"). The labs print redacted by default;
  `--show-identifiers` output is for your terminal only.
- **Name vendor internals by RVA** (`FUN_<rva>`), never by symbol or string name.
- **Keep `docs/protocol-research-log.md` current** as derivation work progresses.

### Bounded exception 1: the v1 interoperability constants

`src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` is committed on purpose: about 4 KB of constant
data (8.7 KB as hex-encoded JSON) in four control KDF tables (a PS5 pair and a PS4 pair), four field context
keys (the registration context key is one of them, stored twice), two registration key tables, two
material-wrap tables, and a byte offset.

- **The test is generic versus personal.** These are identical for every console and account, and the console
  computes against them; `NOTICE` describes them. Anything tied to one console, user or
  account stays in the captures folder, and `BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial`
  enforces that.
- **How each client carries it:** the dotnet client embeds and reads it at run time;
  `libripcord/tools/gen_constants.py` and `engine/ripcord-proto/build.rs` generate tables from it at build
  time, never committed. Inert builds: `-p:BundleInteropConstants=false`, the engine's `interop-constants`
  feature (off with `--no-default-features`), `RIPCORD_BUNDLE_INTEROP_CONSTANTS = NO` on the Apple clients
  (`ripcord_interop_constants_bundled()` reports it), and none yet for the C ports.
- **The inventory includes one more constant:** the 32-byte `Client-Type` value, generic to the application
  and tied to no account or console. It has exactly **two homes**, `HalyardRegistrationMessage.ClientTypeHex`
  and `HALYARD_REGIST_CLIENT_TYPE_HEX` in `libripcord/session/halyard_regist_message.h`.
  `BundledInteropConstantsTests.ClientType_CCoreCopyMatchesTheReferenceImplementation` asserts they agree.
  The Rust engine reads it from the C# source at build time rather than holding a third, and
  `ClientType_RustEngineDerivesItFromTheReference` checks that. A new home goes here and into that test
  together.
- **Don't widen this exception** without amending `NOTICE` and this file together.

### Bounded exception 2: the application OAuth credential

`src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json` holds the vendor desktop client's own
`client_id`/`client_secret`, committed by the owner's decision on 2026-08-07. It came from **our own capture of
our own traffic**; PSN offers no third-party registration, so there is no credential of ours to use.

- It passes the generic-versus-personal test: identical for every user, it authenticates an application, not a
  person. No user credential is ever bundled; the signed-in account's refresh token is encrypted on the user's
  own machine (DPAPI on Windows, the Keychain on the Mac).
- **It is not a protocol value.** A client speaks the protocol without it (a LAN session needs no internet), so
  `NOTICE` describes it separately. Never justify a third bundle by pointing at this one.
- **"Other implementations ship it" is not the rationale** and must never be recorded as one. Our provenance is
  our own capture; any comparison came afterwards, as an audit.
- Omit it with `-p:BundleOAuthClient=false` (or `RIPCORD_BUNDLE_OAUTH_CLIENT = NO` on the Apple clients);
  override it at run time with `RIPCORD_CLIENT_ID`/`RIPCORD_CLIENT_SECRET` or a `client.json`. The committed
  file ships populated; `tools/extract-oauth-client.py` regenerates it from a capture.
- **Don't widen this exception** without amending `NOTICE` and this file together.

## Working style

- Ask before anything outward-facing: pushes, merges, publishing, repo settings. Launching the app is fine;
  driving its UI, touching the console, and changing system-wide settings are the owner's to do or approve.
- Hardware work is batched: prepare the checklist rather than running it.
- History rewrites and destructive git clean-ups are the owner's to run. Prepare and dry-run them.
- Say "the dotnet client", never "the Windows client".
- Commit on a branch, signed off (`git commit -s`), with a `Co-Authored-By` trailer; commit with `TZ=UTC`.
- Say plainly what was run and what wasn't, especially what hasn't met hardware.
- Ripcord sends nothing anywhere but the console and PSN. Ask before adding any other network call.
