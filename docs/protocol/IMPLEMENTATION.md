# v1 Remote Play — implementation handoff

One entry point for building the PS5 Remote Play **v1** (older-protocol, LAN) client from the spec in this
directory. Read this first, then work the spec's §7 checklist top to bottom.

- **The spec:** [`ps5-remoteplay-v1-spec.md`](ps5-remoteplay-v1-spec.md) — authoritative. §0 is the
  test-vector index; §7 is an ordered build checklist. Confidence tags: **[V]** verified byte-for-byte on a
  live capture · **[C]** confirmed from our decompiled binary · **[W]** observed on the wire · **[I]**
  inferred · **[X]** assumed — adopted provisionally and not confirmed against the console. An `[X]` value
  with no accompanying `[C]`/`[V]`/`[W]` tag is **not settled**; treat it as a known risk. There is no
  central list of them, deliberately — a second copy goes stale — so grep the specs for `[X]`;
  [`README.md`](README.md) states the rule.
- **Protobuf:** [`stream_control.proto`](stream_control.proto) + [`bandwidth_probe.proto`](bandwidth_probe.proto)
  — `protoc`-compile for the control-plane bindings.
- **Provenance / clean-room:** [`README.md`](README.md).
- **Where it plugs in:** [`../history/phase1-lan-build-plan.md`](../history/phase1-lan-build-plan.md) — the
  `IHalyardSessionCrypto` seam and the Phase-1 stage table. Everything else in Phase 1 (discovery, session
  orchestration, transport demux, the D3D12+WASAPI media pipeline, controller input) is already built behind
  that seam against a passthrough stub; this work fills in the stub.

---

## Readiness — read before you start

**Implementation-ready:** registration → session-control field crypto → streaminfo → ECDH stream-key
agreement → per-packet AES-CTR + GMAC → Takion (SCTP-over-UDP) transport → A/V header parse. Spec §2–§8, all
**[V]** or **[C]**.

**That risk has since been retired, and this section is kept for the reader building from the spec alone.**
When this was written the **control** path was **[V]** and the **stream** crypto only **[C]** — verified at
the primitive and round-trip level, never driven end-to-end against a live stream-key capture — so the advice
was to budget a debug pass and not expect a first-try picture. Both are now **[V]** against a real console,
and the client streams video over the LAN and over the internet. If you are reimplementing from these
documents rather than using this code, the advice still applies to *your* first connection: the nonce and
key-position wiring is where an off-by-one surfaces, and the discipline below is how you retire it cheaply.

---

## Progress — C# implementation

**Status, as of 2026-09-09: the whole build order below is done and live.** Registration, the control plane,
the stream crypto, the Takion transport and A/V decode all run against a real console, on the LAN and over
the internet. What follows is the dated record of how it was built, kept because the order it was built in is
the useful part of this document; read it as history, not as a to-do list. [`../journal.md`](../journal.md)
carries the record past this point, and [`../../ROADMAP.md`](../../ROADMAP.md) is the source of truth for
what is actually left.

### 2026-07-21 — primitives and key schedules

The crypto primitives and key schedules were implemented and unit-tested first, following the build order
below, with every transform behind a green known-answer test before anything was wired into the live session
path.

- **Primitives** (`src/Ripcord.Core.Net/Crypto/`): `AesKeystreamModes` (CTR/OFB/CFB128) and `AesGcmCore`
  (GCM with an arbitrary-length IV — the platform `AesGcm` only accepts 12-byte nonces, but the stream MAC
  uses a 16-byte GCM IV). Anchored against the NIST SP 800-38A (modes) and GCM-spec (GMAC) test vectors.
- **v1 key schedules** (`src/Ripcord.Protocol.Halyard.Common/Crypto/V1/`): `HalyardControlKdf`,
  `HalyardFieldIv`, `HalyardControlFieldCrypto` (control plane); `HalyardStreamKeySchedule` (ECDH-P256 +
  SP 800-108) and `HalyardPacketCrypto` (per-packet AES-CTR + rotating GMAC) for the stream.
