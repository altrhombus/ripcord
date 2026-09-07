# Ripcord Phase 1 — PS5 LAN remote play: build plan

> **Superseded — kept for the record.** This plan describes work that has since landed; it is not a
> current description of the system. See [`../../ROADMAP.md`](../../ROADMAP.md) for what is left,
> [`../journal.md`](../journal.md) for what happened, and [`../architecture.md`](../architecture.md)
> for how the code is arranged now.

Scope: connect Ripcord to a real PS5 on the same LAN, sign in with a PSN account, and get live
video/audio with controller control — the Phase 1 deliverable from the project plan. This document
turns the protocol specs in `docs/protocol/` into an ordered, buildable sequence, structured around
one deliberate design decision: **everything except the cryptography is implementable today, so the
crypto is isolated behind a single seam** and everything else is built and tested around it first.

Written 2026-07-13 from our own spec docs and captures only. Nothing here should be implemented by
referencing another Remote Play project's source — see "Clean-room guardrails" at the end, which
matters most for the crypto stage.

> **Current status now lives in [`ROADMAP.md`](../../ROADMAP.md)** at the repo root — the stage table below
> is historical and was last updated 2026-07-13 (pivot note 2026-07-18). In particular, Stage 5 below is
> no longer accurate: the crypto seam has since been solved (see `docs/protocol/IMPLEMENTATION.md` and
> `docs/protocol/captures/lab-notebook.md`). This document remains the detailed technical/historical record.

## Status (updated 2026-07-13; crypto-approach pivot 2026-07-18 — read this note first)

**Pivot note (2026-07-18):** Stage 5 below was written against the *current* PS Remote Play build's
protocol (ECDH/`RP-Pubkey`/`RP-Hmac`/AEAD, `data1/2/3`+`skey` cloud seeds). That build's anti-debug
packer has proven very costly to get past. Discovered that older PS Remote Play releases
(5.5.0.8250/9.0.0.2120-era) are unobfuscated and speak a materially simpler protocol instead — static
`RP-Registkey` + server `RP-Nonce` → a KDF → AES-128-CFB encryption of a handful of small fields
(`RP-Auth`, `RP-Did`, `RP-OSType`, `RP-StartBitrate`, `RP-StreamingType`), no public-key exchange at
all — confirmed working end-to-end against real hardware including LAN play. **Decision: implement
Phase 1 against this older, simpler protocol as "v1"; defer the ECDH/HMAC protocol described in this
document's Stage 5 to a later "v2"** once a version is found that has the modern protocol but predates
the packer. This means `IPs5SessionCrypto`/`Ps5HandshakeSecrets` below will need a v1-shaped
implementation first (registkey+nonce in, key+IV out, not ECDH+cloud-seeds in) — the interface shape
may need revisiting when Stage 5 is actually implemented, but the seam/stub architecture (build
everything else against a passthrough first) still applies unchanged. Live RE progress on the v1
crypto (the KDF is fully solved and verified; the field-encryption cipher mode and IV derivation are
mostly mapped, one open mystery remains) is tracked in `docs/protocol/captures/lab-notebook.md` — read that
for current status before starting Stage 5 work, not this document's Stage 5 description below, which
still only describes the deferred v2 approach.

**Naming note:** the code uses trademark-free internal codenames. The Sony-console backend is
**Halyard**, so where this plan says `Ripcord.Cloud.PlayStation` / `Ripcord.Protocol.PlayStation.*` /
`IPs5SessionCrypto` / `ConsolePlatform.Ps5`, the code has `Ripcord.Cloud.Halyard` /
`Ripcord.Protocol.Halyard[.Common]` / `IHalyardSessionCrypto` / `ConsolePlatform.Halyard`. This plan
keeps the descriptive names for readability; required on-wire values (hostnames, the `"PS5"` platform
tag, `RP-*` headers) stay verbatim in code as protocol constants.

Everything except the crypto seam is built. Summary by stage:

| Stage | State | Verified |
|---|---|---|
| 0 — ProtocolLab + replay | Done | `replay synth` reassembles frames; harness drives all verbs |
| 1 — Cloud sign-in (OAuth2) | Built | `authurl` builds the correct URL; **live login pending an OAuth client-credential decision** |
| 2 — Discovery (cloud list + LAN SRCH/mDNS) | Built | **`discover` found the real PS5 on the LAN** with correct power state |
| 3 — Session orchestration (create/wake/OFFER) | Built | Not live-verified (gated on the credential) |
| 4 — Direct transport + `/sess` framing + stream demux (crypto-stubbed) | Built | Demux/reassembly verified via `replay synth` and `mediademo` |
| 5 — **Crypto seam (key agreement + registration)** | **Not started — the one blocker** | — |
| 6 — Media + input wiring | Built | Input packet path verified (66-byte channel-`0x0e`); media pipeline below |

