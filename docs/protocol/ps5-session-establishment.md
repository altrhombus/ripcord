# PS5 Remote Play — session establishment (HTTP-over-RUDP exchange)

> **HISTORICAL — this is the `v2` control plane, which Ripcord does not implement.** These captures
> (2026-07-10) were taken against the *then-current* vendor client, whose HTTP handshake carries an ECDH
> exchange in `RP-Pubkey`/`RP-Hmac` plus `RP-DevACha`/`RP-DevAChaTag`. On 2026-07-18 the project pivoted to
> the older, unobfuscated client, whose control plane is materially different — `RP-Registkey` + `RP-Nonce`
> → KDF → AES-128-CFB, **no public key in the HTTP handshake at all**. That one is specified in
> [`ps5-remoteplay-v1-spec.md`](ps5-remoteplay-v1-spec.md) and is what ships.
>
> This document is retained as an accurate record of what was *observed* on the wire. It was never
> reverse-engineered: the header set and shapes below are real, the constructions behind them are not known.
> Individual statements that were later superseded are marked inline. Note that `RP-Version: 1.0` appears
> here too — it is a console-family value, not a generation marker (see `README.md`).

Status: draft, based on five capture sessions now (see provenance log) - a reconnect, a genuinely
first-time connection from a client device that had never spoken to this console before ("online
pairing", no on-console PIN involved), a locally-discovered connection to a console with no
internet access, and a WAN/relay-addressed connection from a different network. All produced the
**identical three-step exchange described below** (field names/structure), including the client
presenting a valid `RP-Registkey` on its very first packet to the console whenever registration was
presented at all. See "Where this sits in the bigger picture" for what that implies. A **separate,
PIN-based/local "Link Device" pairing flow** (a first-party PS5 feature, distinct from the
account-SSO "online pairing" every capture so far has used) is still uncaptured - see the to-do
list.

This exchange has now been observed over **two different transports**: RUDP-wrapped UDP (port
9303, this document's primary subject) and plain TCP (port 9295, used for a locally-discovered
connection to an offline console - see `ps5-local-discovery.md`). The HTTP-level structure
documented below is identical either way; only the framing underneath differs.

Source: own packet captures of network traffic both on the author's own LAN and from a separate,
external network, between a real PS5 console and a connected client, at IP/port level only.
Captures and this document written 2026-07-10. No source code from any existing Remote Play client
project was consulted for this document - see `docs/protocol-research-log.md`.

**Redaction note**: header/body *values* below (keys, tokens, HMACs, nonces) are never reproduced
verbatim - they're real cryptographic material belonging to a real console/account. Only field
*names*, *encodings*, and *approximate sizes* are documented, which is what a from-scratch
implementation actually needs.

## Overview

Three HTTP/1.1-style request/response exchanges were observed, carried inside the RUDP framing
described in `ps5-session-transport.md`, all directed at the console's LAN IP on the control port
(9303):

1. `POST /sie/ps5/rp/sess/rgst HTTP/1.1`
2. `GET /sie/ps5/rp/sess/init HTTP/1.2` (note: `1.2`, not `1.1` - observed exactly this way, not a
   transcription error)
3. `GET /sie/ps5/rp/sess/ctrl HTTP/1.1`

After step 3 completes, the exchange transitions to the binary `RPCS` control frames (see the
transport doc) - no further HTTP-style messages were observed afterward in this capture.

**Where this sits in the bigger picture** (updated in a 2026-07-10 deep-dive pass with two more
captures, see `ps5-network-architecture.md`): this console-local handshake is only the last of
three tiers. It is preceded by PSN-cloud authentication over HTTPS and by a persistent WebRTC
(ICE + DTLS) signaling channel to a PSN relay server. Registration is fundamentally a cloud/account
operation, not a console-local one: the client authenticates to PSN and coordinates the session
through cloud APIs before ever touching the console directly.

**Correction (2026-07-12, TLS-decrypted capture, see `ps5-cloud-session-api.md`)**: an earlier
version of this note said the `RP-Registkey` was "obtained/refreshed from a PSN cloud
key-management service during sign-in", pointing at a host whose name contains `.km.`. With that
tier now decrypted, the `.km.` host is an account-info endpoint (identity/region/age), **not** a
key service, and **no registration key is fetched from the cloud on a reconnect at all** - the key
is held client-side after first pairing and simply presented. So where the `RP-Registkey`
originates is *not* observed in any reconnect capture (cloud or direct); it is bound up with the
still-uncaptured first-time/PIN pairing flow. The rest of the conclusion stands.

**What is confirmed**: a capture of a brand-new client device's *first-ever* connection to this
console (signed into the same PSN account, no on-console PIN - what the PS5 UI calls
automatic/"online" pairing) showed the exact same three-step exchange as the reconnect capture, and
the client presented a valid `RP-Registkey` on its very first packet to the console - there was no
different/extra step for "this device hasn't talked to this console before." **Conclusion**: for
account-SSO ("online") pairing, there is no console-local wire-level distinction between first
connection and reconnect - both look like this document. The genuinely different, still-uncaptured
case is the PS5's separate **PIN-based "Link Device" pairing** (Settings → System → Remote Play →
Link Device on the console), which is a first-party feature distinct from account-SSO pairing; that
capture is still a to-do.

