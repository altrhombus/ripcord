# PS5 Remote Play — v1 (older protocol) full traffic specification

Status: **implementation-ready for the crypto + control plane; transport reliability wire-validated against
a live LAN capture (Takion-internal SCTP INIT/COOKIE/SACK — not the binary's secondary RUDP module).**
Scope: the older PS Remote Play protocol (client build ~5.5.0.8250, the client control library), which Ripcord
implements as **v1** for LAN play — meaning a control plane keyed from `RP-Registkey` + `RP-Nonce` with **no
public-key exchange in the HTTP handshake**. The newer client's `RP-Pubkey`/`RP-Hmac` control plane (our
**v2**) was observed but never reverse-engineered; see `README.md`, "Protocol generations".

> **`v1` is our label, not the `RP-Version` header.** `RP-Version` is a console-family value — `1.0` on PS5,
> `10.0` on PS4 — and `1.0` appears in the newer generation's captures too. Do not read `RP-Version: 1.0`
> below as "this is v1"; it is not the discriminator.

This document is **clean-room**: the substance is derived from our own copy of the client control library
(static Ghidra RE + the embedded protobuf reflection metadata) and our own live packet/memory captures. No
vendor code is reproduced. A small number of values remain **assumptions** rather than findings; each carries
the **[X]** tag so it cannot be mistaken for settled. The raw RE trace lives in
`captures/lab-notebook.md` — part of the gitignored dirty room, so it is **not present in a published copy** of
this repository.