**Media decode pipeline (a major addition beyond the original Stage 6 — Phase 0 had only the D3D12
capability probe, no decode pipeline, so it was built from scratch and is now working):**

| Piece | State | Verified on hardware |
|---|---|---|
| D3D12 composition swap chain + present (`VideoRenderer`, Layer 1) | Done | Yes — animated clear colour |
| Textured frame draw (root sig + shader + upload, `PresentBgra`, Layer 2) | Done | Yes — moving test pattern, scaled |
| Software H.264 decode via Media Foundation → NV12 → BGRA (Layer 3) | Done | Yes — plays a test `.h264` clip, correct colour/geometry |
| Background decode/present thread (non-blocking submit) | Done | Yes |
| DPI-correct sizing (panel stretches; no manual buffer resize) | Done | Yes |
| WASAPI audio render (`AudioRenderer`/`AudioOutput`), format- and device-change-hardened | Done | Yes — test tone |

**Bottom line:** cloud/discovery/session/transport/framing plumbing and the full A/V *output*
pipeline are built, with LAN discovery and the whole video+audio path verified on real hardware. The
single thing standing between all of it and a live console stream is **Stage 5 (the session
crypto)**, which also gates first-time registration.

### Next steps

1. **Stage 5 — the session crypto/key-agreement + registration.** The real blocker and the
   clean-room-sensitive research problem; every input it needs is already wired by the plumbing.
   Needs the real console + our captures to validate rather than a self-contained test. Start from
   the cleartext leads (`data1/2/3` seeds + `skey`, see `ps5-cloud-session-api.md`).
2. **Live-verify the cloud path** (Stages 1–3) — blocked only on deciding the OAuth client
   credential (register our own vs. the public PS-app id).
3. **Smaller media follow-ups** (deferrable): audio decoder (Opus → the WASAPI path), video frame
   pacing (present-by-timestamp + decode-ahead), aspect-ratio/letterboxing, and eventually
   hardware/D3D12 video decode as an optimization over the working software path.

## The guiding principle: one seam

From the capture analysis (`ps5-cloud-session-api.md`, `ps5-session-establishment.md`,
`ps5-av-stream.md`) the connect path breaks cleanly into two halves:

- **The plumbing** — OAuth2 sign-in, cloud discovery, session orchestration, LAN discovery, the
  direct-session transport, the `/sess` handshake *framing*, the `RPCS` control frames, and the
  multiplexed stream demux. All of this is specified well enough to write now.
- **The secret sauce** — the key agreement that turns the handshake's ECDH/nonce/registration
  material (plus the cloud `data1/2/3` seeds and `skey`) into session keys, the `RP-Hmac`
  construction, and the AEAD that protects the stream/input payloads. This is *not* yet derived and
  is the one genuine blocker.

So: define an interface for the secret sauce, give it a **passthrough stub**, and build the entire
plumbing against the stub. The pipeline runs end to end early (against fixtures and synthetic data),
and the crypto collapses to a single, fully-wired research problem — every input it needs is already
being handed to it by working code.

## New projects

Added to the existing Phase-0 solution (`Ripcord.Core`, `Ripcord.Input`, `Ripcord.Media[.Interop]`,
`Ripcord.App` already exist). Dependency direction stays platform-specific → shared → `Ripcord.Core`.

| Project | Holds | Notes |
|---|---|---|
| `Ripcord.Core.Net` | Transport primitives: the dual control transport (TCP-9295 and RUDP-over-UDP-9303), UDP stream sockets, mDNS resolver, UDP 9302 `SRCH`. Thin crypto-primitive wrappers (AES-GCM, ECDH, HKDF) exposing .NET's `System.Security.Cryptography`. | No PS/Xbox knowledge. The crypto *primitives* live here; the PS *key schedule* does not (that's the seam, in Common). |
| `Ripcord.Cloud.PlayStation` | The PSN cloud REST client: OAuth2 (auth-code + refresh), account/profile, the `cloudAssistedNavigation` console list, the `remotePlaySessions` session manager, the `commands` wake call, and `sessionMessage` signaling. Backs a cloud `IConsoleDiscoveryService` and a new `IRemotePlaySessionCoordinator`. | Pure HTTPS + JSON. The largest fully-specified chunk. |
| `Ripcord.Protocol.PlayStation.Common` | Transport-neutral PS types: the `RPCS` frame codec, the `/sess/rgst|init|ctrl` message builder/parser, the stream framing (channel demux + reassembly), the `ControllerStateFrame`→PS input-packet mapping, **and the crypto seam interfaces + stub**. | Where the seam is defined. Shared by PS5 now, PS4 later. |
| `Ripcord.Protocol.PlayStation.Ps5` | The PS5 `IStreamingSession`: wires cloud coordination + LAN discovery + transport + handshake + crypto + stream demux into one session lifecycle. Also the PS5 `IConsolePairingService`. | The composition point for the direct session. |
| `tools/Ripcord.ProtocolLab` | Console harness that drives the connect flow against a real PS5 and replays our own captures through the parsers. Built first, kept alive long-term. | The iteration tool for every stage below; also the per-stage verification driver. |
| `tests/…` | Parser/framing/mapping tests against recorded fixtures (no live console needed in CI). | Fixtures are *structural* only — no real keys/tokens committed; see guardrails. |