The client also knew the console's LAN address without any LAN discovery broadcast in any
cloud-mediated capture - the console was separately visible via mDNS as `PS5-<6 hex>.local`, and/or
the address came from the cloud/signaling tier. The exception is a genuinely local-only connection
to an offline console, which used an explicit `SRCH`-style UDP broadcast instead - see
`ps5-local-discovery.md`, which also documents `RP-Registkey` being presented with **no** preceding
`/sess/rgst` step at all in that case (the three-step exchange became a two-step one). Whether
`/sess/rgst` is skippable depends on something not yet isolated - the current best guess is the
control transport chosen (`/sess/rgst` appeared on the RUDP/UDP-9303 path and was skipped on the
LAN-direct TCP path; see `ps5-local-discovery.md`).

**WAN/relay addressing note**: in a capture of a relay-addressed WAN connection (see
`ps5-wan-relay.md`), the `Host:` header in steps 2 and 3 carried the **relay's** address rather than
the console's - the client never needs or has the console's real address at all when a session is
relay-mediated. All three steps and every header name/structure below were otherwise identical to
the LAN reconnect case, including presenting the same registration key.

## Common headers

Every request carried:
- `Host: <client's own LAN IP>` (steps 1) or `Host: <console LAN IP>:9303` (steps 2-3) - inconsistent
  between requests in the same session, not fully understood yet.
- `User-Agent: remoteplay <platform>` - `Windows` in one capture, `OSX` in another (different
  client devices/OSes were used across capture sessions), i.e. this is a per-platform string, not a
  fixed literal.
- `Connection: close` (steps 1-2) or `keep-alive` (step 3 onward)
- `Content-Length: <n>`
- `RP-Version: 1.0` (or `Rp-Version` - capitalization was inconsistent between requests)

## Step 1 — `POST /sess/rgst`

Request headers beyond the common set:
- `RP-SupportCmd: 130000` - looks like a bitmask/version code advertising client capabilities;
  same literal value appeared in step 2's request too.
- `RP-Hmac: <base64>` - ~~length consistent with a truncated (not full SHA-256) HMAC.~~
  **Superseded 2026-07-13:** it is a *full* 32-byte HMAC-SHA256 (44-char base64), not truncated — the
  original reading was an eyeball estimate of the base64 length. See `ps5-session-crypto.md`.

Request body: binary, `Content-Length: 587` in the observed exchange, no plaintext structure
visible (consistent with being encrypted or containing high-entropy key material).

Response: `200 OK` with:
- `RP-SupportGcm: 1`
- `RP-Feature: 3`
- Binary body, `Content-Length: 195` in the observed exchange.

### `/sess/rgst` transport crypto — the no-PIN (account/"web") route `[C]`

The 587-byte request body and 195-byte response are **field-cipher** (`HalyardControlFieldCrypto`:
`IV = HMAC-SHA256(contextKey, material ‖ be64(counter))[:16]`, AES-128-CFB, bundled `contextKey`). The
request body = a 480-byte plaintext **context** + a 107-byte encrypted field; the response = the encrypted
field block carrying `PS5-RegistKey`, `RP-Key` (companion), nickname, MAC, `RP-KeyType`, `RP-SupportCmd`.

The transport key is `key' = seed XOR registrationTable[ context[0x18d] & 0x1f ]` — the no-PIN analog of the
PIN route (where the PIN folds into `registrationTable[sel]`; here a per-registration **seed** XORs the whole
entry). `registrationTable` and the selector are the ones already in `HalyardRegistrationKdf`. Verified by
decrypting a live `/sess/rgst` request offline (blocks 1..n of CFB are IV-independent, so `key'` validates
outright) and, separately, the response.

**Where the seed comes from — SOLVED end-to-end 2026-08-31.** The seed is **not derived locally; the console
delivers it, encrypted, over the PSN cloud** (this is why every local-derivation hunt failed and the seed
appears in no plaintext channel). The full chain, all `HalyardControlFieldCrypto`:

1. The client generates two **ephemeral 16-byte** values and sends them in the cloudAssistedNavigation
   `commands` call as **`data1`** (field-cipher **key**) and **`data2`** (field-cipher **material**) —
   see `ps5-cloud-session-api.md`.
2. The console generates the seed, field-encrypts it with `HalyardControlFieldCrypto(key = data1,
   material = data2, counter = 0)`, and returns the ciphertext as **`customData1`** (double-base64) on the
   `rps:customData1:updated` push channel.