- **Constants, then supplied out of band, now bundled**: `HalyardControlSecrets` holds the KDF tables and
  context keys. At this point they were fed in as config from the gitignored dirty room. They are **committed
  today**, in `src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json` — a deliberate, argued exception
  covering values that are identical for every console and every account; see [`NOTICE`](../../NOTICE) and
  [`CLAUDE.md`](../../CLAUDE.md)'s "Bounded exception 1". Nothing tied to a specific console or account is
  bundled, and a test enforces that line.
- **Seam reshaped + wired** (`Crypto/IHalyardSessionCrypto.cs`): the seam now models v1's two crypto
  systems — control (`EstablishControl` → `EncryptControlField`/`DecryptControlField`/`CryptStreaminfo`) and
  stream (`GenerateEphemeralPublicKey` → `TryEstablishStream` → per-packet `TryOpenPacket`/`SealPacket`,
  keyed by key position over the whole packet). `HalyardV1SessionCrypto` is the real implementation;
  `PassthroughHalyardSessionCrypto` keeps the plumbing testable. The A/V packet parse (`HalyardStreamHeader`),
  the demuxer, and the input writer were realigned to the confirmed §6.1 layout and drive the seam.
- **Handshake assembly (partial)**: `HalyardPairingRecord` (registkey + companion + RP-KeyType) serializes
  to/from the credential-store blob, so the companion reaches `EstablishControl`; `HalyardSessCtrlFields`
  builds the encrypted RP-Auth/RP-Did/RP-OSType/RP-StartBitrate/RP-StreamingType fields (§2.1) through the
  seam. `HalyardStreamingSession` now loads the pairing record, establishes the control key from the
  RP-Nonce + companion, and emits the encrypted `/sess/ctrl` fields. (Convention: the credential-store blob
  is a serialized `HalyardPairingRecord`; registration, when implemented, emits `record.Serialize()`.)
- **Takion transport (foundation started)** — new `Ripcord.Protocol.Halyard.Takion` project:
  - Protobuf codegen wired (`Grpc.Tools` over the committed `.proto` files → `Avstream.ControlMessage` &c.).
  - `TakionMessageHeader` — the 13-byte common header (base_type · verification tag@1 · GMAC tag@5 · key
    position@9), decoded from and validated against `rudp_control_setup.pcapng`.
  - `TakionHandshake` — the client active-open SCTP handshake (INIT / INIT_ACK / COOKIE_ECHO / COOKIE_ACK);
    `BuildInit` reproduces the captured INIT **byte-for-byte**.
  - `TakionConnection` — the live UDP socket loop that runs the handshake to ESTABLISHED with per-attempt
    timeout + retransmit; tested end-to-end over loopback (incl. INIT retransmission).
  - `TakionDataChunk` / `TakionSackChunk` / `TakionMessageReassembler` / `TakionReliableChannel` — the
    **reliable-delivery layer**, all validated with a tshark pass over the full capture:
    - DATA value = `seq_num(4) · channel(u16) · 3 reserved · protobuf@9` (corrected the spec, which said
      offset 8); the flags byte carries the SCTP **ending bit** (fragmentation — the large SESSION_REQUEST is
      begin+end across two DATA chunks on channel 0x0001).
    - SACK = standard SCTP (`cumulative_tsn_ack · a_rwnd · gap/dup counts`); `Build` reproduces a captured
      SACK **byte-for-byte**.
    - `TakionReliableChannel` ties it together over the socket: fragmented reliable send + retransmit-until-
      SACKed, in-order receive + cumulative SACK + reassembly into whole `ControlMessage`s. Loopback-tested
      end to end (send → ack clears the queue → reply delivered).
  - `TakionSessionNegotiator` — the **stream key agreement flow**: sends SESSION_REQUEST (our ECDH pubkey +
    `ecdhSignature` + launchSpec), receives SESSION_REPLY, verifies the server's signature, and calls
    `TryEstablishStream`. Proven **end to end over loopback**: the client negotiates keys and then
    **decrypts an A/V packet the mock server sealed with the server→client keys** — the full
    transport→crypto "first picture" path, validated without a console.
  - `HalyardTakionStream` — the **orchestrator**: one UDP socket runs the handshake, then a single receive
    loop demultiplexes by base-type (control `0x00` → reliable channel; A/V `0x02/0x03` → demuxer, which
    authenticates + decrypts through the crypto seam), and the SESSION_REQUEST/REPLY exchange establishes the
    stream keys. Proven **full-stack over loopback**: handshake → ECDH negotiate → a mock-sealed video packet
    is routed, GMAC-verified, AES-CTR-decrypted, and reassembled into an `EncodedVideoFrame`.
  - **Wired into `HalyardStreamingSession`**: after the `/sess/init`+`/sess/ctrl` control plane, the session
    opens the Takion stream, runs `HalyardTakionStream.StartAsync`, and routes decrypted A/V to the
    video/audio observables + controller input up the same socket. The raw stream socket is gone.
  - **Stream crypto validated [C]→[V] against a live console (2026-07-22)**: dumped receive keys decrypt real
    console video to valid H.264, and the GMAC key rotation was corrected from the wire (window ≥ 1 chains
    from the window-0 GMAC key, not `aes_key`) — 13670/13672 A/V packets verify across 223 rotation windows.
  - Still open: the pre-SCTP PROTOCOL_VERSION_REQUEST/ACK (0x06/0x07) exchange (skippable — version can be
    assumed); and the launchSpec template + `encryptedKey`/`sessionKey` field semantics are still `[I]` (a
    minimal placeholder launchSpec is built today — pin these from capture before a live attempt).
