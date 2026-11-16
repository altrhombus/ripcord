# The v2 control plane (PS5)

`v2` is our name for the control-plane scheme the PS5 speaks when a pairing holds the right key material: an
AES-GCM envelope over the `/sess` exchange, keyed from an ECDH exchange in the HTTP headers, with a key that
rolls from session to session. `v1` is the per-field AES-CFB scheme in
[`ps5-remoteplay-v1-spec.md`](ps5-remoteplay-v1-spec.md). The labels name the scheme, not a vendor application
version. A PS4 speaks v1 only; a PS5 speaks either.

The sources are set out in [`NOTICE`](../../NOTICE); each source is recorded in
[`../protocol-research-log.md`](../protocol-research-log.md). The reference is the vendor's 8.5 Windows client:
our own captures of it against our own console, hook logs of its key material, and static analysis of its
control DLL. Vendor code is cited by RVA (image base `0x180000000`). Tags as in the v1 spec: **[V]** verified
against a console, **[W]** observed on the wire, **[C]** read from our own copy of the binary, **[X]** assumed.

**Where Ripcord stands.** The dotnet client implements this whole document behind an opt-in switch
(`RIPCORD_V2_HANDSHAKE`). On our own console, our client's registration, `/sess/init`, `/sess/init2` and
`/sess/ctrl` are accepted, the RPCS channel runs both ways, the bandwidth probe runs, and the stream association
reaches the console's session reply **[V]**. The console's stream-key signature then fails to verify, so **v2
has not streamed from Ripcord's own client**. The client now offers the stream protocol versions the vendor
offers and sends the field the vendor sends (§6); that change has not met hardware.

Section numbers are stable; code comments cite them.

---

## 1. Pairing / registration (PIN route)

- The registration request is v1's 587-byte body, unchanged **[W]**.
- `RP-Hmac = HMAC-SHA256(K16_reg, PIN as u32 little-endian)` **[V]** (byte-exact on four of the vendor's
  sessions; accepted from our own client). `K16_reg` is a fixed 16-byte value the client selects by console
  family and pairing mode (`FUN_1801ae9a0`, selectors `FUN_180195120`/`FUN_180195240`) **[C]**. The PS5 value
  is the bundled `registAuthKey` (`NOTICE`, "Interoperability constants"). A second value exists for another
  family; which family is **[X]**, and it is not bundled.
- **The console advertises `RP-SupportGcm` in its registration reply** when it speaks v2 **[W]**. That is the
  signal a client keeps: Ripcord selects v2 only for a pairing made by PIN whose reply carried it, or one that
  already holds a rolled key (§2). Pairing through the account route never selects v2.
- **A PS4 is v1 only** **[V]**: against a PS4 the vendor client uses `/sie/ps4/rp/sess/…`, no `init2`, none of the
  v2 headers, and v1 field encryption, and all three steps succeed.

## 2. The session key and how it rolls

- The key behind `/sess/init`'s `RP-Hmac` and the control KDF (`K16` below) is the pairing's stored token
  **[C]** (session `+0x90`; readers `FUN_1801e3e40`, `FUN_1801e3700`, `FUN_1801e2bd0`).
- Each session the client draws a fresh 16 random bytes, sends them sealed as `init2`'s `RP-VerifyKey`, and stores
  them as the next session's key **[W]** (three consecutive vendor sessions). The first session after pairing has
  no stored token and uses `K16_reg` **[W]**.
- A persisted rolled key is accepted on a later session without re-pairing **[V]** (vendor client, LAN).
- When client and console disagree about the key, what the console does is **[X]**. Ripcord stores the new key only
  once the session is accepted; the vendor's exact commit point is **[X]**.

## 3. Choosing v1 or v2 per session

`FUN_1801b8850` picks the init flavour and `FUN_1801e3e40` builds it **[C]**:

- **v2 init** when a token is held: `RP-Registkey`, `Rp-Version`, `RP-SupportCmd`, `RP-Pubkey`, `RP-Hmac`,
  `RP-Feature`.
- **`init2`**: `RP-VerifyKey`, sealed in `RP-Data`/`RP-Tag`, when the new key is non-zero.
- **v1 init** otherwise.
- **Fallback:** a v2 attempt refused with `0x80108d6c` is retried as v1 **[C]**.

## 4. The control plane