3. Client: `seed = HalyardControlFieldCrypto.Decrypt(data1, data2, 0, base64⁻²(customData1))`, then
   `key' = seed XOR registrationTable[selector]`.

Confirmed byte-exact against the vendor client's `session+0x10` on multiple fresh registrations. The long
trail of ruled-out hypotheses (client RNG, scenp/TLS ECDH, the RP control-plane ECDH, the npticket cookie,
every local KDF) is in the dirty-room note; the answer was that the seed rides the wire as `customData1`
ciphertext. **Ripcord account route:** generate `data1`/`data2` → send in the `commands` call → read
`customData1` from the push channel → field-decrypt → seed → `key'` → decrypt `/sess/rgst` → store
`RP-Registkey` + companion. No new primitives.

## Step 2 — `GET /sess/init`

Request headers beyond the common set:
- `RP-Registkey: <hex string>` - the registration key/identifier for this console+account pairing,
  obtained from the cloud (see above). Structurally, the hex string decodes to further ASCII hex
  digits - i.e. it reads as a short (roughly 8-byte) binary identifier that has been hex-encoded
  twice, not a raw high-entropy key. Consistent across both a reconnect and a brand-new device's
  first connection in the two captures analyzed - it is scoped to the account+console pair, not to
  the client device.
- `RP-SupportCmd: 130000`
- `RP-Pubkey: <base64>` - decodes to a byte string starting with `0x04`, the standard uncompressed
  elliptic-curve point marker (RFC 5480 §2.2) - i.e. this is an uncompressed EC public key, not an
  opaque blob. ~~Curve not yet identified from the capture alone (would need the decoded point's
  byte length to narrow down - not measured in this pass).~~ **Settled 2026-07-13:** the point is
  exactly 133 bytes (`0x04` + X(66) + Y(66)) — **P-521 (secp521r1)**. See `ps5-session-crypto.md`.
- `RP-Hmac: <base64>`
- No body (`Content-Length: 0`).

Response: `200 OK` with:
- `RP-Nonce: <base64>`
- `RP-Version: 1.0`
- `RP-SupportCmd: 134200` - a *different* value than the client advertised, presumably the
  console's own capability set.
- `RP-Feature: 1`
- `RP-Pubkey: <base64>` - same `0x04`-prefixed uncompressed EC point structure as the client's,
  i.e. the console's half of what reads as an ECDH key exchange.
- `RP-Hmac: <base64>`
- `RP-DevACha: <base64>`
- `RP-DevAChaTag: <base64>` - name suggests this pairs with `RP-DevACha` as an authentication tag
  (e.g. AES-GCM tag) over it, but not confirmed.
- No body.

**Read as**: an ECDH-style key agreement, client and console each contributing a public key plus
authentication material (HMAC/challenge/tag headers), consistent with deriving a shared session
key without transmitting it directly. This matches the general shape of session-key-establishment
schemes documented in the public cryptography literature (ECDH + key confirmation via
HMAC/challenge-response) - nothing here is specific to Sony's implementation choices beyond which
headers carry which piece.

## Step 3 — `GET /sess/ctrl`

Request headers beyond the common set:
- `Connection: keep-alive` (switches from `close` here on)
- `RP-Data: <base64, several KB>` - much larger than any previous field; likely an encrypted
  session-parameter payload (stream config, codec negotiation, etc.) rather than key material at
  this size.
- `RP-Tag: <base64>` - likely an authentication tag over `RP-Data`.

Response: `200 OK` with:
- `Pragma: no-cache`
- `RP-Data: <base64>`
- `RP-Tag: <base64>`

After this response, the RUDP layer switches to the binary `RPCS` frames described in
`ps5-session-transport.md`. No further HTTP-style requests were observed.

## What's still needed

- **PIN-based/local "Link Device" pairing capture** - the one pairing mode genuinely not yet seen,
  across five captures now. Account-SSO ("online") pairing is thoroughly covered (reconnect,
  first-ever device, offline-console/local, and WAN/relay); the console's separate Link Device/PIN
  flow is a different, first-party feature that may behave differently (possibly console-local
  rather than cloud-mediated, especially since it's reachable without the console having internet
  access per the console's own settings UI) and still needs its own session.
- Decode the exact byte layout of `RP-Data`/`RP-Tag` in step 3 (would need either the console's
  private key or a second capture with more context to make progress here).
- Identify the EC curve used for the `RP-Pubkey` exchange from the decoded point length.
- Isolate what actually determines whether `/sess/rgst` is sent at all - present in the reconnect,
  first-ever-device, and WAN captures; absent in the local/offline-console capture. See
  `ps5-local-discovery.md`.
- WAN/PSN-relay path: confirmed via a full relay-addressed session capture to carry real media
  traffic, not just signaling - see `ps5-wan-relay.md` for the full writeup. Still out of scope to
  implement per the plan (LAN-only for Phase 1).