- **Tests** (`tests/Ripcord.Protocol.Halyard.Tests/`): 39 tests at this point, all green. (The suite has
  grown a long way past that since; [`../../README.md`](../../README.md) carries the current figure.)
  - Self-contained (public NIST vectors + a fully-synthetic stream round-trip that ports
    `stream_crypto_reimpl.py` — the [C]-risk validator — plus seam-level seal/open round-trips through
    `HalyardV1SessionCrypto` in both directions).
  - `LiveControlVectorTests` validate the control KDF / field-IV / field cipher against the **real captured
    ground truth** (tables extracted from our own binary), loaded from the gitignored
    `captures/control_crypto_vectors.json`; they self-skip when that fixture is absent. **With the fixture
    present, the control plane now reproduces the live capture byte-for-byte in C# — [V] in our code, not
    just in the Python reference.**

**Open at the time of writing, all three since closed:** (1) first-time registration (§2.0), which persists
the `HalyardPairingRecord` — built, as `HalyardRegistrationClient`, and later joined by account-based no-PIN
registration; (2) threading the companion (RP-Key) through the credential store so `EstablishControl` runs on
a real connect — built, as `HalyardPairingCredentialStore`; (3) decrypting the real launchSpec to finalize the
JSON template — done. `sessionKey`/`encryptedKey` were pinned, the RP-Did device id reads the real MachineGuid
(`HalyardDeviceIdentity`), and the **stream ECDH is version-aware** — v17 uses P-521 (133-byte pubkey),
auto-detected on receive, with the full ECDH X as the KDF secret. These were what stood between the tested
stack and a first live picture; the picture arrived.

---

## Two inputs the spec withholds — and where they are now

The committed **spec** deliberately omits extracted constants and captured secrets (clean-room discipline —
see [`README.md`](README.md)). A reimplementation built from these documents alone will compile without them
but **will not interoperate**. What has changed since this section was written is where the first of the two
lives:

1. **Extracted constants** — the registration key tables and the `context_key` constants for the per-field IV
   (spec §2.0, §2.1). **These are now committed**, in
   `src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json`, and this repository's client needs nothing
   supplied out of band to interoperate. They are generic interoperability values — identical for every
   console and every account, and a client cannot speak the protocol without them — which is the argument
   [`NOTICE`](../../NOTICE) makes in full and the reason the exception is bounded to values of that kind. The
   spec still withholds them, so if you are implementing from the spec rather than from this code, they are
   the values you have to recover from your own captures. `captures/name-mapping.md` is where our own
   working copy sits, in the dirty room.

2. **The reference implementations** — `captures/*_reimpl.py` (see spec §0). These are your **golden test
   vectors** and they are *not* committed. Port each one into the client as a known-answer test.

