# `docs/protocol/` — PS5 Remote Play protocol specification (independent implementation)

This directory holds Ripcord's **independent specification** of the PS5 Remote Play wire protocol, written
to build an **interoperable** client for hardware the user owns. It is engineering documentation, not a
copy of any vendor product.

## What's here

- [`IMPLEMENTATION.md`](IMPLEMENTATION.md) — **start here to build.** Build order, the two inputs that live
  outside these committed docs (extracted constants + reference vectors), and the "make the vectors pass
  first" discipline for the stream crypto.
- [`ps5-remoteplay-v1-spec.md`](ps5-remoteplay-v1-spec.md) — the full v1 (older-protocol) traffic spec:
  session control, streaminfo, control-plane messages, stream key agreement + per-packet auth, transport,
  and FEC. Confidence-tagged; test-vector index at the top.
- [`stream_control.proto`](stream_control.proto), [`bandwidth_probe.proto`](bandwidth_probe.proto) — the
  control-plane message schema, with the vendor's coined names replaced (see "What is reproduced here" for
  which names are kept).
- [`ps5-local-discovery.md`](ps5-local-discovery.md) — **current**: LAN discovery, wake, and the PS4
  console-family deltas. Part of the implemented protocol, not historical.
- [`ps5-controller-input-packet.md`](ps5-controller-input-packet.md),
  [`dualsense-hid-report.md`](dualsense-hid-report.md), [`ps5-av-stream.md`](ps5-av-stream.md) —
  **current**: input and A/V payload formats, as implemented.
- [`ps5-v2-control-plane.md`](ps5-v2-control-plane.md) — **current**: the v2 control plane (the GCM
  envelope, the rolling session key, RPCS) and how its stream plane differs from v1. Implemented behind an
  opt-in switch; not yet streaming from Ripcord's own client.
- [`ps5-cloud-session-api.md`](ps5-cloud-session-api.md), [`ps5-network-architecture.md`](ps5-network-architecture.md),
  [`ps5-wan-relay.md`](ps5-wan-relay.md) — the PSN cloud/account tier and WAN path. Partly current
  (the cloud API shape is accurate), partly superseded speculation, flagged in place.

## The control-plane schemes, and what "v1" means

`v1` and `v2` are **our** labels for the two control-plane schemes a console speaks. They name the scheme, not
a vendor application version, and they are **not** the `RP-Version` header:

- **`RP-Version` is a console-family value, not a scheme.** It is `1.0` on PS5 and `10.0` on PS4, and `1.0`
  appears under both schemes (it also rides the cloud `commands` payload as `protocolVer`). `RP-Version: 1.0`
  and our label `v1` are unrelated; the collision is unfortunate and is the reason this section exists.
- **`v1`** — `RP-Registkey` + the console's `RP-Nonce` → a table-driven KDF → AES-128-CFB over a handful of
  small headers. **No public-key exchange in the HTTP handshake at all.** ECDH exists only later, inside the
  Takion *stream* handshake (`ecdhPublicKey`/`ecdhSignature`). Every PS4 and every PS5 speaks it; Ripcord's
  default, validated end-to-end against our own PS5 and PS4.
- **`v2`** — PS5 only: an ECDH exchange in the `RP-Pubkey`/`RP-Hmac` headers, an AES-GCM envelope over the rest
  of the exchange, and a session key that rolls each session. Specified in
  [`ps5-v2-control-plane.md`](ps5-v2-control-plane.md) and implemented behind an opt-in switch; it reaches the
  console's session reply from Ripcord's own client but has not yet streamed.

Two corrections to earlier framing, recorded because they were wrong for a while:

- **Cloud/PSN authentication is not a `v2` feature.** Early notes treated the account/OAuth tier as
  belonging to the newer generation. It does not — it is the account path, orthogonal to which control
  plane the console speaks. `ps5-cloud-session-api.md` is accurate about the API shape; only its
  generation framing was wrong.
- **Cloud *play* — streaming from a datacentre rather than from your own console — is a candidate for a
  genuinely different protocol, but we have no data on it.** It is not implemented, not captured, and
  not specified anywhere here. Listed only so the gap is explicit. **[X]**

## Provenance & method

- **Where the substance came from** is stated once, in [`NOTICE`](../../NOTICE): our own copies of the vendor's
  client, analysed statically and dynamically, and our own captures of our own console and account. Each
  capture, analysis and reference is recorded, dated and with what it informed, in
  [`../protocol-research-log.md`](../protocol-research-log.md). Neither is restated here.