## The seam

Defined in `Ripcord.Protocol.PlayStation.Common`. Two interfaces, because registration and per-session
crypto are separate research problems that fail independently:

```csharp
// Per-session key agreement + AEAD. This is the load-bearing unknown.
public interface IPs5SessionCrypto
{
    // Given everything the /sess exchange and the cloud seeds provide, derive session keys.
    // The stub returns an empty schedule; the real impl is the research deliverable.
    Ps5SessionKeys DeriveKeys(in Ps5HandshakeSecrets secrets);

    // The keyed authenticator carried as RP-Hmac over a handshake message.
    void ComputeHandshakeMac(ReadOnlySpan<byte> message, in Ps5SessionKeys keys, Span<byte> macOut);

    // AEAD seal/open for one payload on a given stream channel + sequence number.
    bool TrySeal(Ps5Channel ch, uint seq, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag);
    bool TryOpen(Ps5Channel ch, uint seq, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain);
}

// First-time registration (obtaining an RP-Registkey for this client+console+account).
public interface IPs5Registration
{
    Task<Ps5RegistrationResult> RegisterAsync(Ps5RegistrationRequest request, CancellationToken ct);
}
```

`Ps5HandshakeSecrets` bundles the wired-up inputs: the `RP-Registkey`, our ephemeral ECDH keypair,
the console's `RP-Pubkey`, the `RP-Nonce`, and the cloud-delivered `data1/2/3` + `skey` seeds. The
point of the bundle is that by the time the real crypto is written, **the plumbing already collects
and hands over every one of these** — the crypto author only implements the transform.

**Two implementations, from day one:**

- `PassthroughPs5SessionCrypto` — `TryOpen`/`TrySeal` copy input to output; `ComputeHandshakeMac`
  writes a fixed pattern; `DeriveKeys` returns an empty schedule. Lets the transport, framing,
  demux, decode, and input paths all run against fixtures whose payloads are already plaintext
  (synthetic frames, or the *structural* parts of our captures). This is what makes stages 0–4 and 6
  independently testable without the secret.
- `Ps5SessionCrypto` / `Ps5Registration` — the real implementations. Stage 5. The only components
  that must be derived first-principles from our own captures under clean-room discipline.

The `IStreamingSession` takes an `IPs5SessionCrypto` by injection, so swapping stub → real is a DI
change, not a rewrite.

## Build order

Each stage has a concrete runnable check, per the plan's "every phase has its own check" rule.
`Ripcord.ProtocolLab` is the driver throughout.