**Redaction.** Values tied to one console, account, session or network are replaced here with clearly
synthetic stand-ins, on the same policy as `../protocol-research-log.md`: registration keys and pairing
material, and any address that identifies real hardware (public IPv4 is shown as
[RFC 5737](https://www.rfc-editor.org/rfc/rfc5737) documentation space, and the one IPv6 endpoint token in
§8 as a synthetic ULA). Generic protocol constants — field offsets, discovery protocol versions, RVAs into
the vendor binary — are **not** redacted: they are identical for every console and every user, and they are
the substance of the document. Where a passage explains how one encoding derives from another, the
stand-ins are internally consistent so the derivation still reads correctly.

**Naming & IP note.** All message/field/enum/type names in this spec and the `.proto` files are our own
descriptive labels — the vendor's coined names, symbol names, and log strings are deliberately not
reproduced (a private vendor↔ours mapping lives in the gitignored dirty room, `captures/name-mapping.md`).
What *is* reproduced verbatim is limited to **on-the-wire interoperability facts** that the console parses
by value and that therefore cannot be changed: protobuf **field numbers / enum values / wire types**, the
JSON config **keys** (`handshakeKey`, `sessionId`, `streamResolutions`, `disableRPEncryption`, …), and the
HTTP **header names** (`RP-Auth`, `RP-Did`, `RP-Nonce`, `RP-OSType`, …). Vendor code is cited by relative
virtual address (`FUN_<rva>`) only. This is not legal advice; ship-time review by counsel is still
warranted (see the DMCA §1201 / interoperability discussion in the project notes).

Confidence tags used below: **[V]** verified byte-for-byte against a live capture · **[C]** confirmed from
our decompiled code · **[W]** observed on the wire · **[I]** inferred / not yet pinned · **[X]** assumed —
adopted provisionally and never confirmed against the console. An `[X]` value with no accompanying
`[C]`/`[V]`/`[W]` tag is **not settled**: it is a working assumption that happens to interoperate, and each
one is a candidate cause if something misbehaves. The open list is in the roadmap.

---

## 0. How to test this spec (test-vector index)

Every cryptographic layer has a self-checking pure-Python reference under [`captures/`](captures/). Run
them (needs `pip install cryptography`); each prints `OK`:

| Layer | Reference | What it checks |
|-------|-----------|----------------|
| Control session KDF | `kdf_reimpl.py` | `out1,out2 = KDF(nonce‖companion)` vs live capture **[V]** |
| Per-field IV | `field_iv_reimpl.py` | `IV = HMAC-SHA256(ctxkey, out2‖be64(ctr))[:16]` vs 2 live triples **[V]** |
| `/sess/ctrl` field encryption | `rp_auth_reimpl.py` | full `RP-Auth`+`RP-OSType` reproduce wire-confirmed values **[V]** |
| Streaminfo config | `streaminfo_reimpl.py` | `AES-128-OFB(out1)` regenerates the captured keystream **[V]** |
| Stream key schedule + GMAC | `stream_crypto_reimpl.py` | ECDH→KDF→per-packet GMAC round-trip + rotation **[C]** |
| Stream key derivation vs hardware | `LiveStreamKeyVectorTests` | `DeriveDirection` reproduces the dumped console send/recv keys **[V]** |
| Registration framing | `RegistrationMessageTests` | endpoint/token/HTTP framing + pairing-record parse (structural, synthetic) **[C]**; body cipher **[I]** |

`kdf_reimpl.py` needs the client control library beside it (gitignored). Protobuf schema:
[`stream_control.proto`](stream_control.proto) + [`bandwidth_probe.proto`](bandwidth_probe.proto) (compile with `protoc`). A decrypted
sample streaminfo is at [`captures/streaminfo_config_decrypted.json`](captures/streaminfo_config_decrypted.json).

---

## 1. Architecture & layering

```
 Discovery / wake       UDP 987 / 9302   (existing Ripcord code; out of scope here)
 Registration (1-time)  HTTP/1.1 ── POST /sie/ps5/rp/sess/rgst  ── PIN route: TCP 9295 · account route: UDP 9303  (§2.0)
 Session control        HTTP/1.1 over TCP 9295   ── /sess/init, /sess/ctrl  (§2.1, encrypted headers)
 Streaming transport    Takion over a single UDP socket (negotiated port; 9297 observed)   (§3)
   │  reliability        Takion-internal: INIT/COOKIE handshake + DATA/SACK (§8, wire-validated [V])
   ├ control channel     control-plane protobuf messages (ControlMessage), reliable   (§4)  ── handshake, streaminfo, input
   └ data channels       A/V packets + 4-byte GMAC, unreliable + FEC       (§6)  ── video / audio / FEC
       (all multiplexed on the one socket, distinguished by the base-type byte; §3)
 Stream security        ECDH-P256 · HMAC-SHA256 · SP800-108 KDF · AES-128-GCM key-rotation   (§5)
```

Two independent crypto systems:
- **Control (`/sess/ctrl`)** — symmetric, keyed by a per-connection KDF over `RP-Nonce`+a pairing
  constant. Encrypts a handful of HTTP header fields (§2).
- **Stream (stream-transport)** — ephemeral ECDH authenticated by the streaminfo's `handshakeKey`, feeding
  per-packet AES-128-GCM (§5). The streaminfo config that carries `handshakeKey` is itself encrypted with
  the control key `out1` (§4.3), so the stream layer bootstraps from the control layer.

---

## 2. Session control — registration + HTTP header field crypto

### 2.0 Registration / pairing (one-time, per console)  **[C]**

Before any session, the client must be **paired** with the console; this establishes the two long-lived
secrets the session crypto depends on: the **registkey** and the **companion**.

> **Two registration routes.** Registration can be **PIN-based** (user enters the console's on-screen
> 8-digit passcode; wire transport **TCP 9295**, cap5–cap12) or **account-based / no-PIN** (authorized by the
> signed-in PSN account; wire transport **UDP 9303** in a lighter reliable-UDP framing — `0xC0/0xC1` header,
> 32-bit connection tag, INIT/COOKIE handshake, NAT/holepunch pre-exchange — cap4). Both carry the *same*
> HTTP/1.1 shape and body sizes. The PIN route is the one being reversed (fully wire-visible, own hardware);
> the account route additionally involves the PSN account/token flow and is deferred.

#### 2.0.1 The account (no-PIN) route — what is now established **[C]**, 2026-08-07

Re-examined `app-startup-connect-and-pair-online-first-time-no-pin.pcapng.gz` (cap4). The capture carries a
complete no-PIN pairing — `rgst` → `200 OK` with the pairing record → `init` → `ctrl` → `RPCS` — with **no
traffic on TCP 9295 at all**; the entire control plane runs over UDP 9303 on this route, not just
registration.

**Framing [C].** Each chunk is a 2-byte big-endian header whose top two bits are set and whose low 14 bits are
the chunk length, followed by two 16-bit connection tags, then the payload. Several chunks may be concatenated
in one datagram (the `rgst` POST arrives as a 12-byte chunk followed immediately by an 819-byte one, 831
total). `0xC0…`/`0xC1…` are simply the high bits of that length field rather than distinct message types, which
supersedes the earlier "`0xC0/0xC1` header" phrasing above. Preceded by a 5-packet 88-byte INIT/COOKIE
exchange (types `06`/`07`).

**The request is byte-identical in size to the PIN route's [C].** Body = 587 bytes = the same 480-byte
(`0x1e0`) context plus the same 107-byte encrypted field (`Client-Type: <64 hex>\r\nNp-AccountId:
<base64>\r\n`). Recomputing our PIN-route builder's output gives 587 exactly. So the body *layout* is shared
and does not need separate derivation.

**`RP-Hmac` is a client-version detail, not the route's authorizer.** cap4 (a newer client, `User-Agent:
remoteplay OSX`) sent `RP-Hmac` (44-char base64 = full HMAC-SHA256) and `RP-SupportCmd: 130000` on the rgst
request. **cap63 (the older Windows client, off-network, `NoPasscode`) sends neither** — its rgst request
headers are exactly the set our PIN-route builder emits (`HOST`/`User-Agent`/`Connection`/`Content-Length`/
`RP-Version`). So `RP-Hmac` cannot be *the* no-PIN authorizer: a client that omits it still pairs. It is a
newer-client addition (plausibly the v2 ECDH handshake bleeding into shared header space), and the earlier
"obvious candidate" reading is withdrawn.

**cap63 update (2026-08-07): the account route is confirmed present and dissected, and the "PIN route minus
the fold" hypothesis is now FALSIFIED [X].** cap63 is a single off-network session containing a successful
PS5 and a successful PS4 no-PIN pair (plus two PS4 attempts that died in the RUDP `06`-type INIT with no
server reply — reachability, not auth). Findings, all validated in `NoPinRegistrationVectorTests` against the
gitignored `nopin_registration_vectors.json` fixture:
- **Whole control plane is on UDP 9303** — no TCP 9295 anywhere in the capture. `rgst`, `init`, `ctrl` all run
  over the 9303 reliable-UDP framing.
- **The request is byte-identical to the PIN route** — 480-byte context + 107-byte `Client-Type`/`Np-AccountId`
  field, both PS5 and PS4. The send-side codec is reusable unchanged.
- **The response key is NOT any pinless entry of the bundled registration table.** Using the IV-free CFB
  block oracle (blocks ≥1 decrypt from the key alone; the registkey rides there), all 32 table entries were
  swept for both families with nothing folded in. **Zero produced a parseable pairing record**, cross-checked
  against the ground-truth registkey the client sent on the following `/sess/init`. So the account route is
  *not* "the PIN route with passcode 0", regardless of selector — the same result the RE log reached for the
  v2 case, now shown for the older client's v1 account route too.

**cap64/cap65 update (2026-08-07): the correlation capture was obtained, and the `data1/2` hypothesis is
FALSIFIED.** cap64 (PS5) and cap65 (PS4) are each a first-time no-PIN pair recorded as a Fiddler `.saz` *and*
a raw `.pcapng` **simultaneously**, so the cloud bodies and the 9303 exchange are both readable for one event
— the capture earlier believed impractical. Findings (validated in `NoPinKeyCorrelationTests` against the
gitignored `nopin_correlation_ps5.json`):

- **The cloud `commands` `initialParams` carries `data1` + `data2`, 16 bytes each (no `data3` this time).**
  Neither appears verbatim in the 9303 rgst context.
- **Neither data value keys the registration response [X].** With the PS5 `data1/2` and the ground-truth
  registkey (read from the following `/sess/init`) both in hand, an IV-free CFB block oracle — validated
  first on a known PIN key — was run over the response for every derivation worth trying: each value
  directly, reversed, XORed/fold-4 into the selected table entry, and `SHA-256`/`HMAC-SHA256` in the shapes
  the vendor uses elsewhere (24 candidates). **None recover the registkey.**
- **The PS4 (cap65) settles it structurally.** It completed the same 9303 account-route pairing with **no
  `commands` call at all** — it was found by search rather than the cloud list, so it never received any
  `data1/2` — yet still obtained a registration key. A source the PS4 never saw cannot be the account-route
  key source.

So "the `data1/2/3` authorise the no-PIN pairing" joins "they seed the session crypto" as a **falsified**
reading of these values. The commands data is real, out-of-band, 16-byte material whose role keeps not being
the thing it sits nearest to; treat any future `data1/2/3` hypothesis with corresponding suspicion.

**SOLVED (2026-09-04) — this paragraph is kept only so the reasoning below is not repeated.** The
account-route registration key comes from the seed the console publishes in `customData1`, recovered with
`data1`/`data2`, and wrapped by a transform **distinct from the PIN route's** over the same table
(`HalyardRegistrationKdf.WrapAccountMaterial`; the two routes differ in bias and in the order of the subtract
and the XOR). It is `[V]`: it decrypts three independent captured sessions' `/sess/rgst` fields from first
byte to last, and it pairs and streams live.

The search below was the state before that, and every branch of it was wrong — the key was not in-band 9303
material, nor the OAuth token, nor the session-manager exchange. Recorded because a plausible-sounding search
space that turned out to be empty is worth exactly as much as a positive result to whoever asks the same
question next:

> Because the PS4 had no cloud trigger, the key source must be present in *both* families' flows — which
> points away from the cloud payload and toward either **in-band material** (the 9303 INIT `06` packets carry
> two ~20-byte high-entropy blobs) or **account/session material** common to both (the OAuth token, the
> session-manager rendezvous).

> **The vendor registration tag is DEAD CODE (do not chase it).** The client embeds a `sprintf("<vendor regist tag>_%04d", passcode/
> 10000)` token + a ~63-char secret, referenced only from `FUN_101f5700`. Extensive work established this path
> is **never executed** for PS4 *or* PS5 pairing: a correct software breakpoint at `FUN_101f5700` never fires,
> and the function statically returns error `0x80108d03` (its crypt-init entry points are `-1` stubs). All
> token+secret brute-forcing was against code that does not run. The live crypto is below.

**Wire framing** (both routes, wire-confirmed):
```
POST /sie/ps5/rp/sess/rgst HTTP/1.1     (PS4: /sie/ps4/… wire-confirmed [W] cap53; older fw /sce/rp/… DLL-present)
HOST: <client-ip>                        (uppercase; the CLIENT's own IP, no port)
User-Agent: remoteplay Windows
Connection: close
Content-Length: 587
RP-Version: 1.0                          (10.0 seen for PS4)
                                         <587-byte encrypted request body>
```
Response: `HTTP/1.1 200 OK` · `Connection: close` · `RP-Feature: <keytype>` (PS5; absent on PS4) ·
`Content-Length:` (space-padded) · `<encrypted response body>` (PS5 195 B / PS4 262 B). There is **no**
`Np-AccountId`/`Content-Type`/nonce header — the account identity and all key material live **inside** the
encrypted bodies.

**Response cipher — SOLVED [V]** (recovered via Frida runtime instrumentation, verified byte-for-byte on PS4
and PS5): the response body is **AES-128-CFB** with the *same* field-crypt construction as the `/sess/ctrl`
fields (§2.1):
```
key = out1                                             # the 16-byte AES key (Crypt object +0x1c)
IV  = HMAC-SHA256( context_key, out2 ‖ be64(counter) )[0:16]   # out2 = 16-byte material (Crypt object +0x0c)
plaintext = AES-128-CFB(key, IV).decrypt(body)
```
`context_key` is the §2.1 selector (PS4 → `B_eq_0`, PS5 → `A_in_8_9`). The decrypted response is the pairing
record as `Key: Value\r\n` lines: `PS5-RegistKey`/`PS4-RegistKey` (registkey, hex), `RP-Key` (companion, 16 B
hex), `RP-KeyType`, `PS5-Nickname`/`PS4-Nickname`, `PS5-Mac`/`PS4-Mac`, `AP-Bssid`/`AP-Name`/`AP-Key`,
`RP-SupportCmd`. The client stores `{registkey, companion, keytype}` as the persistent pairing record and
reuses it for every subsequent session (`registkey → RP-Registkey/RP-Auth`, `companion → session KDF`,
`keytype → §2.1 KDF variant`).

**Key derivation — it is a PIN-authenticated KEY EXCHANGE, and it is SOLVED [V].** A single 16-byte
transport key `K` protects **both** the request field and the response, derived from the request body's
leading random **context** (the client generates it; both peers hold it) and the on-screen passcode:
```
K = registrationTable[ context[selectorOffset] & 0x1f ]     # one of 32 static 16-byte table entries
K[12:16] ^= big-endian-uint32(passcode)                     # the PIN folds into the last 4 bytes
```
The PIN fold **is** the authentication: the console recomputes `K` from the transmitted context plus the
PIN the user typed, so a client with the wrong PIN derives the wrong key and cannot decrypt the pairing
record. The table lookup is obfuscation, not secrecy. The `(table, selectorOffset)` pair is chosen by the
negotiated protocol variant (there are ~11; RP-Version 1.0 → the variant with selectorOffset `0x18d`). The
table contents are extracted interop constants that live only in the gitignored dirty room (as with the
§2.1 KDF tables), not here.

Both fields then use the §2.1 field cipher unchanged: `AES-128-CFB(K, IV)`,
`IV = HMAC-SHA256(context_key, material ‖ be64(counter))[0:16]`, context key **B_eq_1**, **counter 0**,
one shared `material` per pairing (the KDF's material output; the client generates it and the console
recomputes it from the context). Request field plaintext = `Client-Type: <hex>\r\nNp-AccountId: <base64>\r\n`;
response plaintext = the pairing record.

**Validated end-to-end** against live captures — `K` derived from the wire context + PIN alone reproduces
the runtime keys byte-for-byte and decrypts the response to the pairing record (`PS5-RegistKey`, `RP-Key` =
companion, `RP-KeyType`) on three independent pairings. Reference: `rgst_keyexchange_reimpl.py` (dirty room)
and the C# implementation below.

Runtime map (RVA): `FUN_101f5290` registration entry (PS4/PS5 branch via `0x101dea20`/`0x101dea40`) →
`FUN_1020cd50` crypt ctor / `FUN_1020ce30`→`FUN_102209c0` socket setup → `FUN_101f4fb0` HTTP dispatcher →
`FUN_1020da40` send+recv+parse → field encrypt `FUN_101f8cc0`; key derivation `FUN_101ed040` (variant
dispatch) → KDF core `FUN_101fd830` (RP-Version 1.0 variant). The request context is CSPRNG-filled
(`FUN_101e99d0`→`FUN_100403b0`) — which is why `K` is per-pairing and never a static function of the PIN
(the earlier hash/heap-scan hunts were doomed for that reason; only the table-lookup-plus-PIN-fold shape,
visible via decompilation, fits).

**Implementation.** Committed, cross-platform, and unit-tested against the live vectors:
`HalyardRegistrationKdf` (the `K` derivation) + `HalyardRegistrationSecrets` (the injected dirty-room table
+ selector offset) + `HalyardRegistrationCipher` (ties `K` to the §2.1 field cipher for both directions).
`LiveRegistrationVectorTests` loads the gitignored fixture (`registration_crypto_vectors.json`, produced by
`gen_registration_fixture.py`) and reproduces the captured keys and pairing records; it `Skip`s when the
fixture is absent, so CI stays green and no constants are committed. The structural HTTP half
(`HalyardRegistrationMessage`: endpoints, HTTP/1.1 framing, response splitting → `HalyardPairingRecord`)
predates this and needs its header set refreshed to the wire shape above (the scaffold still carries the
pre-reversal `Np-AccountId`/`Content-Type` headers and the dead vendor-registration-tag model).

**Remaining (implementation detail, not research):** for the client SEND path, derive the per-pairing
`material` (IV input) by reimplementing the `FUN_101fd830` material transform over the client's own random
block and lay out the context so `context[selectorOffset]` selects the intended entry; extract the other
~10 variant `(table, selectorOffset)` tuples for non-1.0 protocols / other keytypes. The DECRYPT path
(recovering registkey + companion from a response) is complete and tested.

### 2.1 Session control — HTTP header field crypto  **[V]**

`/sess/init` (per-connect handshake) uses `RP-Registkey` + `RP-Nonce`; `/sess/ctrl` then carries several
**base64-encoded, encrypted** HTTP header fields. All of the following is reproduced end-to-end by
`rp_auth_reimpl.py` against wire-confirmed values.

**Session key derivation** (dispatched by `FUN_101ede80` on `RP-KeyType`, KDF `FUN_101fe340`, reimpl
`kdf_reimpl.py`):
```
(out1, out2) = KDF( nonce(16, from RP-Nonce) ‖ companion(16, = the stored RP-Key) )
```
`out1` = the 16-byte AES key. `out2` = 16-byte IV material. (Two-round table-lookup + subtract/XOR over
two static 32-entry tables; see kdf_reimpl.py.) `RP-KeyType` selects among KDF variants (values 1/2/3/4/6
observed); our captured sessions use the variant reversed in `kdf_reimpl.py`.

**Per-field IV** (`field_iv_reimpl.py`):
```
IV = HMAC-SHA256( context_key, out2 ‖ big-endian-uint64(counter) )[0:16]
```
- `counter` starts at 0 and **increments once per field-encrypt call** (single per-connection counter).
- `context_key` is one of four hardcoded 16-byte constants selected by protocol/version selectors
  (`FUN_101eddb0`): `sel0∈{8,9}→A`; `elif sel1==1→B_eq_1`; `elif sel1==0→B_eq_0`; else zero. Observed
  sessions resolve to the `B_eq_1` variant. The four constant values are extracted interop material and
  live only in the gitignored dirty room (`captures/name-mapping.md`), not here.

**Field cipher**: `AES-128-CFB`, key `out1`, IV as above, then base64. Field order fixes the counter:

| Field | Counter | Plaintext |
|-------|---------|-----------|
| `RP-Auth` | 0 | `registkey(8 raw bytes) ‖ 0x00×8` |
| `RP-Did` | 1 | `be16(len) ‖ hexdecode(Windows MachineGuid, dashes stripped)` |
| `RP-OSType` | 2 | `"Win%d.%d"` from kernel32 version (e.g. `"Win10.0\0"`) |
| `RP-StartBitrate` | 3 | 4-byte int |
| `RP-StreamingType` | 4 | 4-byte int |

---

## 3. Streaming transport (Takion over UDP)  **[V]/[C]**

Streaming runs over the **Takion** protocol carried directly on a **single UDP socket** — control, A/V,
feedback, congestion, and FEC are all multiplexed on that one flow, distinguished by the **base-type byte**
(byte 0). The console-side port is **negotiated** via the streaminfo `port` field; the live capture used
**9297** (an earlier capture observed 9296), client on an ephemeral port. Reliability is **internal to
Takion** (INIT/COOKIE handshake + DATA/SACK), *not* a separate reliable-UDP sublayer — see §8. (The
binary also contains a second, general-purpose reliable-UDP module, identifiable from its RTTI type
records — but that one is the modern/PSN transport and does **not** appear on the v1 LAN wire, §8.1.) **Protocol versions 7–0x11
(7–17)** are supported (the encrypt/authenticate paths in `FUN_100ce1a0` gate on version ∈ {7…0x11}); v1
negotiates one via `PROTOCOL_VERSION_REQUEST`; version ≥ 7 framing enables the per-packet encryption (§5.5a).

**Packet type = base-type byte** (byte 0). Wire distribution (one 17 s stream, single 9297 flow):

| type | name | dir | wire count | notes |
|------|------|-----|-----------|-------|
| `0x02` | **video** | down | 4625 | dominant, ~1–1.4 KB |
| `0x03` | **audio** | down | 1107 | small |
| `0x00` | control (protobuf + Takion chunks) | both | 272 | INIT/COOKIE handshake + DATA/SACK (§8) |
| `0x05` | **congestion** | both | 48 | `{received,lost}` feedback (§8.2) |
| `0x12` | **FEC** | down | 31 | parity fragments (§6.2) |
| `0x08` | control (client-info) | both | 9 | |
| `0x01`,`0x06` | feedback (history / state) | up | 1 | controller input (encrypted, tag@8/keypos@4) |
| `0x04` | handshake | — | 0 | distinct handler; none seen this session |

(An additional 6 datagrams with byte0 `0x06`/`0x07` precede the SCTP INIT — the
`PROTOCOL_VERSION_REQUEST`/`_ACK` version-negotiation exchange, **not** feedback; see §8.)

**Packet classes** — the receive dispatcher `FUN_100ce1a0` switches on the base type (`buf[0] & 0x0f`) into:
**control** (`0`,`8`), **feedback/input** (`1`,`6`), **A/V** (`2`=video,`3`=audio), **handshake** (`4`),
**congestion** (`5`), plus further control types (`7`,`9`,`0xa`,`0xb`,…); FEC rides as type `0x12`. Each
class authenticates with a 4-byte GMAC but at class-specific header offsets.

> **Correction (DLL-verified):** an earlier revision labeled **congestion as type 4**. Re-reading
> `FUN_100ce1a0`, **type 4** dispatches to a distinct handler (`vtable+0x3c`) with no GMAC path (a
> **handshake**-class message), while **type 5** is the GMAC-authenticated
> **congestion** path (`vtable+0x44`, version-gated crypto). Our binary is unambiguous: handshake=4,
> congestion=5.

**A/V data packet header** (**[C]** from `parseMessage` + **[W]** wire-confirmed):
```
off 0     : type (0x02 video / 0x03 audio / 0x12 FEC)
off 1-2   : per-packet sequence (big-endian uint16, +1 per packet)
off 3-9   : media sub-header (codec / fragment index / flags — type-specific)   [I: exact fields]
off 10-13 : 4-byte GMAC tag (§5.5)                          [C: read @+0x0a, then zeroed before verify]
off 14-17 : key position (big-endian uint32; running byte counter, not a timestamp) → key-pos routine [C: @+0x0e]
off 18+   : media payload
```
Wire-confirmed timestamps: video `off14 = 0x00013060,0x00013460,…` (+0x400/pkt); audio
`0x00000050,0x00000150,…` (+0x100/pkt). The 32-bit wire timestamp is extended to a 64-bit key position by
the key-position routine (`FUN_1012e3c0`) via a rollover counter (handles the 32-bit wrap). **Feedback/input** packets
put the tag at **off 8** and the key position at **off 4**.

**GMAC coverage:** the verifier zeroes the 4-byte tag field in place (`*(packet+10)=0`) and computes the
GMAC over the **entire packet buffer** (header + payload, tag bytes = 0) as AAD — not just the media
payload. Sender does the same, then writes the tag into the zeroed field. (See §5.5.)

Example video packet: `02 00 004b |media-subhdr| |<4-byte tag>| 00013060=ts …`. The tag is
session-keyed and is described rather than reproduced, per the redaction note above. The timestamp is
kept: it is the start of the generic `+0x400/pkt` sequence given above, identical on every session, and
so is a protocol fact rather than a value tied to this one.

**Control packet (type 0x00)** begins with a 4-byte **connection tag** (e.g. `00 00 48 23`, also seen
`0000b18ccf`) then a subtype; the body is a control-plane protobuf `ControlMessage` (§4). Reliable/control packets
may carry a **null tag** (a null tag from the peer is accepted in the tag-compare step/`FUN_100cf700`).

**Per-class GMAC placement** — each message class carries its 4-byte GMAC tag and 4-byte key position at
class-specific offsets (from `FUN_100ce1a0`, **[C]**):

| type(s) | class | tag offset | key-position offset | source |
|---------|-------|-----------|---------------------|--------|
| 2, 3 | A/V (video, audio) | 10 | 14 | **[C]** `FUN_100ce1a0` |
| 1, 6 | feedback / input | 8 | 4 | **[C]** `FUN_100ce1a0` |
| 0, 8 | control (protobuf, reliable) | 5 | 9 | **[V]** `cap47`, 2528 type-0 packets (2026-07-29) |
| 5 | congestion | 7 | 11 | **[V]** `cap47`, 474 type-5 packets (2026-07-29) |
| 4 | handshake | — | — | distinct handler, no GMAC |
| 0xc | GenericData | 1 | 5 | [C] earlier RE |

> **How the control/congestion offsets were confirmed (2026-07-29).** Both rows above were `[X]`-only until
> a structural analysis of `cap47` settled them without needing any key material. For control (type 0,
> 2528 packets): bytes 5–8 are high-entropy (2516 distinct values, zero only on the 13 unauthenticated
> handshake packets) while bytes 9–12 are **all exact multiples of 16** across 2508 distinct values — the
> signature of a key position that advances in whole cipher blocks. For congestion (type 5, 474 packets) the
> same test puts the tag at 7 and the key position at 11. A random field and a block-aligned counter are not
> mistakable for one another at that sample size.
>
> **Congestion packet layout is version-specific.** `cap47` carries a **15-byte** type-5 packet:
> `[0]=0x05`, `[1..2]=0` (zero in all 474), `[3..4]` u16 BE received (2..351 per interval), `[5..6]` u16 BE
> lost (**0 in every packet** — that session had no loss, so the field's *position* is confirmed but its
> meaning is inferred), `[7..10]` GMAC, `[11..14]` key position. Send cadence measured at min 201 ms /
> median 216 ms / p90 219 ms → a **~200 ms** interval **[V]**.
> Our earlier `session8` capture shows a **23-byte** variant of the same base type: `[1..2]` a sequence
> number (+3 per packet), `[3..4]` a 16-bit **90 kHz timestamp** (≈19 440 ticks per 216 ms interval,
> matching the media clock), `[5..8]` a monotonic cumulative counter, `[9..12]` a per-interval count,
> `[15..18]` GMAC, `[19..22]` key position. So packet size and field placement here are negotiated per
> protocol version — do not treat 15 bytes as universal.

**Reliability:** the reliable delivery + key position live in the **reliable-transport (RT)** sublayer
header over the reliable-UDP layer (RUDP) (key position in the reliable-transport header). Reliable/control packets (type 0) begin
with the 4-byte connection tag and may carry a **null tag** (allowed). The full the reliable-UDP layer (RUDP) reliability
wire format (connection setup, seq/ack/SACK, retransmit, window/congestion) is a large sub-protocol — see
§8 for its scope. The A/V/feedback/generic data paths above are unreliable and fully specified here.

---

## 4. control-plane messages (protobuf)  **[C]**

Control-plane messages are the protobuf schema in [`stream_control.proto`](stream_control.proto) (package
`avstream`, 41 messages), recovered from the embedded `FileDescriptorProto` in our own copy of the client
binary and verified to compile. Field numbers, enum values, and wire types are interoperability facts and are
reproduced exactly; **every name below is our own descriptive choice**, per the provenance note at the head
of the `.proto`. Top-level:

```proto
message ControlMessage {
  required ControlMessage.MessageType type = 1;    // dispatch enum
  optional SessionRequestPayload sessionRequestPayload = 2;
  optional SessionReplyPayload   sessionReplyPayload   = 3;
  optional StreamInfoPayload     streamInfoPayload     = 15;
  // … one optional payload per MessageType …

  enum MessageType { SESSION_REQUEST=0; SESSION_REPLY=1; BANDWIDTH_INFO=2; HEARTBEAT=3; PACKET_LOSS=4;
    CORRUPT_FRAME=5; CURSOR=6; TIMER=7; DISCONNECT=8; LOG=9; HEADER_REQUEST=10; DEBUG=11;
    BANDWIDTH_PROBE=12; STREAM_INFO=13; STREAM_INFO_ACK=14; SYSTEM_MENU_COMMAND=15;
    CONNECTION_QUALITY=16; CLIENT_METRIC=17; …; PROTOCOL_VERSION_REQUEST=31; PROTOCOL_VERSION_ACK=32;
    AUDIO_STATE=33; }
}
```

### 4.1 Connection & key-exchange sequence  **[C]**

```
client → server : [PROTOCOL_VERSION_REQUEST]  supportedVersions=[…]
server → client : [PROTOCOL_VERSION_ACK]
client → server : SESSION_REQUEST   { clientVersion, sessionKey, launchSpecJson, encryptedKey, ecdhPublicKey, ecdhSignature }
server → client : SESSION_REPLY  { serverVersion, token, encryptedKeyAccepted, versionAccepted, sessionKey,
                          serverVersionString, ecdhPublicKey, ecdhSignature }
  (both sides verify the peer's ecdhSignature; see §5)
… STREAM_INFO / STREAM_INFO_ACK, then A/V data flows …
```

### 4.2 SESSION_REQUEST / SESSION_REPLY (the ECDH exchange)  **[C]**
```proto
message SessionRequestPayload {                     // MessageType SESSION_REQUEST (client → server)
  required uint32 clientVersion = 1;
  required string sessionKey    = 2;
  required string launchSpecJson    = 3;     // the streaminfo JSON (see §4.3)
  required bytes  encryptedKey  = 4;     // handshakeKey delivery
  optional bytes  ecdhPublicKey    = 5;     // client ECDH P-256 pubkey, 65-byte uncompressed
  optional bytes  ecdhSignature       = 6;     // = HMAC-SHA256(handshakeKey, ecdhPublicKey)
}
message SessionReplyPayload {                     // MessageType SESSION_REPLY (server → client)
  required uint32 serverVersion = 1;  required uint32 token = 2;
  required bool   encryptedKeyAccepted = 3;  required bool versionAccepted = 4;
  required string sessionKey = 5;  optional EventCode extendedInfo = 6;
  optional string serverVersionString = 7;
  optional bytes  ecdhPublicKey = 8;        // server ECDH pubkey
  optional bytes  ecdhSignature    = 9;        // = HMAC-SHA256(handshakeKey, ecdhPublicKey)
}
```

### 4.3 Streaminfo config (`launchSpecJson`)  **[V]**

`SessionRequestPayload.launchSpecJson` is a **JSON** document, transmitted **AES-128-OFB-encrypted with the control key
`out1`** (verified: `streaminfo_reimpl.py`). The client builds it from a static template
(`FUN_1021f670`) and inserts a fresh random **`handshakeKey`** (see §5.1). Decrypted sample:
[`captures/streaminfo_config_decrypted.json`](captures/streaminfo_config_decrypted.json). Key fields:
`sessionId`, `streamResolutions[{resolution{width,height},maxFps,score}]`, `network{bwKbpsSent, bwLoss,
mtu, rtt, ports}`, `videoCodec` (`avc`), `dynamicRange` (`SDR`), **`handshakeKey`** (base64 of 16 random
bytes), `audioChannels{encoderType:"opus", audioChannelSettings[{sampleRate:48000, channels:2,
samplesPerFrame:480, maxFrameDataSize:1920, bitrate, fecMode}]}`, and a `disableRPEncryption` toggle.

> **Template corroborated first-party (2026-07-22):** the `appSpecification` section (`minFps`, `minBandwidth`,
> `timeLimit`, `startTimeout`, `afkTimeout`) was read directly from our own client's launchSpec buffer in
> memory (a debugger dump caught it mid-OFB-XOR, with those fields still plaintext) and matches the sample
> template exactly; the head (`sessionId:"sessionId4321"`, `streamResolutions`) was earlier confirmed against
> the wire keystream (`streaminfo_reimpl.py`). So the template is our client's genuine structure, not a
> third-party guess. `sessionKey` in the enclosing SESSION_REQUEST is the literal `"InvalidSessionId"` and
> `encryptedKey` is unused (§4.2, wire-parsed).

---

## 5. Stream key agreement & per-packet crypto  **[C]**

Reference + round-trip test: `stream_crypto_reimpl.py`. All HMAC/hash = SHA-256. Ghidra map:
`FUN_100cb3b0` (init), `FUN_100cb620` (finalize/derive), `FUN_1012d9e0` (KDF), `FUN_1012e6e0`
(per-packet GMAC), `FUN_1012eee0` (iv_add), `FUN_1012efc0` (sha256_fold).

### 5.1 handshakeKey
A **16-byte cryptographically-random** value the client generates per session (Windows CSPRNG /
OpenSSL RAND), base64s into `launchSpecJson.handshakeKey`, and uses to authenticate the ECDH exchange. It is
NOT an encryption key. (Verified random: 6/6 captured values distinct; no derivation path in code.)

### 5.2 Ephemeral ECDH — curve is **version-dependent**
Each side generates an ephemeral ECDH keypair; the uncompressed pubkey (`04‖X‖Y`) is sent in
SESSION_REQUEST/SESSION_REPLY as `ecdhPublicKey`, accompanied by
```
ecdhSignature = HMAC-SHA256(handshakeKey, ecdhPublicKey)      # 32 bytes
```
Each side **verifies the peer's `ecdhSignature`** (`FUN_100cb620`, pubkey-verification failure on mismatch)
→ mutual authentication / MITM protection.

> **Curve correction [C]→[V] (live-parsed 2026-07-22):** the curve depends on the **negotiated protocol
> version**. A session that negotiates **version 17 (0x11)** uses **P-521** — the wire `ecdhPublicKey` is
> **133 bytes** (`0x04` + X66 + Y66), parsed directly from a real SESSION_REQUEST. The earlier "v1 uses P-256,
> the default" was a guess; versions 0xd–0x11 use P-521 (as the note below hinted). Also wire-confirmed in that
> SESSION_REQUEST: `sessionKey = "InvalidSessionId"` (a literal), `encryptedKey` **empty** (unused),
> `clientVersion = 17`.
>
> **Reduction resolved [V] (live-validated 2026-07-22):** there is **no reduction** — the **full 66-byte X**
> is used directly as the SP800-108 HMAC key (§5.3). HMAC-SHA256 internally hashes any key longer than its
> 64-byte block, so the 66-byte X is absorbed without truncation. The earlier "reduced to 32 bytes" note came
> from a truncated 32-byte dump; a full 66-byte X dump feeds `DeriveDirection` and reproduces the real
> console's send/recv keys byte-for-byte.
```
shared_secret = ECDH(local_priv, peer_pub).X          # 32 bytes (P-256) or 66 bytes (P-521), used as-is
```
(For a P-256 session the X coordinate is 32 bytes; for P-521 it is 66 bytes — both feed the KDF unchanged.)

### 5.3 Base key/IV per direction (SP800-108 counter-mode KDF)  **[V]**
For each channel direction `dir` (client→server = `2`, server→client = `3`):
```
info    = 0x01 ‖ dir ‖ 0x00 ‖ handshakeKey(16) ‖ 0x01 0x00        # 21 bytes; 0x0100 = 256 output bits (BE)
block   = HMAC-SHA256( key = shared_secret, msg = info )          # 32 bytes; shared_secret = the ECDH X
aes_key = block[0:16]      base_iv = block[16:32]                  # AES-128
```

> **Correction [C]→[V] (live-validated 2026-07-22):** the SP800-108 output-length field is `0x01 0x00`
> (`0x0100` = 256 bits, big-endian), **not** `0x00 0x01`. The prior `0x00 0x01` was a reference-guess error
> that produced wrong keys. Corrected by brute-forcing the KDF structure against a real console, then
> confirming byte-for-byte: with the full 66-byte P-521 X as the HMAC key and the dumped 16-byte
> `handshakeKey`, `DeriveDirection` reproduces **both** the console's client→server (`dir 2`) and
> server→client (`dir 3`) AES key + base IV exactly (`LiveStreamKeyVectorTests`). This closes the last
> unvalidated step of the stream key agreement — §5.2/§5.3/§5.4 are now all [V] end to end.

### 5.4 Per-packet key material

Each data packet is **both encrypted and authenticated**, using two keys derived from the per-direction
`aes_key`/`base_iv`:

- **Cipher key** = `aes_key` itself (used directly; §5.5a). No rotation.
- **GMAC key** = a rotating key that changes every 45000 `key_pos` units (§5.5b). A base key is folded
  once from `aes_key` and `base_iv`; window 0 uses it directly, and each later window re-folds **that base
  key** (not `aes_key`) with an incrementing IV: **[V]**
```
window   = key_pos // 45000
key0     = sha256_fold( aes_key ‖ base_iv )                       # the window-0 GMAC key
gmac_key = key0                              if window == 0
         = sha256_fold( key0 ‖ iv_add(base_iv, window * 0xAF6E) ) if window >= 1
  where sha256_fold(x) = SHA-256(x)[0:16] XOR SHA-256(x)[16:32]
        iv_add(iv,n)   = little-endian (int(iv) + n) mod 2**128, back to 16 bytes
```
A red-black-tree cache (the cipher object +0x88) memoizes `gmac_key` per window. `key_pos` is the packet's
64-bit media position (the 32-bit wire value at offset 14 extended via the rollover counter, §3).

> **Correction [C]→[V] (live-validated 2026-07-22):** the first fold argument for window ≥ 1 is `key0` (the
> already-folded window-0 GMAC key), **not** `aes_key`. The prior `sha256_fold(aes_key ‖ …)` form (a [C]/[X]
> guess) authenticated window 0 only and rejected every packet past the first ~0.5 s of stream. Corrected by
> replaying dumped receive keys against a live console capture: the chained form GMAC-verifies **13670/13672**
> A/V packets across 223 rotation windows (the two misses are the torn capture tail). Payload AES-CTR
> decryption was already correct throughout (real H.264 NALUs recovered).

> **Correction [C]/[X]:** an earlier version of this spec labeled the `sha256_fold` output as *the* packet
> key and stated the payload was "authenticated, not encrypted." That was an incomplete trace — the
> `sha256_fold` value is the **GMAC key**, and there is a **separate AES-CTR encryption** of the payload
> keyed by `aes_key` (§5.5a), which the original RE missed. The encryption path was subsequently located in
> our own binary (`FUN_1012e070`/`FUN_1012df00`/`FUN_1012de40` + the crypt-thread keystream `FUN_1017e100`),
> The model is self-consistent and reproduces live console traffic.

### 5.5 Per-packet crypto — encrypt‑then‑MAC

Each A/V data packet payload is **AES-128-CTR encrypted and then GMAC-authenticated**. Both operations key
off the same `key_pos` (the offset-14 wire value, §3).

**5.5a — Payload encryption (AES-128-CTR) [C]/[X].** The payload is XORed with an AES keystream. **The
cipher counter is offset one block above the GMAC's** (the GMAC consumes keystream block 0, GCM-style):
```
enc_pos       = key_pos + 0x10                                    # +BLOCK_SIZE, for protocol version ≥ 7 (v1)
nonce/counter = iv_add(base_iv, enc_pos >> 4)                     # = base_iv + key_pos/16 + 1
keystream     = AES-128-CTR(key = aes_key, counter block = nonce) # AES-128-ECB of successive counter blocks
payload      ^= keystream                                          # symmetric: encrypt == decrypt
```
The keystream is precomputed by a background "crypt thread" (`FUN_1017e100`) into a ring buffer keyed by
`aes_key`(cipher-obj+0x30)/`base_iv`(+0x40); `FUN_1012e070` XORs the appropriate keystream slice into the
payload via `FUN_1012df00` → `FUN_1012de40` (`xor_bytes`). **`FUN_1012e070` has two branches on the
protocol-version field (cipher-obj+0x74): version < 7 indexes the keystream at `key_pos` and advances the
running position by `data_size`; version ≥ 7 (the v1 range, §3) indexes at `key_pos + 0x10` and advances by
`data_size + 0x10` rounded up to a 16-byte multiple** (`__aullrem(key_pos + 0x10, …)`). So for v1 the CTR
nonce is `base_iv + key_pos/16 + 1`, while the GMAC (§5.5b) uses `base_iv + key_pos/16`. The 64-bit running
position is at cipher-obj+0x18/+0x1c; call sites `FUN_100cc090` (send) / `FUN_100cee40` (receive). A receiver
**must AES-CTR-decrypt the payload before feeding the codec** — the raw `0x02`/`0x03` payload is ciphertext
(hence high-entropy, not clean H.264/Opus). Confirmed in our binary (`FUN_1012e070`, the version-≥7 branch);
the send path encrypts at `key_pos + BLOCK_SIZE` while MACing at
`key_pos`.

**5.5b — Authentication (AES-128-GCM as GMAC) [C].**
```
aad   = the entire packet with its 4-byte tag field (offset 10 for A/V) set to 0x00000000
nonce = iv_add(base_iv, key_pos >> 4)                             # 16-byte GCM IV (same as 5.5a's counter)
tag16 = AES-128-GCM(gmac_key, nonce, aad, plaintext = "")         # AAD-only, gmac_key from §5.4
tag4  = tag16[0:4]                                                # written into the zeroed tag field @off10
```
`FUN_1012e6e0` computes the tag over the whole packet (tag field zeroed) as AAD; sender writes `tag4` in,
receiver re-zeroes, recomputes, and compares. **Ordering:** the tag is computed over the *encrypted* packet
(encrypt-then-MAC); a receiver verifies the GMAC first, then AES-CTR-decrypts the payload.

**Per-class AAD rule — control zeroes key_pos as well as the tag [V].** The A/V rule above (zero the tag
only) is not universal. For **control** (base type `0x00`, tag@5, key_pos@9) the AAD zeroes **both** the
4-byte tag *and* the 4-byte key_pos field. Verified 2026-08-02 by recomputing the MAC offline over `cap3`
with that session's dumped stream keys: of **727 authenticated type-0 packets** (364 client→server, 363
server→client), zeroing tag+key_pos reproduces the on-wire tag **727/727**. Zeroing the tag alone matches
only the 2 packets (one per direction) whose `key_pos` is 0, where the two rules are byte-identical and are
therefore not evidence. A further 67 type-0 packets carry an all-zero tag — the pre-key INIT/COOKIE
handshake, unauthenticated — and are excluded rather than scored. The same run independently confirms the
§5.4 GMAC key fold, the `base_iv + key_pos>>4` nonce, and the control field offsets.

**Feedback** (base types 1/6, key_pos@4, tag@8) zeroes **only** the tag, like A/V. **Congestion** (base type
`0x05`, tag@7, key_pos@0xb) is implemented as zeroing both, by analogy with control — that specific rule is
**[X]**, since the only capture pairing traffic with dumped keys carries no congestion packets.

Note: `disableRPEncryption` in the streaminfo (§4.3) can disable the payload encryption (5.5a) while
authentication (5.5b) still runs — worth honoring on the sender.

---

## 6. A/V media & FEC  **[W]/[C]/[I]**

- **Video**: `avc` (H.264), SDR, resolutions up to 1920×1080@60 negotiated in `streamResolutions`;
  packets type `0x02`, NALU fragments reassembled per frame.
- **Audio**: **Opus**, 48 kHz, stereo, 480 samples/frame (10 ms), CELT-only fullband (TOC `0xF4`); packets
  type `0x03`, constant 268 B. Each packet's decrypted payload packs `total_units` equal-size units
  back to back (unit size = payload / total_units). **The first unit is the source Opus frame; the remaining
  units are redundant copies of the same 10 ms** (independently encoded — each decodes to the same audio) for
  loss concealment. The frame index advances by one per packet ⇒ exactly one source frame per packet. Observed
  on this v12 firmware: `total_units = 3`, 80-byte units (1 source + 2 redundant), payload 240 B.
  **Decode only the first unit** — Opus/CELT sizes its per-band bit budget from the packet byte-length, so
  handing the decoder the whole payload makes it read the redundant units as high-frequency coefficients and
  corrupts every band above ~5 kHz (audible as "underwater"/muffled). Wire-confirmed empirically: decoding the
  first 80-byte unit lifts L/R correlation 0.13 → 0.58 and restores clean harmonics; the three units decode to
  identical audio.

  The bytes-5..8 packed unit field is laid out differently from video: video packs three 11/11/10-bit fields
  (`unit_index[31:21] | total_units-1[20:10] | parity_units[9:0]`); **audio packs byte-wide fields**
  (`unit_index[31:24] | total_units-1[23:16] | low-16 firmware-specific`). The low-16 word's unit-size sub-field
  does *not* match the v9 reference on this firmware (reads as 32, which would imply a 96-byte payload, not the
  observed 240), so unit size is derived from payload ÷ total_units rather than the header field.
- **FEC** (§6.2): selected by `audioChannelSettings.fecMode`; packets type `0x12`.
- **bandwidth-probe**: a pre-stream bandwidth-probe phase with its own bind endpoint and message set
  ([`bandwidth_probe.proto`](bandwidth_probe.proto): `BandwidthCommand`, `MtuCommand`, `EchoCommand`, `ClientMtuCommand`).

### 6.2 FEC (forward error correction)  **[C]** structure / **[C]** matrix form / **[V]** field

Decoder = **`FUN_100ab700`**. It is a **systematic (N, K) erasure code**
applied per media frame: a frame is `K` source fragments plus `M` FEC fragments (`N = K + M`; type `0x12`
packets carry the FEC fragments, `0x02`/`0x03` the source). The decoder (`FUN_10103da0`) reconstructs the
frame **iff `received_source + received_fec ≥ K`** (fields on the FEC group object: `K`/expected at `+0x08`,
received-source at `+0x10`, received-FEC at `+0x14`, fragment size at `+0x18`); the erasure solve is
`FUN_10103b30`, workspace built by `FUN_100ac480`. Per-frame accounting is logged as
the per-frame expected/received correction-frame counts. Outcome enum: `OK, BAD_HEADER, PRE_DECODE_OK, PRE_DECODE_FAIL, CORRECT_FAIL`; on unrecoverable loss the pipeline error-conceals.

**Algorithm = systematic Reed-Solomon erasure code over GF(2⁸)** (`FUN_10103b30` builds the present/missing
fragment index lists; `FUN_101035e0` sizes the matrices — fragment length must be a multiple of 4;
`FUN_1015f610` drives the decode). The core `FUN_1015f2a0` is **Gaussian-elimination matrix inversion**
(builds an identity, pivots/row-reduces) over **GF(256) lookup tables** (see the field-object layout at the
end of this section); `FUN_1015efd0` applies the inverted submatrix to the received fragments (word-wise)
to reconstruct the missing ones. So: solve the received rows of the RS generator matrix for the erased
fragments — standard RS erasure decoding. The GF field table is generated at runtime (not in `.rdata`), which
is why the primitive polynomial needed a dynamic capture rather than static search — see the end of this
section, where it is now pinned.

**The generator matrix is a Cauchy matrix — `matrix[i][j] = inverse(i XOR (m + j))` in GF(2⁸) [C]**,
row-major with `m` rows of `k` columns, one `u32` per element. Read directly off the construction loop in
`FUN_101035e0`, which stores `table[((m + j) XOR i) | 0x100]` at element `[i][j]`. That lookup is row
`a = 1` of the 64 KB **divide** table at field-object `+0x8`, i.e. `1 / value` — the field **inverse** (it is
also what `FUN_1015f2a0` divides the pivot by, while GF *multiply* goes through the separate 64 KB table at
`+0xc`, which the matrix construction never touches). **This rules out Vandermonde**: a Vandermonde row is a power
sequence and would need index products through the multiply or an exp/log table, not a single inverse
lookup on an XOR of the two indices. Superseded here: this was `[X]` — an adopted assumption — until the
construction loop was read first-hand; the earlier pass over `FUN_101035e0` had noted only its
fragment-length check. So: **systematic Cauchy-matrix Reed-Solomon over GF(2⁸)**, with the Cauchy claim now
resting on our own decompilation rather than on the form being the conventional choice.

The frame parameters come straight from the A/V header (§6.1,
DLL-confirmed): **`m` = `parity_units`** (off5..8 bits[9:0]) and **`k` = `total_units − m`**
(source units).

**Coded unit length = the frame's longest source unit rounded up to a multiple of 4 [C][W]**, constant within
a frame, and it is what FEC codes over. Two independent confirmations: `FUN_101035e0` rejects any fragment
length with `(len & 3) != 0`, and the console lays its units out contiguously at `base + len * unit_index` —
its per-unit stride *is* the coded length, with no other alignment anywhere. On the wire in `cap47` (5,961
frames): parity length is constant within every frame, is never exceeded by any source unit, and
`parity_len − max(source_len)` is only ever 0..3. Parity units carry no size-extension, so a parity unit's
payload length is the coded length directly. Superseding the earlier text here: this was written as "per-unit
stride = the unit size rounded up to a multiple of **0x10** [X]", which conflated two different things — the
**0x10 was never a protocol value**, only a client-side slot-spacing choice, and the alignment that *is* real
is 4 and applies to the coded length. **Video source units are prefixed with a 2-byte big-endian
size-extension** that widens that unit's buffer to the coded length [X] (§6.1). The
**GF(2⁸) primitive polynomial = `0x11D`** (x⁸+x⁴+x³+x²+1), the standard GF(256) Reed-Solomon/AES polynomial
— **[V] confirmed 2026-08-02**, superseding the `[X]` assumption it had been, and confirmed **two
independent ways**.

*Statically:* the field-table builder `FUN_1015eed0` is the textbook exp/log generator loop, and at
`0x1015ef5d` it executes `xor ecx, 0x1d` — the low-byte form of 0x11d (`x <<= 1; if (x & 0x100) x ^= 0x1d`),
present as a **literal immediate in the instruction stream**. *Dynamically:* a breakpoint at `0x101036db`
in the matrix builder dumped the live table, which reproduces 0x11d on every defined entry; the other 15
primitive degree-8 polynomials match at most one entry each, so the identification is unambiguous.

**The polynomial is a fixed compile-time constant, not a negotiated or per-session value.** `FUN_1015eed0` is
a `thiscall` that takes no arguments, reads no globals, and is guarded to build once (`cmp [this],0` →
early-return); no session, console, or stream state can reach it. "Runtime-generated" here means *computed
at initialisation from a hardcoded constant*, not *variable* — the same thing a client-side table build does.
`fecMode` in the streaminfo config (§4.3) selects FEC behaviour but cannot influence the field.

**Field-object layout** (`FUN_101033d0` constructs it; corrects the earlier "0x200 inverse table" reading):

| offset | size | contents |
|---|---|---|
| `+0x00` | `0x100` | `log[]`, prefilled `0xff` = unset |
| `+0x04` | `0x300` | `exp[]`, cycle written three times, then the **pointer biased by `+0xFF`** so division's negative indices resolve |
| `+0x08` | `0x10000` | **divide** table, `div[a<<8 \| b] = a / b` |
| `+0x0c` | `0x10000` | **multiply** table, `mul[a<<8 \| b] = a · b` |

So `table[value | 0x100]` is row `a = 1` of the divide table — `1 / value` — which is why it functions as the
inverse table. Division by zero stores a **`0xff` sentinel** (`div[a<<8 | 0] = 0xff`) where a from-scratch
implementation would more likely throw; it is unreachable in the Cauchy construction either way, since
`i < m ≤ m + j` means `i XOR (m + j)` is never zero. The 0x200-byte dump is exactly `div[0]` and `div[1]`,
and reproduces byte-for-byte from the layout above.

With the matrix form `[C]` and the field now `[V]`, both halves of FEC recovery are pinned to the console;
they were only ever safe as a pair, since a correct matrix over the wrong field still reconstructs garbage.
None of it is needed for a clean-LAN first picture — with no loss, the `k` source fragments always arrive and
the decoder never runs.

### 6.1 A/V data packet header (bit-exact)  **[C]** from `FUN_100fcb10` (protocol-version handler +0xc parser); field names **[X]**

Multi-byte integers are **big-endian on the wire** (parser byte-swaps via `ntohs`/`ntohl`). The byte/bit
layout is code-exact from our binary. Two fields our first pass left `[I]` were later resolved, and one
**corrected**, by re-reading the parser — see
the note).
```
off 0        u8    packet type in low nibble (2=video, 3=audio); bit 4 = uses_nalu_info_structs flag  [X]
off 1..2     u16   packet_index  (per-packet sequence, +1 per packet)                     [C]/[X]
off 3..4     u16   frame_index                                                            [X]
off 5..8     u32   bit-packed (dword, big-endian):
                     bits[31:21] (11b)  unit_index          (fragment index within the frame)         [X]
                     bits[20:10] (11b)  total_units − 1  (stored −1; = source+FEC units)      [X]
                     bits[9:0]   (10b)  parity_units  (FEC unit count)                           [X]
off 9        u8    codec                                                                  [X]
off 10..13   u32   4-byte GMAC tag (zeroed for the GMAC computation, §5.5)                 [C]
off 14..17   u32   key position (running byte counter; §5.4 — NOT a media timestamp)       [C]/[X]
off 18..     …     media payload. Header base = 0x12 (18); +3 bytes for video, +3 for uses_nalu_info:  [X]
                     if video      : {size_extension u16 BE, adaptive_stream_index = next byte >> 5}  (3 B)
                     if nalu_info  : a further 3-byte NALU-info struct
                     then the (AES-CTR-encrypted, §5.5a) unit payload
```
> **Correction [C]/[X]:** our earlier trace labeled `off5..8` bits[9:0] as a *frame index* and `off3..4` as a
> *channel/frame selector*. Re-reading our own parser `FUN_100fcb10` confirms **`off3..4` → the `frame_index`
> setter (`vtable+0x3c`)** and **bits[9:0] (`dword & 0x3ff`) → the FEC-count setter (`vtable+0x34`) =
> `parity_units`** (which feeds §6.2's `m`); `off1..2` → `packet_index` (`+0x84`), `off5..8` `>>0x15` →
> `unit_index` (`+0x74`), `(>>0xa & 0x7ff)+1` → `total_units` (`+0x6c`), `off9` → `codec` (`+0x2c`).
> The bit widths/offsets our RE recovered are correct; only the two labels were wrong. The wire mapping is
> DLL-confirmed [C]; the field *names* remain [X]. Unlike the control-plane protobufs — whose descriptor is
> embedded in the binary — this header is a bit-packed struct carrying no names at all, so there is no vendor
> naming to recover here and every label is one we chose.

Wire cross-check (video): `off1..2` = 0x004b,0x004c,… (+1); `off5..8` top field (`unit_index`) = 0,1,2,…
(+1/pkt); `off14..17` = 0x00013060,0x00013460,… (+0x400/pkt). The `off14` step equals the payload size
(video +0x400, audio +0x100) because it is a **key position that advances by the encrypted byte count** per
packet, not a wall-clock timestamp. **Input/feedback** packets (type 1/6) instead
carry the tag at off 8 and the key position at off 4 (§3). This fully parses/emits A/V packets and drives
per-frame reassembly.

### 6.3 Controller feedback (up-direction input)  **[V]** re-derived from our own capture `cap48` (2026-07-29)

> **Provenance note.** This section is **derived from our own instrumented session**:
> `pin-regist/cap48.pcapng.gz` + the stream keys dumped by
> `pin-regist/hook_feedback_state.js`, decrypted with `decrypt_feedback.py`. Every field position and
> encoding below was recovered by correlating 10,165 state packets and 1,258 history packets against a
> deliberately isolated input sequence (`input_capture_plan.md`). Every value is cited to that capture;
> the few the capture could not settle are tagged `[I]` inline and must stay tagged.
>
> Decryption facts needed to reproduce this (both cost a debugging pass, both are load-bearing):
> the feedback key position is a **u32 at offset 4** and the payload starts at **offset 12**; and the CTR
> counter is **little-endian**, so a standard CTR-mode cipher handed the nonce decodes only the first
> 16-byte block correctly and produces noise thereafter — each block's counter must be incremented
> little-endian explicitly.

Controller input is sent up the stream socket as two **feedback** packet types, both sharing one 12-byte
header and the same outgoing key-position counter as control/congestion (so nonces never repeat). GMAC
zeroes only the tag (like A/V, unlike control/congestion), and the payload **is** AES-CTR encrypted.

```
off 0      u8    packet type: 6 = feedback state, 1 = feedback history
off 1..2   u16   sequence number (per-type, +1 per packet, big-endian)
off 3      u8    0
off 4..7   u32   key position (shared up counter; drives the crypto nonce)      [written at seal]
off 8..11  u32   4-byte GMAC tag (payload region only zeroes the tag in AAD)     [written at seal]
off 0xc..  ...   AES-CTR-encrypted payload
```

- **State** (type 6): a periodic analog snapshot, fixed **0x1c-byte** payload — 10,165/10,165 packets in
  `cap48` were exactly 28 bytes, and `[0]` was `0xa0` in 100% of them. Byte-offset map, each entry
  established by its own evidence:

  ```
  [0x00]        u8    0xa0            constant across every packet (100%)
  [0x01..0x0c]  6 x u16 LE            motion sensors — see below
  [0x0d..0x10]  u32                   orientation, smallest-three compressed quaternion
  [0x11..0x12]  s16 BE                left stick X
  [0x13..0x14]  s16 BE                left stick Y
  [0x15..0x16]  s16 BE                right stick X
  [0x17..0x18]  s16 BE                right stick Y
  [0x19]        u8    0x00            constant (100%)
  [0x1a]        u8    0x00            constant (100%)
  [0x1b]        u8    0xca            98% of packets; 0x00 for the rest — see the discrepancy note
  ```

  *Sticks — how the assignment was established.* Each of the four `s16 BE` fields reaches its extremes
  exactly once, and the four excursion windows fall in the same order as the scripted stick sequence, ~3.5 s
  apart (matching the hold-2s/wait-2s cadence): `0x11` at t=137.5/141.0, `0x13` at t=142.8/146.2, `0x15` at
  t=148.7/151.4, `0x17` at t=152.9/155.3. The extremes are exactly `0x8001` (−32767) and `0x7fff` (+32767)
  read **big-endian**, and each field's median sits within ±1200 of zero (resting drift) — an unsigned or
  little-endian reading would not centre at zero. First axis moved was left-X, so `0x11`=LX, `0x13`=LY,
  `0x15`=RX, `0x17`=RY, with **left/up negative**.

  *Motion sensors — how the split was established.* All six are `u16 LE` (the odd offsets take all 256 byte
  values uniformly, the even offsets span a narrow high-byte range — the signature of a little-endian pair).
  The first three (`0x01`,`0x03`,`0x05`) have a median of exactly `0x7fff`, i.e. they rest at mid-scale =
  **gyro** (zero angular rate). The last three (`0x07`,`0x09`,`0x0b`) rest *off* centre — medians `0x81be`,
  `0x9889`, `0x797d` — with one axis strongly displaced, which is gravity on a stationary pad =
  **accelerometer**. The scale factors (the ranges the endpoints map to) are **not** determined by this
  capture and remain **[I]**; a controller with no motion sensor can send the resting values above.

  *Orientation.* `[0x0d..0x10]` observed max is `0x3f310a01`, i.e. bit 29 set and bit 30 clear — consistent
  with a 30-bit packed field (a 3-bit selector plus three 9-bit components). 5,025 distinct values over the
  session. The exact packing is **[I]** from this capture alone; a client may send a fixed identity value.

  > **The tail byte, and a warning.** `[0x19]`/`[0x1a]` are zero, and `[0x1b]` is **`0xca`** in 98% of
  > packets (`0x00` in the remainder, confined to t=39.7–132.5). The meaning of `0xca` is **[I]**. If you
  > encounter a description of this field giving `1`, our own hardware disagrees with it — do not "correct"
  > this to `1` without a capture of your own that says so.

- **History** (type 1): button/trigger **transitions**, sent only on change; payload is a concatenation of
  events with no count prefix. Two event forms are present in `cap48`, distinguishable by code:
  - `[0x80][code][state]` — 3 bytes, `state` = `0xff` pressed / `0x00` released. Observed codes:
    `0x80`–`0x8b` (twelve). Directly visible as repeating `80 85 00 | 80 85 ff | 80 84 00 | …` triplets.
  - `[0x80][code]` — 2 bytes, no state byte; press/release is folded into the code as a **+0x20** bit
    (`0x8e` released ↔ `0xae` pressed, confirmed as an adjacent pair in a 4-byte packet `808e80ae`).
    Observed pressed codes: `0xac`,`0xad`,`0xae`,`0xaf`,`0xb0` ⇒ base codes `0x8c`–`0x90`.

  There is also a **third event family** framed `00 00 00 <subtype>`, whose length depends on the subtype:
  `0x20` carries no payload (4 bytes total), `0x21` carries two (6 bytes total; observed payloads `5100`
  and `2800`). These recur throughout the session, including while idle. Their meaning is **[I]** — they are
  not needed to *send* input, but a parser must skip them correctly or it desynchronises.

  *How the events were delimited.* Rather than assume boundaries, they were recovered empirically: history
  packets are cumulative with newest events **prepended**, so wherever an older packet is a strict suffix of
  the next one, the leading difference is exactly one atomic event. That yielded `8080ff`, `808000`, `808e`,
  `00000020`, `000000215100` and friends directly — the 3/2/4/6-byte forms above are observed, not inferred.

  **Button code map — [V], from the scripted sequence.** Each code's first press falls in the scripted order
  with the expected spacing, and the set is closed: exactly seventeen base codes `0x80`–`0x90` appear in the
  whole capture, no others.

  | Code | Button | Form | Evidence (first press) |
  |---|---|---|---|
  | `0x80` | D-pad Up | 3-byte | by elimination — see caveat |
  | `0x81` | D-pad Down | 3-byte | by elimination — see caveat |
  | `0x82` | D-pad Left | 3-byte | t=87.8 s |
  | `0x83` | D-pad Right | 3-byte | t=89.1 s |
  | `0x84` | L1 | 3-byte | t=91.4 s |
  | `0x85` | R1 | 3-byte | t=92.5 s |
  | `0x86` | **L2 (analog)** | 3-byte | t=103.2 s; **57 distinct state values** |
  | `0x87` | **R2 (analog)** | 3-byte | t=110.3 s; **52 distinct state values** |
  | `0x88` | Cross | 3-byte | t=77.1 s |
  | `0x89` | Circle | 3-byte | t=78.9 s |
  | `0x8a` | Square | 3-byte | t=80.9 s |
  | `0x8b` | Triangle | 3-byte | t=82.4 s |
  | `0x8c` | Options | 2-byte | t=119.6 s |
  | `0x8d` | Create / Share | 2-byte | t=124.0 s |
  | `0x8e` | PS | 2-byte | t=0.0 s (used to open the session) |
  | `0x8f` | L3 | 2-byte | t=114.5 s |
  | `0x90` | R3 | 2-byte | t=115.8 s |

  The 3-byte/2-byte split falls exactly at `0x8b`/`0x8c`, which is what makes the form predictable from the
  code alone. **L2/R2 being analog is independently proven**: their state bytes take 57 and 52 distinct
  values across the session, whereas every other 3-byte code only ever carries `0x00` or `0xff` — so the
  third byte is a 0–255 level for those two and a boolean for the rest.

  > **Caveats.** (1) D-pad **Up/Down** (`0x80`/`0x81`) are assigned *by elimination*: both were pressed
  > during the earlier period when input was misbehaving, so their first-press timestamps predate the
  > scripted D-pad step and cannot order them. Left/Right are directly timed. Confirming Up vs Down needs
  > one clean press of each — cheap to add to a future capture. (2) **Touchpad click has no code here.** The
  > scripted touchpad step produced no eighteenth code, so it is either carried by the `00 00 00 21` family
  > or was not registered; do not invent a code for it. (3) The parser reaches 65% of history packets; the
  > rest contain at least one further subtype still to be decoded.

Stick **Y is inverted** relative to the GameInput convention: the console wire expects stick-up = negative.
This is now **[V]** from `cap48` independently of the earlier live-play observation — the scripted "full up"
excursion drove `[0x13]` to −32767. Remaining **[I]** items: the gyro/accel scale factors, the quaternion
packing, the `[0x1b]=0xca` tail byte, and the per-button code assignment. Note this supersedes the earlier
tentative "channel `0x0e`,
66-byte" input-packet guess in `ps5-controller-input-packet.md` — that capture-labelled channel numbering
proved unreliable (the working transport numbers types as in §3, e.g. congestion is type 5, not the
enum's `0x06`).

---

### 6.4 Senkusha probe sequence — ordering  **[W]** wire-observed 2026-08-02

The pre-stream probe phase runs, in order: **control handshake → RTT echo ×10 → MTU-in (downstream) →
MTU-out (upstream)**. Observed **identically in two independent captures of our own**, which is what moves
this from `[X]` to `[W]`:

| stage | base type | size | direction | `session8-wireshark` | `cap47` |
|---|---|---|---|---|---|
| control handshake / STREAM_INFO | `0x00` | 18–65 B | both | frames 142–154 | — |
| **RTT echo ×10** | `0x03` | **548 B** | client→console, echoed back | frames 155–175 | frames 955–977 |
| **MTU-in** (downstream probe) | `0x02` | **1426 B** | console→client | frame 180 | frame 982 |
| **MTU-out** (upstream probe) | `0x03` | **1226 B** | client→console, echoed back | frames 187–188 | frames 990–991 |

Corroborating detail:

- **The echo is a byte-for-byte reflection.** In `session8` all **10/10** echo replies are payload-identical
  to the ping that produced them — so RTT is the only thing the exchange measures, and a client can time it
  without parsing the body.
- **The probe sizes are stable across sessions**: 548 B pings, a 1426 B downstream probe and a 1226 B
  upstream probe in *both* captures, on different dates and different ephemeral client ports.
- **`cap47` shows a real loss/retry**, which is why the implementation's "majority of 10" rule matters: the
  first ping (frame 955) went unanswered for ~200 ms, was retried at frame 958, and the remaining ten
  completed at ~5 ms intervals — 11 sent, 10 echoed. A probe that demanded all ten would have failed a
  perfectly healthy session.
- Note the direction asymmetry: **MTU-in is type `0x02` and console-originated; MTU-out is type `0x03`** and
  reuses the echo mechanism. The two legs are not symmetric in either base type or origin.

**Scope of the claim:** this is the ordering the console and vendor client *do* follow in our sessions. It is
not evidence that the order is mandatory, nor that a client may not reorder or skip legs —
`HalyardSenkusha` deliberately treats probe failure as non-fatal, which is our design choice, not a
protocol requirement (§6). The `BANDWIDTH_COMMAND` leg is absent from every capture we hold and stays `[X]`.

---

## 7. Implementation checklist for `IHalyardSessionCrypto` / the transport

Ready to build now (spec complete, test vectors green):
- [x] Control `/sess/ctrl` field crypto — `kdf_reimpl.py` + `field_iv_reimpl.py` + `rp_auth_reimpl.py`.
- [x] Streaminfo build + `AES-128-OFB(out1)` encrypt/decrypt — `streaminfo_reimpl.py` + the JSON schema.
- [x] control-plane protobuf messages — `stream_control.proto` (generate bindings with `protoc`).
- [x] Stream key agreement + per-packet **AES-128-CTR payload encryption** + 4-byte GMAC —
      `stream_crypto_reimpl.py`; generate a random 16-byte `handshakeKey`, do ECDH-P256, exchange
      `ecdhPublicKey`+`ecdhSignature`, derive per-direction keys, AES-CTR the payload (key=`aes_key`), GMAC it.

Ready to build (transport parse/emit; §3 + §6.1 are code-exact):
- [x] stream-transport packet classes + A/V/feedback header offsets (type, seq, tag, key position) — §3, §6.1.
- [x] A/V data header bit layout (frame/unit/fragment indices, timestamp) — §6.1 (`FUN_100fcb10`).

- [x] **Takion-internal reliability — wire-validated [V]:** INIT/INIT_ACK/COOKIE/COOKIE_ACK handshake +
      DATA/SACK (seq starts at the connection tag, +1/DATA) — §8 (`captures/rudp_control_setup.pcapng`).
      **The secondary RUDP module is NOT the v1 transport** (§8.1) — do not implement it for LAN.

- [x] FEC structure: systematic (N,K) per-frame erasure code; reconstruct iff received ≥ K — §6.2.

Remaining RE before a robust *lossy-link* receiver (no crypto/schema/wire-format unknowns):
- [ ] Takion reliability *timing*: RTO computation, retransmit timers, window sizing (`a_rwnd=0x19000`),
      congestion response — tunable, not wire format (§8.2). A minimal in-order+timeout layer suffices for clean-LAN.
- [x] pre-SCTP `0x06`/`0x07` exchange **fully resolved** = `PROTOCOL_VERSION_REQUEST`/`…ACK` version
      negotiation (`FUN_100ca4e0`); the two 16-byte tokens are the endpoints' **IPv6 addresses** (socket
      getLocalAddr/getRemoteAddr, `FUN_10093f20`/`FUN_10093de0`, AF_INET6 `sin6_addr`) — §8.
- [x] FEC = systematic **Cauchy-matrix** Reed-Solomon over GF(2⁸) [C] — §6.2. Decode path and the matrix
      construction `inverse(i XOR (m + j))` both read from our binary (`FUN_101035e0`); Vandermonde ruled out.
- [x] FEC exact GF primitive polynomial — **`0x11D`** (§6.2 [V]); the runtime inverse table was dumped from
      the live client and matches on all 255 defined entries, no other candidate matching more than one.
- [x] A/V bit-packed field semantics — **resolved** (§6.1): `frame_index`, `unit_index`,
      `total_units`, `parity_units`, `codec` (DLL-confirmed in `FUN_100fcb10`; names [X]).
- [x] **A/V confidentiality — CORRECTED:** media **is encrypted (AES-128-CTR, key=`aes_key`,
      nonce=`base_iv+key_pos/16`)** and GMAC-authenticated — §5.5. (Prior "not encrypted" claim was an
      incomplete trace; the CTR path was re-derived from our binary, `FUN_1012e070`/`FUN_1012df00`.)
- [x] **Wire-validated the transport against a live LAN capture** — proved reliability is Takion-internal
      (SCTP INIT/COOKIE/SACK), not the secondary RUDP module; §8, `captures/rudp_control_setup.pcapng`.

---

## 8. Transport reliability — Takion (SCTP-over-UDP)  **[V]** wire-validated

**v1 LAN reliability is provided by Takion itself, not by a separate reliable-UDP layer. Takion's reliable layer
is essentially SCTP carried over UDP** — the diagnostic strings referenced by `FUN_10129910`/`FUN_10129cc0`
name the SCTP cookie-handshake chunks and their verification-tag mismatch case, and the wire chunk types are
SCTP's exactly. This was
settled by a live LAN capture (`captures/rudp_control_setup.pcapng`, client ↔ PS5 on the same subnet): the
entire session — control, A/V, feedback, congestion, FEC — runs over a **single Takion
UDP socket** (the console-side port is negotiated via the streaminfo `port`; this session used **9297**, an
earlier one 9296), multiplexed by the base-type byte (§3). **No secondary-RUDP packets appear on the wire** (zero
datagrams with `byte0 ≥ 0x80`; the `b0>>6` SYN/DATA framing in §8.1 is never seen). Reliability rides in the
base-type-`00` **CONTROL** packets as SCTP chunks.

> **Correction (wire-validated, supersedes the prior static-analysis note):** an earlier revision inferred
> from a call-graph pass that v1 rode a distinct RUDP layer under the control channel. The capture disproves
> that — the connection handshake is Takion's own INIT/COOKIE exchange, and the secondary RUDP module
> (`FUN_10234cf0`, documented at the end of this section) is present in the binary but is the **modern/PSN
> transport**, not used on the v1 LAN path. The static
> pass missed the chunk-level dispatch inside the message iterator (`FUN_101022a0`); the wire is authoritative.

**Connection handshake** — the SCTP 4-way (in CONTROL packets; `chunk_type` at byte 13 of the Takion message
header, §3). The client **active-opens**. Observed on the wire:
```
client → server : INIT        (chunk_type=0x01)  payload{ tag, a_rwnd=0x19000, out_streams=0x64,
                                                           in_streams=0x64, initial_seq = tag }
server → client : INIT_ACK    (chunk_type=0x02)  payload{ server tag, a_rwnd, streams, seq, 0x20-byte cookie }
client → server : COOKIE_ECHO (chunk_type=0x0a)  echoes the 0x20-byte cookie
server → client : COOKIE_ACK  (chunk_type=0x0b)  → ESTABLISHED
```
Each side's `tag` is the SCTP **verification tag** (`vtag`) — a random 32-bit connection id the peer must echo
in every subsequent packet's message-header tag field; it also seeds that direction's `initial_seq`.
Chunk-type values (byte 13) are SCTP's: `DATA=0x00, INIT=0x01, INIT_ACK=0x02, SACK=0x03, COOKIE_ECHO=0x0a,
COOKIE_ACK=0x0b` — all wire-observed.

**Reliable delivery.** Reliable payloads (the `SESSION_REQUEST`/`SESSION_REPLY`, `STREAMINFO`, input) travel
as **DATA** chunks acknowledged by **SACK** chunks. A DATA chunk payload begins
`{ seq_num u32, channel u16, 3 reserved bytes (0x000000), <payload> }` — i.e. the payload of the **first (or
only)** fragment starts at **value offset 9** (**[V]**, pinned by parsing captured DATA packets against the
schema; a prior revision said offset 8 / `0 u16`, off by one byte). A **continuation fragment** reserves only
**2** bytes, so its payload starts at **value offset 8** (**[V]**, wire-confirmed reassembling a fragmented
SESSION_REQUEST: first fragment @9 + continuation @8 recovers the exact protobuf). "First" is positional (the
flags carry only the end bit, §6.1's ending-bit convention), so a reassembler tracks it from its own state.
The **channel** is a per-class stream id: the client uses `0x0015` for the version exchange, `0x0001` for
session/control, `0x0008` for the bandwidth probe; the server replies on channel `0x0000`. **Each direction's `seq_num` starts at its connection tag and increments once per
DATA chunk** — wire-confirmed: client `0x4823 → 0x4824 → 0x4825…`, server `0x00b18ccf → 0x00b18cd0…`. A DATA
chunk leaves the retransmit queue when its seq is SACKed. (Unreliable A/V/feedback/congestion use base types
1/2/3/5 directly and are **not** carried in DATA chunks — they rely on FEC, not retransmit.)

**Before the SCTP handshake: the protocol-version negotiation [C].** ~4 s before the INIT, the capture shows a
short exchange of 96-byte datagrams (base byte `0x06` request / `0x07` ack). This is the
**`PROTOCOL_VERSION_REQUEST` / `PROTOCOL_VERSION_ACK`** exchange emitted by `FUN_100ca4e0` — identified from
the paired send/receive log-format strings referenced by that function, which name the request and its ack —
and it negotiates the protocol version **before** the SCTP connection is opened and the
`SESSION_REQUEST`/`SESSION_REPLY` pair is sent. It is
retransmitted until acked (hence the duplicate datagrams). In `FUN_100ca4e0` the message object is stamped
with type `0x1f` (=31, `PROTOCOL_VERSION_REQUEST`) and filled from a static **supported-version table**
(`DAT_102bb530`, 11 entries = versions 7–17, §3).

**The message *content* is just the version list.** The
`PROTOCOL_VERSION_REQUEST` payload is `ProtocolVersionRequestPayload { repeated uint32 supportedVersions }` and
the ack is `ProtocolVersionAckPayload { uint32 protocolVersion }` (§4.x). So the **two 16-byte tokens on the wire are NOT part
of the message** — they belong to the reliable **TRP-channel transport framing** (`FUN_100ca4e0` opens a
"trp-client" channel with the socket's local/peer identity, `+0x50`/`+0x44`, and sends the request over it).
Wire layout of the raw datagram: `type u32` + two 32-byte endpoint descriptors `{ 4-byte tag, 16-byte value,
12-byte pad }` (local, remote) + a small per-direction trailer.

**What the 16-byte tokens are — RESOLVED [C]: the endpoints' IPv6 addresses.** They are **not** random GUIDs,
not the MachineGuid, and not keys — they are **socket addresses**, confirmed by decompiling the getters. Trace:
`FUN_100ca4e0` fetches them from the streaming socket via vtable `+0x50` (local) and `+0x44` (remote); the
socket class is built by the factory `FUN_100492f0` (vtable `PTR_FUN_102b711c`, object `0x1f0` bytes, stored
at connection `+0x44` by the ctor `FUN_100c9ae0`). Both getters (`+0x50` = `FUN_10093f20`, `+0x44` =
`FUN_10093de0`) return the socket's stored address: for family `AF_INET` (`0x2`) the 4-byte IPv4 address, and
for family **`AF_INET6` (`0x17`) the 16-byte `sin6_addr`**. Since the wire carries 16 bytes, the socket is
AF_INET6 and each token is the endpoint's **IPv6 address**. Consistent with the capture: the client token was a well-formed
**Unique Local Address** in `fc00::/7` (shown here as the synthetic
`fd00:1a2b:3c4d:5e6f:0011:2233:4455:6677` — the real value is a per-site identifier and is redacted, like
every other per-device value in this spec). The observed datagrams were IPv4 (172.16.0.x), so these are the
endpoints' **IPv6 identities** the transport uses for the connection — one per peer, local + remote. What
matters for interop is the *form*, not the value: 16 bytes of `sin6_addr`, and a ULA is what a consumer
network's stack will hand you.
> **Note:** these TRP endpoint tokens are unrelated to the `RP-Did` *control* field (which our client fills
> from the MachineGuid, §2.1). An earlier "random token" guess here was wrong; the binary shows an address.
>
> **Implementation:** the token is just the endpoint's (IPv6) address as the socket sees it; supply the local
> address, echo the peer's. The `PROTOCOL_VERSION_REQUEST` step is itself skippable — a client may simply
> assume the version. Nothing here blocks interop. **The transport is now fully resolved end-to-end.**

### 8.1 The secondary RUDP module — present in the binary, **NOT on the v1 LAN wire**

The following was reversed from `FUN_10234cf0` (parser) / `FUN_10235580` (serializer) and originally assumed
to be the v1 transport; **the live capture shows it is not used on the v1 LAN path** (no matching packets on
the wire). It is retained here as documentation of the binary's *other* reliable-UDP transport (the
modern/PSN path). Do **not** implement it for v1 LAN play.

**RUDP packet header** (parse order; struct offsets shown for cross-reference):
```
u8    b0            type = b0 >> 6   (2 bits: 2=DATA, 3=SYN/control)   flags = b0 & 0x3f (6 bits)
u8    subChannel    (struct+0x14)
u16   len/window    (struct+0x8)
[flag 0x20]  u16    (struct+0x0a)
[flag 0x04]  u16    (struct+0x10)         # requires flag 0x20 also set
--- if type == 2 (DATA): ---
u16   dflags        bit11(0x800)->F0, bit9(0x200)->F1, bit8(0x100)->F2; low 4 bits -> struct+0x15
u16   seq           (struct+0x2)          # sequence number
u16   ack           (struct+0x4)          # cumulative ack
u32   timestamp     (struct+0xc)
u16   (struct+0x12)
--- if type == 3 (SYN / control): ---
u16   (low byte -> struct+0x16)
u32   timestamp     (struct+0xc)
--- end type-specific ---
[flag 0x10]  OPTIONS — TLV loop, each option:
    u8 tag   (high bit 0x80 = "more options follow")
    u8 len
    tag 1 : SACK block, len 1..32 bytes    -> struct+0x18  (count -> struct+0x38)
    tag 2 : len 2, u16                     -> struct+0x12
    tag 5 : selective-ack gap ranges, count = len>>2, then count × { u16 start, u16 end } -> struct+0x3e…
    other : skip `len` bytes
```
So a DATA packet is `[type|flags][subCh][len][opt u16s][dflags][seq][ack][timestamp][x]` + options; a SYN/
control packet is shorter (`[type|flags][subCh][len][…][u16][timestamp]` + options). The reliable-transport key
position (§3, §5) travels inside the reliable payload, above this RUDP header. The aggregator
(the RUDP aggregator module) may pack multiple control-plane messages into one datagram. Field *roles* (seq/ack/
timestamp/SACK) are inferred from structure + the reliable-UDP transport semantics; the byte/bit layout is code-exact.

**Connection state machine** (the RUDP networker module; TCP-like): `IDLE → SYN_SENT → ESTABLISHED`
(active open: send SYN=type 3, receive SYN-ACK, send ACK) with the passive path via `SYN_RCVD`, and
teardown `ESTABLISHED → CLOSE_WAIT → CLOSED`. The client does the **active open**: send a type-3 SYN,
reach `ESTABLISHED`, then exchange DATA (type 2). Errors surface as "state is not established" / "state
is closed".

**Send / reliability model** (`FUN_10232d70`, the Context send dispatcher, under a lock): three commands —
(1) **queue a reliable segment**: insert into a **sorted-by-sequence retransmit list** (`qsort`/`bsearch`
by seq; tracks the highest seq) and transmit (type 2 DATA); (2) send an unreliable data segment directly;
(3) send a control packet immediately (type 3). Acknowledgement is the header's cumulative `ack` field plus
the **SACK options** (§8 tag 1/5) for gap ranges; a segment leaves the retransmit list once acked.

### 8.2 Remaining reliability/tuning items (v1)

The **wire format and handshake are now validated** (§8, `[V]`). What's left is **tunable behaviour** of the
Takion DATA/SACK loop, not wire format: RTO computation, retransmit timers, and send/receive window
sizing (the advertised window is `a_rwnd = 0x19000`). For a **clean-LAN first picture** a minimal
in-order + timeout-retransmit of DATA/SACK is sufficient; fuller fidelity only matters on lossy links.
Congestion is reported via base-type-5 packets — **every 200 ms**, `{received u16, lost u16}` counts [X].
Media-side remainder (FEC is fully settled as of 2026-08-02 — §6.2): the pre-stream **bandwidth/MTU
probe** (§6) — a Takion sub-flow using the [`bandwidth_probe.proto`](bandwidth_probe.proto) message set.
Its **sequence ordering is [W]** as of 2026-08-02, wire-observed identically in two of our own independent
captures (see §6.4); the `BANDWIDTH_COMMAND` leg remains **[X]**, never having been observed at all.

---

See `captures/lab-notebook.md` (gitignored dirty room; not in a published copy) "Session 8" for the full Ghidra trace and the reusable
`rpctrl_proj` Ghidra project.
