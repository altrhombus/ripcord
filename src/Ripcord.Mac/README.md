# Ripcord for Mac

The macOS client, on macOS 26 and Apple Silicon. What it is, what it is built on and in what order are
in [`docs/macos-plan.md`](../../docs/macos-plan.md). This file covers the tree and the build.

**Status: on the Rust engine (Phase 3 of [`docs/engine-plan.md`](../../docs/engine-plan.md)).** RipcordKit
reaches the protocol only through the engine's generated header: the connect sequence, discovery, wake,
registration and the account seed. Key agreement runs on CryptoKit behind the engine's backend table and
matches the .NET vectors, and `ripcord-lab` can discover, pair, wake and connect. The C core is no longer
built here. There is no app yet.

## Layout

| Path | What it is |
|---|---|
| `Ripcord.xcodeproj` | The project. Every source folder is an Xcode *synchronized* folder, so adding a file never touches the project file |
| `Config/` | Every build setting, in xcconfig files. The project file holds none of its own |
| `RipcordEngine/` | The module map for the Rust engine's generated header (`engine/target/include/ripcord.h`), read in place |
| `RipcordKit/` | The Swift layer over the engine. It imports the engine's C module *internally*, so no C type reaches its callers |
| `RipcordKitTests/` | Swift Testing suites for RipcordKit, including whole sessions against the engine's loopback consoles (`EngineClientTests`, `EngineRendezvousTests`) and CryptoKit against the .NET vectors (`EngineKeyAgreementTests`) |
| `RipcordLab/` | `ripcord-lab`, a command-line driver against a real console: the Mac counterpart of `tools/Ripcord.ProtocolLab` |

## Decisions the build rests on

**The engine is built from where it lives, twice.** RipcordKit's "Build the Rust engine" phase runs cargo
in `engine/`: a shipping library without `test-support` in `target/shipping`, which the lab links and an
app target will, and the test library, which only RipcordKitTests links. Every executable links its
static archive by full path, and CI checks the shipping build carries no test exports. The header is generated there and
read in place; nothing under `src/Ripcord.Mac` is a copy of engine code or of the interop constants, which
the engine generates from the one committed bundle.

**An Xcode project, not a Swift package.** It was first chosen because a package could not compile the C
core from outside its root or compile a generated C file; the app and its extensions need a project
anyway, and that reason stands.

**Key agreement is CryptoKit's**, supplied to the engine through `RipcordEcdhBackend`
(`RipcordKit/Engine/CryptoKitEngineECDH.swift`), and checked against the .NET vectors by
`EngineKeyAgreementTests` before it is used.

**Pairings are kept by the host.** The lab keeps them in an owner-only `pairings.json`
(`PairingFileStore`); the app will keep them in the Keychain (`KeychainPairingStore`). The lab imported the
C core's old `pairing.txt` once, leaving it as `pairing.txt.imported`.

## Building

Xcode 26 or later, and a stable Rust toolchain (`rustup`): RipcordKit's "Build the Rust engine" phase runs
cargo, which it looks for in `~/.cargo/bin` and Homebrew's `rustup` prefix. From the repository root:

```sh
# The known-answer vectors, generated from the .NET reference (needs the .NET 10 SDK)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

cd src/Ripcord.Mac
xcodebuild -project Ripcord.xcodeproj -scheme RipcordLab -derivedDataPath build build
xcodebuild -project Ripcord.xcodeproj -scheme RipcordKit -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath build test

cd ../..
src/Ripcord.Mac/build/Build/Products/Debug/ripcord-lab check
src/Ripcord.Mac/build/Build/Products/Debug/ripcord-lab discover
```

Or open `Ripcord.xcodeproj` in Xcode. The shared schemes run from the repository root, so the lab finds its
paths.

If every `xcodebuild` run prints pages about `DVTCoreDeviceCore` or "CoreSimulator is out of date", the
install's device support is behind Xcode itself. It does not affect a macOS build. `xcodebuild
-runFirstLaunch` normally brings it up to date.
