# engine — the Rust protocol engine

The protocol engine the first-class clients are moving to. Why it exists, what it will cover and in what
order are in [`docs/engine-plan.md`](../docs/engine-plan.md). This file covers the tree, the build and
where Phase 1 stands. What is still open is in [`ROADMAP.md`](../ROADMAP.md).

**Status: Phase 1, the spike.** The stream plane only: packet framing, the stream key schedule, per-packet
crypto, Cauchy Reed-Solomon FEC and the demuxer, ported from [`libripcord/stream/`](../libripcord/stream/).
Nothing here talks to a console yet. The Mac and Windows clients still run their existing engines; the
Mac lab links this one alongside the C core, only for the benchmark.

## Layout

| Path | What it is | `unsafe` |
|---|---|---|
| `ripcord-proto/` | The protocol as sans-IO state machines. No sockets, no clock, no threads | forbidden |
| `ripcord-ffi/` | The C ABI: every export. Its `build.rs` generates `ripcord.h` (cbindgen) and `NativeMethods.g.cs` (csbindgen) into `target/include/` | the only crate that uses it |
| `ripcord-kat/` | Runs the `.kat` files `ProtocolLab vectors` generates, unchanged | forbidden |
| `hosts/dotnet/` | The .NET harness: the engine through its generated C# bindings, a differential run against the managed engine, and the benchmark on both | — |
| `deny.toml` | The dependency policy `cargo deny check` enforces | — |

`ripcord-net` (sockets and the pump loop) and `fuzz/` (cargo-fuzz, which needs nightly) arrive with
Phase 2. Until then, `demux::tests::random_packets_never_panic` is a stable-Rust sweep of hostile
packets.

The generated bindings are never committed. Nobody edits them: change `ripcord-ffi/src/lib.rs`, and
the next `cargo build` rewrites both.

## Building and testing

A stable Rust toolchain (`rustup`), and the .NET 10 SDK for the vectors and the harness. From the
repository root:

```sh
# The known-answer vectors, generated from the .NET reference (never committed)
dotnet run --project tools/Ripcord.ProtocolLab -- vectors

cd engine
cargo test --workspace --release      # unit tests, and the vector runner against stream-crypto.kat
cargo clippy --workspace --all-targets -- -D warnings
cargo deny check
cargo run --release --example packet_bench -p ripcord-proto

# The engine from .NET: layout check, differential against HalyardPacketCrypto, demux callbacks, bench
dotnet run -c Release --project hosts/dotnet/Ripcord.Engine.Harness
```

Without the vectors, the vector test prints `SKIP` and passes, like the .NET suite's capture-backed
tests. `cargo run -p ripcord-kat` fails instead, because running it without vectors is a mistake.

The Mac lab (`src/Ripcord.Mac`) builds this engine itself: RipcordKit's "Build the Rust engine" phase
runs cargo, and `ripcord-lab bench` times both engines from the same Swift workload.

### On Windows, for the figure the gate still needs

Phase 1 wants the per-packet cost on Windows x64 and ARM64, where the C core was never measured. On
each machine, with `rustup` and the .NET 10 SDK installed:

```sh
dotnet run --project tools/Ripcord.ProtocolLab -c Release -- vectors
cd engine && cargo test --workspace --release
dotnet run -c Release --project hosts/dotnet/Ripcord.Engine.Harness
```

The harness prints the Rust engine and the managed engine side by side. The managed engine is what the
Windows client ships today, so on Windows that pair is the comparison that matters.

## Phase 1, measured (2026-09-25, M4 Max, macOS 27)

| What | Result |
|---|---|
| `stream-crypto.kat` | 65 of 65: all 9 `gmac`, 4 `streamkdf`, 12 `packetnonce` and 12 `packettag` lines |
| Differential against `HalyardPacketCrypto` | 80 key positions × 4 checks (CTR, both seal directions, the control AAD rule, tamper), across rotation windows and the 32-bit edge |
| Per packet, one Swift harness for both engines (`ripcord-lab bench`) | C core **7.71–8.11 µs**, Rust **0.53–0.57 µs** |
| Per packet, from .NET | Rust through P/Invoke **0.67–0.71 µs**, managed `HalyardPacketCrypto` 16.1–17.3 µs |
| Mac lab | Links through the generated header; the RipcordKit tests and EcdhKat still build and pass |
| .NET | Links through the generated bindings: `[UnmanagedCallersOnly]` callbacks, a `GCHandle` user pointer |
| Size | `libripcord.dylib` 386 KB stripped. `ripcord-lab` grows from 1.10 MB to 2.35 MB stripped, which is more than the engine's own size and still to be explained |

All three benchmarks run one workload: `PacketCryptoBenchmark.swift`'s packets, 1,426 bytes each,
spaced so that every packet lands in its own GMAC rotation window. That is the worst case. At wire
spacing Rust takes 0.54 µs (`packet_bench`), because a rotation window then serves many packets.

Most of the gap is hardware AES and PMULL, which RustCrypto detects at run time, against the C core's
portable table implementation. The rest is structure. The CTR cipher borrows a key schedule expanded
once, where the C core expands the key on every packet. The GMAC cache is keyed by rotation window,
where the C core derives the key with a SHA-256 on every packet just to check its cache.