- **`/sess/init` request:** `RP-Registkey`, `RP-Pubkey` (P-521, 133 bytes), `RP-Hmac = HMAC-SHA256(K16, RP-Pubkey)`,
  `RP-SupportCmd`, `RP-Feature`. **Response:** `RP-Nonce`, the console's `RP-Pubkey`, `RP-Hmac` over it under
  the same key, and `RP-DevACha`/`RP-DevAChaTag` (§12) **[W]**.
- **Keys.** `Z` = the P-521 ECDH shared X coordinate (66 bytes).
  `sk_p = HMAC-SHA256(Z, 01 ‖ p ‖ 00 ‖ K16 ‖ 01 ‖ 00)`, with `p = 0x09` client to console and `0x08` console to
  client **[V]**.
- **Envelope.** AES-128-GCM, key `sk_p[0:16]`, a 16-byte IV
  `HMAC-SHA256(N16, sk_p[16:32] ‖ be64(counter))[0:16]` **[V]**. `N16` is the bundled `contextKeys.selectorOne`.
- **The counter restarts at 0 on every HTTP connection** **[W]**: every message observed is sealed at counter 0,
  so `init2` and the `ctrl` request use the same key and IV. A client must do the same to be accepted. (This is a
  property of the protocol; Ripcord reproduces it because the console requires it.)
- **AAD:** for `init2`, the request's header lines other than `RP-Data`/`RP-Tag`, CRLF-joined with a trailing CRLF;
  for `ctrl` request and response, empty **[V]**.
- **Plaintexts.** `init2` carries `RP-VerifyKey: <base64>`. The `ctrl` request carries v1's header set, base64'd
  but not field-encrypted: the envelope replaces v1's per-field CFB. The `ctrl` response carries `RP-Prohibit`,
  `RP-Device`, `RP-Enterkey`, `RP-Server-*`, `RP-Vmode` and others **[W]**.
- **Values sent** (from the vendor's sessions): `RP-ClientType 11`, `RP-OSType Win10.0`, `RP-Version 1.0`,
  `RP-SupportCmd 120000`, `RP-StartBitrate 0`, `RP-Feature 1` **[W]**. What each bit means is **[X]**; the
  values are stable and sent as observed.

## 5. The launch spec

- `launchSpecJson` is base64 of AES-128-OFB under the v1 control KDF's key and material (`RP-Nonce`, `RP-Key`),
  IV `HMAC-SHA256(N16, material ‖ be64(0))[:16]`, **counter 0** (v1 uses 5, after its five field-encrypted
  headers) **[W]**.
- The JSON is v1's template **[W]**.
- **`handshakeKey` [X].** The console signs its stream public key with the 16-byte value the vendor client uses
  as its stream `handshakeKey` (§7, **[V]**), and the vendor's launch-spec builder (`FUN_18009ee50`) writes a
  client-random value **[C]**. Our decrypts of two vendor sessions nevertheless show the v1 template without a
  `handshakeKey` field. How the console learns the value on this path is not established. Ripcord sends a fresh
  random `handshakeKey` in the launch spec, as v1 does.

## 6. SESSION_REQUEST

- On the v2 path the vendor offers stream protocol versions 9, 10, 11 and 13–20 and the console acks 20 **[W]**.
- The request carries `clientVersion = 20`, fields 1–6 as v1 (field 4 empty, field 5 the 133-byte P-521 public
  key, field 6 the 32-byte signature), and **field 7 = the string `InvalidSessionId`** **[W]**. What field 7
  means is **[X]**. Version 20 uses P-521 **[W]**; 18 and 19 are **[X]**.
- Ripcord's v2 path sends the same offer and field 7. Not yet run on hardware.

## 7. Stream key agreement

- Per direction, `key ‖ IV = HMAC-SHA256(Z_stream, 01 ‖ dir ‖ 00 ‖ handshakeKey ‖ 01 ‖ 00)`, `dir` 2 client to
  console, 3 console to client **[V]** (byte-exact on vendor sessions). This is v1's schedule.
- `ecdhSignature = HMAC-SHA256(handshakeKey, publicKey)`, for both sides' stream public keys **[V]** (the console's
  signature reproduces exactly on a vendor session). The stream `Z` is a separate ECDH from the control plane's.