- **Where a value here is an *assumption*** rather than something our own evidence established, it is tagged
  **[X]** so it cannot be mistaken for a finding.
- **Open assumptions are tagged `[X]` in place**, next to the value each one qualifies, and are
  deliberately not gathered into a list anywhere: a separate list is a second copy, and a second copy goes
  stale — which is the same reason this project does not cite commit hashes. An `[X]` value is provisional:
  it interoperates, but it has not been confirmed against the console. Grep the specs for `[X]` before
  relying on any specific value.
- The captures-folder research materials — the analysed binary, raw captures, decrypted samples, the working RE
  log, the reimplementation scripts (which embed extracted constants and captured secrets), and the
  vendor↔ours name mapping — live under `captures/` and are **gitignored / not distributed**.

## What is reproduced here, and what is not

- **Intended to not be reproduced:** the vendor's coined names, internal symbol names, log strings, or any
  of its code. Vendor code is cited by relative virtual address (`FUN_<rva>`) only — an address is a fact
  about a binary.
  - **Coined names are renamed.** The `.proto` files rename the vendor's *coined or arbitrary* message and
    enum names — `BIG`/`BANG` → `SESSION_REQUEST`/`SESSION_REPLY`, `SENKUSHA` → `BANDWIDTH_PROBE`,
    `XMBCOMMAND` → `SYSTEM_MENU_COMMAND`, `GKTRACE` → `TRACE`, `TAKIONPROTOCOLREQUEST` →
    `PROTOCOL_VERSION_REQUEST` — while preserving all field numbers and enum values, which are
    interoperability facts.
  - **Purely descriptive names are retained as interface labels.** Where the vendor's name for a message or
    enum constant is simply the plain-English description of its content — `CursorPayload`,
    `PacketLossPayload`, `StreamInfoPayload`, `DebugOption`, and the trace/metric constants such as
    `VIDEO_DECODE` or `AUDIO_FRAMENALUSCOMPLETE` — it is kept. About two-thirds of the schema's message
    names fall in this class. Any independent labeller describing the same field arrives at the same words,
    and renaming them would obscure the mapping.
  - **Two further documented exceptions, both deliberate.** (1) `Takion` and `Senkusha` are the **vendor's own
    codenames**, retained throughout the code and this spec as protocol terminology — see the naming table in
    `docs/architecture.md` ("Naming") for the rationale. Only `Halyard` is our invention. (2) Some protobuf **field** names
    (`mtuReq`, `naluCount`, `sendDelay`, …) are most likely the vendor's own, read from the binary's
    reflection metadata. Field names never appear on the wire, so these are not interop facts and could be
    renamed; they simply have not been.
  - **Three packet-level terms are our own.** The vendor has words of its own for the client's controller
    packets, its periodic receive report and the per-packet cipher position; Ripcord uses these instead, in
    the code, the specs and the `.proto` field names:

    | Ours | What it names |
    |---|---|
    | **input** packets (input state, input events) | base types 6 and 1, the client's controller packets (§6.3) |
    | **loss report** | base type 5, the client's periodic received/lost counts (§3, §8) |
    | **keystream offset** | the per-packet position that selects the GMAC window and the CTR nonce (§5.4) |
  - **No vendor *symbol* name is used anywhere** — no C++ class, function, or log string recovered from the
    binary appears in the code or this spec; those are cited by RVA only. (The control-plane cipher is named
    for what it does, not after the vendor's own class.)
- **Reproduced, deliberately — on-the-wire interoperability facts only.** These are values the console
  parses by content and that therefore *cannot* be changed without breaking interoperability:
  protobuf **field numbers / enum values / wire types**; JSON config **keys** (`handshakeKey`, `sessionId`,
  `streamResolutions`, `disableRPEncryption`, …); and HTTP **header names** (`RP-Auth`, `RP-Did`,
  `RP-Nonce`, `RP-OSType`, …).

## What the repository holds

An interoperability specification reproduces the values a client needs to interoperate, not vendor code. Key
material tied to a console, an account or a session is derived at runtime from the user's own registration and
is never committed. Two generic values recovered from the vendor client are committed on purpose, the v1
interoperability constants and the application's OAuth client credential; [`NOTICE`](../../NOTICE) is where
each is described and where the build switches that omit them are listed.

Nothing here is legal advice, and Ripcord makes no claim about how any law applies to it.
