# `docs/protocol/` — PS5 Remote Play protocol specification (clean-room)

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
  control-plane message schema, expressed with our own descriptive names.
- [`ps5-local-discovery.md`](ps5-local-discovery.md) — **current**: LAN discovery, wake, and the PS4
  console-family deltas. Part of the implemented protocol, not historical.
- [`ps5-controller-input-packet.md`](ps5-controller-input-packet.md),
  [`dualsense-hid-report.md`](dualsense-hid-report.md), [`ps5-av-stream.md`](ps5-av-stream.md) —
  **current**: input and A/V payload formats, as implemented.
- [`ps5-session-establishment.md`](ps5-session-establishment.md),
  [`ps5-session-crypto.md`](ps5-session-crypto.md) — **historical.** These describe the *newer* client's
  HTTP control plane (see below), which Ripcord does not implement. Retained as a record of what was
  observed; several statements in them were superseded and are flagged in place.
- [`ps5-cloud-session-api.md`](ps5-cloud-session-api.md), [`ps5-network-architecture.md`](ps5-network-architecture.md),
  [`ps5-wan-relay.md`](ps5-wan-relay.md) — the PSN cloud/account tier and WAN path. Partly current
  (the cloud API shape is accurate), partly superseded speculation, flagged in place.

## Protocol generations, and what "v1" means

`v1` and `v2` are **our** labels for two vendor client generations we observed. They are not vendor
version numbers, and they are **not** the `RP-Version` header:

- **`RP-Version` is a console-family value, not a generation.** It is `1.0` on PS5 and `10.0` on PS4,
  and `1.0` appears in *both* generations' captures (it also rides the cloud `commands` payload as
  `protocolVer`). `RP-Version: 1.0` and our label `v1` are unrelated; the collision is unfortunate and
  is the reason this section exists.
- **`v1` — what Ripcord implements.** The control plane of the older, unobfuscated client
  (the vendor control DLL, ~5.5.0.8250): `RP-Registkey` + the console's `RP-Nonce` → a table-driven KDF →
  AES-128-CFB over a handful of small headers. **No public-key exchange in the HTTP handshake at all.**
  ECDH exists in this generation only later, inside the Takion *stream* handshake
  (`ecdhPublicKey`/`ecdhSignature`). Validated end-to-end against our own PS5 and PS4.
- **`v2` — observed, never reverse-engineered.** The newer client (`<engine module>`, packed/anti-debug)
  uses a *different* HTTP control plane: an ECDH exchange carried in `RP-Pubkey`/`RP-Hmac` headers plus
  the `RP-DevACha`/`RP-DevAChaTag` pair. Seen in five capture sessions on 2026-07-10 and documented in
  `ps5-session-establishment.md`; the packer made static RE uneconomic, so we pivoted to the older
  client instead. **Nothing about it is derived** — we know the header set and the shapes, not the
  construction.

Two corrections to earlier framing, recorded because they were wrong for a while:

- **Cloud/PSN authentication is not a `v2` feature.** Early notes treated the account/OAuth tier as
  belonging to the newer generation. It does not — it is the account path, orthogonal to which control
  plane the console speaks. `ps5-cloud-session-api.md` is accurate about the API shape; only its
  generation framing was wrong.
- **Cloud *play* — streaming from a datacentre rather than from your own console — is a candidate for a
  genuinely different protocol, but we have no data on it.** It is not implemented, not captured, and
  not specified anywhere here. Listed only so the gap is explicit. **[X]**

## Provenance & clean-room method

- The substance here was derived **primarily from our own copy of the vendor's client binary** (static
  analysis + the protobuf reflection metadata the binary itself embeds) and from **our own live
  packet/memory captures** of our own console and account.
- **No other implementation of these protocols is used as a source.** Not for implementation detail, not for
  byte-level constructions, not for naming. Where a value here is an *assumption* rather than something our own
  evidence established, it is tagged **[X]** so it cannot be mistaken for a finding.