> Registration-derived key material (registkey, companion/`RP-Key`, `RP-KeyType`) is **not** a constant — it
> comes from the user pairing their own console at runtime (spec §2.0). Only the two items above are static
> inputs the spec withholds.

---

## Build order

Work spec §7 top to bottom. For each step: **make the matching reference vector pass in-language first, then
wire it to a socket.** That converts "debug a live stream" into "make N known-answer tests green," which is
exactly where the [C]-vs-[V] risk gets retired.

| # | Step | Spec | Golden vector (`captures/`) |
|---|------|------|------------------------------|
| 1 | Registration / pairing (one-time, passcode) | §2.0 | — (registration table; bundled here, recover it from your own captures if reimplementing) |
| 2 | Session-control session-key KDF (`out1`,`out2`) | §2.1 | `kdf_reimpl.py` **[V]** |
| 3 | Per-field IV (HMAC-SHA256 over `out2‖counter`) | §2.1 | `field_iv_reimpl.py` **[V]** (needs `context_key`) |
| 4 | `/sess/ctrl` field encryption (AES-128-CFB) | §2.1 | `rp_auth_reimpl.py` **[V]** |
| 5 | Streaminfo build + `AES-128-OFB(out1)` | §4.3 | `streaminfo_reimpl.py` **[V]** |
| 6 | Control-plane protobuf messages | §4 | compile `stream_control.proto` |
| 7 | Stream key agreement: random `handshakeKey` → ECDH-P256 → `ecdhSignature` verify → SP800-108 KDF | §5.1–5.4 | `stream_crypto_reimpl.py` **[C]** |
| 8 | Per-packet AES-128-CTR encrypt + 4-byte GMAC | §5.5 | `stream_crypto_reimpl.py` **[C]** |
| 9 | Takion transport: single UDP socket, base-type demux, SCTP INIT/COOKIE/SACK | §3, §8 | wire trace `captures/rudp_control_setup.pcapng` **[V]** |
| 10 | A/V header parse + per-frame reassembly | §6.1 | — (code-exact in spec) |

Steps 1–6 are the fully-**[V]** control plane — get these green first; they're low-risk and they bootstrap
the stream layer (the streaminfo carrying `handshakeKey` is itself `out1`-encrypted). Steps 7–8 are the
**[C]** stream crypto — the risk area; lean hardest on the round-trip vector here before going live.

---

## What does NOT block a first picture

The spec flags these; all are clean-LAN-optional. Don't rat-hole on them for first light:

- **FEC** (§6.2) — with no packet loss the decoder never runs. Skip.
- **Takion RTO / window / congestion tuning** (§8.2) — a minimal in-order + timeout-retransmit of DATA/SACK
  is enough on LAN. The wire format and handshake are already **[V]**; only the *timing* is unspecified.
- **Bandwidth / MTU probe** (§6, `bandwidth_probe.proto`) — negotiation nicety, not required to stream.
- **`PROTOCOL_VERSION_REQUEST` / IPv6-token exchange** (§8) — skippable; a client can simply assume the
  version. The tokens are just the endpoints' IPv6 addresses — nothing to derive.
- **`disableRPEncryption`** (§4.3/§5.5) — honor the toggle on the sender, but v1 LAN runs encrypted; you
  don't need the plaintext path for a first connection.

---

## Definition of done for "first picture"

1. Reference vectors 2–8 reproduced byte-for-byte in-language (known-answer tests green).
2. Pairing completes against the real console (persist registkey + companion + `RP-KeyType`).
3. `/sess/init` + `/sess/ctrl` accepted (control field crypto correct on the wire).
4. Takion connects (INIT/INIT_ACK/COOKIE_ECHO/COOKIE_ACK) on the negotiated UDP port.
5. `SESSION_REQUEST`/`SESSION_REPLY` exchanged, both `ecdhSignature`s verify, per-direction keys derived.
6. Type-`0x02` video packets GMAC-verify and AES-CTR-decrypt into valid H.264 NALUs → decoder → frame.

If 1–5 pass but 6 fails, the fault is almost certainly in the step-7/8 nonce or key-position wiring (the
known [C] risk) — compare live bytes against `stream_crypto_reimpl.py` at the exact `key_pos` on the wire.
