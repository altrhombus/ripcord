# PS5 Remote Play — session cryptography (key agreement + AEAD)

> **Superseded by [`ps5-v2-control-plane.md`](ps5-v2-control-plane.md).** This is an early record of the v2
> control plane's key agreement, from before it was worked out. The observations in it (the header set, the point
> sizes, the primitives) stand; its open questions are answered there: the key schedule (§4), the `RP-Hmac`
> construction (§1, §4) and where the session key comes from (§2). Statements below that a key schedule is
> unknown, or that cloud seeds feed the session keys, are superseded.

Sources: our own packet captures, and static analysis of our own installed copy of the client. Static anchoring
used only public standard constants (FIPS-180 SHA-256, SEC2 P-256) and our own captures.

**Redaction note**: no keys, nonces, HMACs, shared secrets, or any captured cryptographic values are
reproduced here — only field names, primitives, sizes, and structure, which is what a from-scratch
implementation needs. Version-specific vendor-binary offsets used as dynamic-analysis anchors live in
a local gitignored note (`docs/protocol/captures/re-runtime-notes.md`), never committed.

## What is confirmed

**Update 2026-07-13 (memory dump + decoded handshake).** A full process memory dump taken during a
live session, cross-read with the same session's TCP/9295 handshake, corrected and firmed up several
points below:
- **The session ECDH curve is P-521 (secp521r1), not P-256.** The client/console `RP-Pubkey` each
  decode to exactly **133 bytes = `0x04` + X(66) + Y(66)** — a P-521 uncompressed point. In the
  client's crypto lib this is **curve id 5** (its curve table holds the P-521 prime `2^521-1`, the
  P-521 `b`, and group order, stored little-endian). This is why anchoring on the P-256 constants
  kept landing in certificate code — P-256 is the *cert* curve; P-521 is the *session* curve.
- **`RP-Hmac` is a full 32-byte HMAC-SHA256** (44-char base64), not truncated.
- The crypto lib is a **full OpenSSL-style TLS stack** (decrypted strings show EVP names, `HKDF`,
  `X25519`/`X448`, `Poly1305`, `BLAKE2`, GHASH), statically linked, with **all strings encrypted at
  rest** (they only appear in the live dump).
- The dump was taken mid-stream, so the **transient handshake material (nonce, ephemeral keys) had
  already been freed** — only persistent state (e.g. the registration id) remained. Extracting the
  session keys needs a dump taken *at the handshake*, or dynamic observation of the derivation.

**Primitives in use:**

| Layer | Primitive | Evidence |
|---|---|---|
| Key agreement | **ECDH on P-521 (secp521r1)** | `RP-Pubkey` = 133-byte `0x04`-prefixed P-521 point (both directions); curve id 5 in the client lib's curve table |
| Hash | **SHA-256** | present in the client's crypto lib; standard FIPS-180 constants |
| Message auth | **HMAC-SHA256** (`RP-Hmac`) | 32-byte value on `/sess/init`; the request-side `RP-Hmac` is sent before the console pubkey is known, so it cannot depend on the ECDH secret — it is keyed from registration material (`RP-Registkey`) |
| Bulk encryption | **AES-GCM** (AEAD) | capture payload shapes (per-packet tag-sized trailer) match GCM; the lib's AES uses no S-box/T-table/AES-NI (constant-time, likely vector-permute) |

**Handshake material available to the key schedule** (all observed on the wire, per
`ps5-session-establishment.md`): the client and console ECDH public keys (`RP-Pubkey`, both
directions), `RP-Nonce`, the `RP-Registkey`, the `RP-DevACha`/`RP-DevAChaTag` pair (reads as an AEAD
blob + tag), and the larger `RP-Data`/`RP-Tag` session-parameter payload. ~~Plus the cloud-tier seeds
(`data1/2/3` in the wake `commands`, and `skey` in `sessionMessage`) identified in
`ps5-cloud-session-api.md` as the lead toward direct-session key agreement.~~

> **Superseded.** The cloud-seed hypothesis was wrong, or at least unnecessary: the direct-session key
> agreement we actually solved (`v1`) derives entirely from `RP-Registkey` + the console's `RP-Nonce`, and
> consumes **no** cloud-delivered material. A `v1` session establishes correctly with the cloud tier absent
> entirely (confirmed against a console with no internet access). What `data1/2/3` and `skey` are actually
> for is therefore **open [X]** — they plainly exist and are high-entropy, but nothing in our evidence ties
> them to session key agreement, and we have not established their role. They may belong to the account
> path rather than the session path.

**Read as** an ECDH key agreement with HMAC/challenge key-confirmation, feeding a KDF that produces
per-channel AEAD keys — consistent with standard session-key-establishment schemes in the public
crypto literature. Nothing above is specific to the vendor beyond which header carries which piece.

## What this document left open (answered in `ps5-v2-control-plane.md`)

- The **key schedule**: exactly how the ECDH shared secret + `RP-Nonce` + `RP-Registkey` (+ any cloud
  seeds) combine — which KDF (HKDF-SHA256 vs. a bespoke hash construction), what salt/info/labels,
  and the output key/IV lengths per channel.
- The **`RP-Hmac` construction**: what is keyed on what. (Not "and the truncation" — it is not truncated;
  see the 2026-07-13 update above. This line contradicted that finding and has been corrected.)
- The **AEAD keying/nonce per channel**: how the per-channel key and the GCM nonce (base ⊕ counter?)
  are formed, and what the AAD covers.
- **First-time registration** crypto (the separate PIN-based "Link Device" flow), still uncaptured.

Statically, these are obscured by design: bitsliced AES (no constants to anchor), EC operations
dispatched through per-curve **function pointers**, a **separate** hash instance from the cert path,
and key state carried in context objects — so constant-anchoring lands in the certificate/signature
machinery (RSA-PSS/OAEP, X.509), not the session key schedule. See the research log entry (2026-07-13).

## How the internals were to be derived (independent, first-party dynamic analysis) — **never executed**

> This plan was overtaken by the 2026-07-18 pivot and never carried out. No TTD recording of this handshake
> was ever made. It is kept because the method is sound and would still be the right approach if the newer
> client's control plane is ever revisited — not because it describes work that happened.

Watch our **own** client run one real session and observe the derivation directly, then validate the
recovered schedule offline against our own captures:

1. Record a session with **Time Travel Debugging** (`tttracer`, built into Windows) — a full,
   replayable instruction/memory trace of one short LAN session to our own console.
2. Analyze the recording **non-interactively** (scripted `cdb`): break at the ECDH setup and the
   network-send path, dump the shared secret, the KDF inputs/outputs, the per-channel keys, and the
   `RP-Hmac` inputs.
3. Reconstruct the schedule in our own words here (field tables + step list), no vendor code copied.
4. **Validate**: recompute the schedule from the captured handshake inputs and confirm it reproduces
   the captured `RP-Hmac` and decrypts the captured video channel — closing the loop entirely on our
   own data.

TTD recordings capture live credentials/keys and are handled exactly like raw captures: stored
locally only, **gitignored, never committed**.

## Implementation seam

This document, once complete, is the spec that `IHalyardSessionCrypto` (`Ripcord.Protocol.Halyard.Common`)
and the registration path (`HalyardRegistrationClient`, `IHalyardRegistrationTransport` in
`Ripcord.Protocol.Halyard`) are implemented from — replacing `PassthroughHalyardSessionCrypto`.
Nothing here is implemented by reading another Remote Play project's source.
