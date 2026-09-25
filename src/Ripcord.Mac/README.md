# Ripcord for Mac

The macOS client, on macOS 26 and Apple Silicon. What it is, what it is built on and in what order are
in [`docs/macos-plan.md`](../../docs/macos-plan.md). This file covers the tree and the build.

**Status: the engine spike's foundation.** The protocol core builds and links, key agreement runs on
CryptoKit and matches the .NET vectors, and `ripcord-lab` can discover consoles on the LAN. There is no app
yet.

## Layout

| Path | What it is |
|---|---|
| `Ripcord.xcodeproj` | The project. Every source folder is an Xcode *synchronized* folder, so adding a file never touches the project file |
| `Config/` | Every build setting, in xcconfig files. The project file holds none of its own |
| `Libripcord/` | The Mac's half of the C core: the platform seam (`rc_platform_darwin.c`) and the module map that lets Swift import the core |
| `RipcordKit/` | The Swift layer over the core. It imports the C module *internally*, so no C type reaches its callers |
| `RipcordKitTests/` | Swift Testing suites for RipcordKit |
| `RipcordLab/` | `ripcord-lab`, a command-line driver against a real console: the Mac counterpart of `tools/Ripcord.ProtocolLab` |
| `EcdhKat/` | `libripcord-ecdh-kat`: the core's own `tests/ecdh_test.c`, unmodified, linked against the CryptoKit backend |

## Two decisions the build rests on

**The core is compiled from where it lives, not copied.** The `Libripcord` target's sources are
`libripcord/` itself, through synchronized folders that reach out of this tree. Nothing under `src/Ripcord.Mac`
is a copy of core code.

**An Xcode project, not a Swift package, and that was measured.** A package cannot hold the core:

- SwiftPM refuses a target whose sources lie outside the package root.
- A symlink inside the package would get past that, but it trips the published-tree sweep and becomes a
  plain file on a Windows checkout.
- The interop constants have to be *generated* at build time from the one committed bundle, and on this
  toolchain SwiftPM will not compile a C file a plugin generates. It reports "C source file generation
  not enabled".

An Xcode project does all three: synchronized folders reach the core, and a Run Script phase generates
`halyard_v1_constants.g.c` into `DERIVED_FILE_DIR`, where a file reference compiles it. The app and its
extensions need a project anyway.

**Key agreement is CryptoKit's.** `Libripcord` is built with `RC_ECDH_EXTERNAL_BACKEND`, and
`RipcordKit/Crypto/CryptoKitECDH.swift` supplies the five backend functions under their C names. See
`libripcord/crypto/rc_ecdh.h` for the contract, and `EcdhKat` for the proof that it holds.

## Building

Xcode 26 or later, and Python 3 for the constants generator (the one Xcode's command-line tools install is
enough). From the repository root:

```sh
# The known-answer vectors, generated from the .NET reference (needs the .NET 10 SDK)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

cd src/Ripcord.Mac
xcodebuild -project Ripcord.xcodeproj -scheme RipcordLab -derivedDataPath build build
xcodebuild -project Ripcord.xcodeproj -scheme EcdhKat    -derivedDataPath build build
xcodebuild -project Ripcord.xcodeproj -scheme RipcordKit -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath build test

cd ../..
src/Ripcord.Mac/build/Build/Products/Debug/libripcord-ecdh-kat libripcord/tests/vectors/session-crypto.kat
src/Ripcord.Mac/build/Build/Products/Debug/ripcord-lab check
src/Ripcord.Mac/build/Build/Products/Debug/ripcord-lab discover
```

Or open `Ripcord.xcodeproj` in Xcode. The shared schemes run from the repository root, so the lab and the
KAT find their paths.

If every `xcodebuild` run prints pages about `DVTCoreDeviceCore` or "CoreSimulator is out of date", the
install's device support is behind Xcode itself. It does not affect a macOS build. `xcodebuild
-runFirstLaunch` normally brings it up to date.