- **The open `[X]` list is short and stated plainly** in [`../../ROADMAP.md`](../../ROADMAP.md). The three
  items this section used to name — the FEC generator-matrix form, the GF primitive polynomial, and the FEC
  per-unit stride padding — were all settled on 2026-08-01/02 against our own binary and our own console; the
  chief remainders now are the control GMAC AAD rule and the senkusha probe ordering. An `[X]` value is
  provisional — it interoperates, but it has not been confirmed against the console. Check that list before
  relying on any specific value.
- The "dirty-room" research materials — the analysed binary, raw captures, decrypted samples, the working RE
  log, the reimplementation scripts (which embed extracted constants and captured secrets), and the
  vendor↔ours name mapping — live under `captures/` and are **gitignored / not distributed**.

## What is reproduced here, and what is not

- **Intended to not be reproduced:** the vendor's coined names, internal symbol names, log strings, or any
  of its code. Vendor code is cited by relative virtual address (`FUN_<rva>`) only — an address is a fact
  about a binary, not authored expression.
  - **Coined names are renamed.** The `.proto` files rename the vendor's *coined or arbitrary* message and
    enum names — `BIG`/`BANG` → `SESSION_REQUEST`/`SESSION_REPLY`, `SENKUSHA` → `BANDWIDTH_PROBE`,
    `XMBCOMMAND` → `SYSTEM_MENU_COMMAND`, `GKTRACE` → `TRACE`, `TAKIONPROTOCOLREQUEST` →
    `PROTOCOL_VERSION_REQUEST` — while preserving all field numbers and enum values, which are
    interoperability facts.
  - **Purely descriptive names are retained as interface labels.** Where the vendor's name for a message or
    enum constant is simply the plain-English description of its content — `CursorPayload`,
    `PacketLossPayload`, `StreamInfoPayload`, `DebugOption`, and the trace/metric constants such as
    `VIDEO_DECODE` or `AUDIO_FRAMENALUSCOMPLETE` — it is kept. About two-thirds of the schema's message
    names fall in this class. They carry no coined expression: any independent labeller describing the same
    field arrives at the same words, and renaming them would obscure the mapping without adding originality.
  - **Two further documented exceptions, both deliberate.** (1) `Takion` and `Senkusha` are the **vendor's own
    codenames**, retained throughout the code and this spec as protocol terminology — see the naming table in
    `CLAUDE.md` for the rationale. Only `Halyard` is our invention. (2) Some protobuf **field** names
    (`mtuReq`, `naluCount`, `sendDelay`, …) are most likely the vendor's own, read from the binary's
    reflection metadata. Field names never appear on the wire, so these are not interop facts and could be
    renamed; they simply have not been.
  - **No vendor *symbol* name is used anywhere** — no C++ class, function, or log string recovered from the
    binary appears in the code or this spec; those are cited by RVA only. (The control-plane cipher is named
    for what it does, not after the vendor's `RpCryptAes` class.)
- **Reproduced, deliberately — on-the-wire interoperability facts only.** These are values the console
  parses by content and that therefore *cannot* be changed without breaking interoperability:
  protobuf **field numbers / enum values / wire types**; JSON config **keys** (`handshakeKey`, `sessionId`,
  `streamResolutions`, `disableRPEncryption`, …); and HTTP **header names** (`RP-Auth`, `RP-Did`,
  `RP-Nonce`, `RP-OSType`, …). Interface facts required for interoperability are not protectable
  expression.

## Legal note (not legal advice)

This is an interoperability specification for connecting a user's own client to a user's own console under
the user's own account. It reproduces interface facts, not vendor code or expression. It does **not** ship
extracted keys or a circumvention tool — the implementation is expected to derive any key material at
runtime from the user's own console registration, and the extracted constants/secrets stay in the
gitignored dirty room.

This is **not** a determination of legal compliance. Anti-circumvention law (e.g. DMCA §1201 and its
interoperability exception §1201(f), and the EU Software Directive Art. 6) and platform terms of service
are fact- and jurisdiction-specific. Anything intended for distribution should be reviewed by qualified
counsel first. See the project notes for the fuller discussion.