- `handshakeKey` is 16 bytes the client chooses **[C]**: the vendor's stream-crypt setter (`FUN_18011a160`) only
  base64-decodes a 16-byte value, and nothing in the control plane derives it.

## 8. Per-packet stream crypto

- **A/V header:** v1's layout moved 8 bytes later **[W]**: type at 0 (2 video, 3 audio; with `0x10` added over
  the internet), packet index at 5–8, frame index at 11–12, the packed unit word at 13–16, codec at 17, GMAC tag
  at 18–21, keystream offset at 22–25, the 3-byte video prefix at 26–28, AES-CTR payload from 29. Bytes 1–4 and
  9–10 are counters whose meaning is **[X]**.
- **GMAC, CTR and FEC are v1's** **[V]**: the rotating GMAC key and its window rule
  (`ps5-remoteplay-v1-spec.md` §5.4), the little-endian CTR counter at `base_iv + keystream_offset/16 + 1`, and
  the Cauchy Reed-Solomon FEC over the decrypted units (v1 spec §6.2), each reproduced byte for byte on vendor sessions.
- **Client to console** rides the same Takion framing as v1 and authenticates under the client-direction key
  **[V]**. Controller input uses v1's input packets with the header moved: keystream offset at 12, GMAC at 16,
  payload at 20; input events carry one extra trailing byte **[W]**. The loss report is the 23-byte form.

## 9. RPCS: the control connection after `ctrl`

- After `/sess/ctrl` the control connection stays open and carries `RPCS` messages, each sealed under the control
  keys (client `0x09`, console `0x08`) with the counter continuing from `ctrl` **[V]**.
- **Frame:** `"RPCS" ‖ be32(inner length + 16) ‖ ciphertext ‖ tag16`, AAD = the 8-byte header; inner =
  `be64 seq ‖ be32 payload length ‖ be16 command ‖ be16 flags ‖ payload`; a reply sets bit `0x8000` **[W]**.
- **Commands seen:** console to client `0x0005`, `0x0033` (a session-id string), `0x0017`, `0x0016`, `0x0041`;
  client to console `0x000d` (carries the MTU, 1454) and `0x0036` (periodic, a keepalive) **[W]**. The console's
  incoming dispatch (`FUN_1801e5000`) handles 0x10, 0x15, 0x16, 0x17, 0x21, 0x22, 0x24, 0x26, 0x32, 0x33, 0x34,
  0x35, 0x41, 0x61 and 0xfe **[C]**. What most commands mean, and which of them a stream needs, is **[X]**.

## 10. Bandwidth probe

Unchanged from v1 in shape (`bandwidth_probe.proto`): after the association and SESSION_REQUEST, the console sends
test data and periodic reports whose target bitrate climbs in 2 Mbps steps; the client acknowledges **[W]**.
The MTU/echo phase and the report's other fields are **[X]**.

## 11. Takion

The association handshake and chunk framing are v1's, byte for byte **[W]**. `clientVersion` lives in the DATA
protobuf, not in any SCTP field. Control on UDP 9297, A/V on 9296 on the LAN.

## 12. `RP-DevACha`

The console sends `RP-DevACha`/`RP-DevAChaTag` in the `init` response. The 8.5 client has no code that answers it
**[C]** and streams regardless, on the LAN and over the internet **[W]**. Ripcord does not answer it either.

## 13. Open items

- Why the console's stream signature fails for Ripcord's own client (the note at the top, §5, §6).
- The `handshakeKey` path on v2 (§5).
- The RPCS send schedule and which incoming commands gate the stream (§9).
- Key desync behaviour (§2).
- The bit meanings of `RP-SupportCmd`, `RP-Feature` and the other enumerated values (§4).

## 14. (Not used)

## 15. The internet path

Over the internet everything rides UDP and there is no TCP 9295 **[W]**. The `/sess` exchange is the same HTTP
codec carried over a reliable association on UDP 9303 (the same headers and the same GCM envelope on `ctrl`), and
after `ctrl` that association carries RPCS. A/V, the bandwidth probe and input ride a second association to UDP
9297. Control and stream crypto are unchanged; the A/V type byte carries `0x10`. A PS4 uses the identical
transport carrying v1 **[W]**. The relay path is unseen **[X]**. `ps5-cloud-session-api.md` covers the
rendezvous that sets this up.