### Stage 0 — ProtocolLab harness + fixture replay
Stand up the console harness and a capture-replay path (feed a saved pcap/decrypted fixture through
the parsers we're about to write). No product code yet.
**Check:** `ProtocolLab replay <fixture>` runs and prints parsed structure. Gives every later stage a
non-live test loop.

### Stage 1 — PSN cloud client: sign-in (`Ripcord.Cloud.PlayStation`)
OAuth2 authorization-code flow (drive Sony's WebAuthn/passkey web flow, catch the code at the
`remoteplay/redirect` URI) + refresh-token persistence via `IConsoleCredentialStore` (DPAPI).
Decide the OAuth client-credential approach here (our own vs. the known public PS-app id) — it's a
policy choice, not a technical unknown.
**Check:** `ProtocolLab login` authenticates and prints the account/profile. Fully specified; no
crypto, no console.

### Stage 2 — Discovery (`Ripcord.Cloud.PlayStation` + `Ripcord.Core.Net`)
Cloud console list (`cloudAssistedNavigation/.../clients?platform=PS5`) → `DiscoveredConsole`s with
wake capability; LAN mDNS + UDP 9302 `SRCH` → LAN address + firmware version. Merge into one
`IConsoleDiscoveryService` per the Core contract, de-duplicating a console seen via both.
**Check:** `ProtocolLab discover` lists your consoles with LAN IP, online/wake state, firmware.
Independent of everything below.

### Stage 3 — Session orchestration (`Ripcord.Cloud.PlayStation`)
`remotePlaySessions` create/read/delete, the `commands` wake/trigger call, emit the `sessionMessage`
`OFFER` with our LAN + reflexive candidates. Parse what we can of the `ANSWER`; for LAN we take the
console address from Stage 2 discovery rather than depending on the (still-undecoded) push WebSocket.
**Check:** `ProtocolLab wake <console>` wakes a sleeping console and a session appears in the readback.
Real cloud effect, still no crypto.

### Stage 4 — Direct transport + handshake framing, crypto-stubbed (`Core.Net` + `Protocol…Common`)
The dual control transport (TCP-9295 for LAN-direct, RUDP/UDP-9303 as the alternate), the
`/sess/init` + `/sess/ctrl` message builder/parser, the `RPCS` frame codec, and the 9296/9297 stream
demux (channel split + video reassembly) → `EncodedFrame`. All using `PassthroughPs5SessionCrypto`,
so the handshake is *syntactically* complete but not authenticated.
**Check:** (a) replay our own captures through the demux and reconstruct frame boundaries/channels
correctly; (b) against a live console, send a well-formed `/sess/init` and correctly parse the
console's response framing (it will reject on MAC — that's expected and is exactly the boundary
Stage 5 removes).

### Stage 5 — The crypto seam: registration + session (`Protocol…Common`, real impls)
The isolated research problem, now with every input already wired. Derive, from our own captures and
public crypto references only: the key schedule (`DeriveKeys`), the `RP-Hmac` construction, the AEAD
nonce/keying per channel, and first-time registration. Start from the cleartext leads we already
have — the cloud `data1/2/3` seeds and `skey` (`ps5-cloud-session-api.md`) — since they are the
material that seeds the direct session and are readable, unlike the encrypted UDP.
**Check:** `/sess/init` completes (console accepts our MAC), and `TryOpen` on the video channel
produces valid H.264/HEVC NAL units that the Phase-0 decode pipeline renders. This is the
"first picture" moment.

### Stage 6 — Media + input wiring (`Protocol…Ps5` + `Ripcord.Media`/`Ripcord.Input`)
Route decrypted video/audio `EncodedFrame`s into the Phase-0 `IVideoDecodePipeline` + WASAPI audio,
and `ControllerStateFrame` (from the existing `IControllerSource`) → PS input packet (channel `0x0e`)
→ `TrySeal` → send. Wire `IStreamingSession` state/stats into the `SessionPage` HUD.
**Check (Phase 1 deliverable):** connect to a real PS5 on the LAN, see live video/audio, and control
it with an Xbox controller or the Ally X's built-in pad.

## What stays stubbed or deferred in Phase 1

- **`IBandwidthController`** — a fixed-bitrate stub; adaptive logic is Phase 4. The up-direction
  feedback channels (`ps5-av-stream.md`, `ps5-wan-relay.md`) get parsed but not acted on yet.
- **WAN / relay path** — LAN-only for Phase 1 (plan decision, reaffirmed by `ps5-wan-relay.md`). The
  `sessionMessage` OFFER still advertises both candidates; we just only *use* the LAN one.
- **Push WebSocket full decode** — sidestepped on LAN via direct discovery; needed later for WAN.
- **DualSense/DualShock advanced features** — Phase 3; Phase 1 treats all pads as generic via the
  existing `Ripcord.Input`.
- **PS4** — Phase 2; the `.Common`/`.Ps5` split is set up now so PS4 slots in later.

## Clean-room guardrails for this phase

The plumbing stages (0–4, 6) are ordinary app/network/UI work — normal AI-assisted coding is fine.
**Stage 5 is the sensitive one.** For it:
- Derive the crypto only from our own dated spec docs (`docs/protocol/`) and our own captures, plus
  public references (RFCs, the AES-GCM/HKDF specs, .NET crypto docs). Never open, quote, or ask an
  AI agent to reproduce another Remote Play project's source or its hardcoded constants.
- Keep the provenance log (`docs/protocol-research-log.md`) current as the derivation progresses.
- Fixtures committed for tests carry *structure only* — no real keys, tokens, account IDs, or
  captured ciphertext with recoverable content. The raw captures stay gitignored as they are now.
