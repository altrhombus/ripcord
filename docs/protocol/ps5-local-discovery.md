# PS5 Remote Play — local discovery (UDP 9302 SRCH) and the TCP control-channel path

Status: draft, from one capture session (see provenance log): a connection to a previously-known
console made entirely on the LAN **with the console's own internet access blocked** (the client
device itself still had internet). This isolates what a connection looks like when nothing on the
console side can reach PSN cloud services, and reveals a full discovery protocol and an alternate,
TCP-based control-channel path not seen in any earlier capture.

Source: own packet capture of network traffic on the author's own LAN. Capture and this document
written 2026-07-10. No source code from any existing Remote Play client project was consulted -
see `docs/protocol-research-log.md`.

**Redaction note**: the console's discovery response contains a hardware identifier (structurally
a MAC address) and a user-assigned device nickname - neither is reproduced here, only their field
names/formats. No IP addresses are reproduced - see the general redaction policy in
`ps5-network-architecture.md`.

## Why this capture looks different

Every earlier capture involved a client that either already had the console in its PSN-cloud
"known console" list (populated via account sign-in, itself requiring the console to have been
cloud-reachable at some point) or discovered it via mDNS. With the console's internet access
blocked for this session, neither cloud-derived path was available for *this* connection attempt,
and the client fell back to genuinely local mechanisms: a UDP broadcast discovery protocol, and -
distinctly from every prior capture - a **plain TCP** control channel instead of the RUDP-wrapped
UDP control channel documented in `ps5-session-transport.md`.

The client itself retained normal internet access throughout (confirmed via TLS SNI to PSN cloud
hosts elsewhere in the capture, none of which ever reached the console) - only the console was
cut off.

## UDP 9302 — `SRCH` discovery broadcast

A single request/response pair was observed on UDP port 9302, matching the port public
router-configuration documentation already cited in the research log names as part of the
discovery/handshake range.

**Request** (client → LAN broadcast address):

```
SRCH * HTTP/1.1
device-discovery-protocol-version:00030010

```

**Response** (console → client, unicast):

```
HTTP/1.1 200 Ok
host-id:<12 hex digits>
host-type:PS5
host-name:<user-assigned device name>
host-request-port:997
device-discovery-protocol-version:00030010
system-version:<8-digit numeric string>

```

Notes:
- Both request and response are plain ASCII, `\r\n`-terminated, HTTP-status-line-like but not full
  HTTP (no headers section separator beyond the trailing blank line, no `Host:`/`User-Agent:`
  etc.) - a lighter-weight protocol than the `/sess/*` exchanges.
- `device-discovery-protocol-version` is echoed identically in the response - a version-match
  check.
- `host-id` is a 12-hex-digit value, structurally consistent with a MAC address (matches the
  6-hex-digit suffix seen in this same console's mDNS hostname in earlier captures, i.e. the
  mDNS suffix is the back half of this full identifier). Not reproduced here - see redaction note.
- `host-name` is a user-assigned nickname for the console (distinct from the `PS5-<hex>` mDNS
  hostname) - also not reproduced.

  **The vendor persists this identity and not the address (2026-09-06).** From first-party static RE of our
  own client's the vendor control DLL string table, the per-console record fields are, per family:

  ```
  PS4-Mac:        PS5-Mac:
  PS4-RegistKey:  PS5-RegistKey:
  PS4-Nickname:   PS5-Nickname:
  ```

  Three fields, and **no address among them** — so a console is keyed by its MAC and its address is
  rediscovered rather than remembered. That answers "how does the vendor survive a DHCP lease change": it
  has nothing to go stale. It also converges with the note above — `host-id` is 12 hex digits structurally
  consistent with a MAC, so the value we key on *is* the value the vendor calls `Mac`, arrived at
  independently.

  **`[C]` on the field names, `[X]` on the interpretation.** The strings are certain; that they are a
  *persisted record* is inferred from their `key: value` shape, from being distinct from the wire header
  (`RP-Registkey: %s`, different capitalisation and prefix), and from the field set being exactly what a
  client would need to keep. **Not corroborated against real stored data** — the vendor client is not
  installed on the machine this was done on, so its actual store was never located or read.

  Ripcord differs deliberately: it keeps the last address as a *cache* and rediscovers by `host-id` only
  when a probe at that address fails. Faster in the common case, identical in outcome, at the cost of the
  stored address being briefly wrong where the vendor's simply does not exist.
- `host-request-port: 997` - differs from the port 987 cited in the public router-configuration
  documentation referenced in the research log for "remote wakeup." **Resolved by cap53 (2026-08-03):
  it was the PS4-vs-PS5-difference hypothesis.** 987 is a **PS4**'s discovery/wake port; a PS5 discovers
  and wakes on **9302**; and the advertised `host-request-port:997` is not where wake goes on either (see
  the LAN wake and PS4-family sections below). This capture did not exercise the announced port.
- `system-version` is an 8-digit numeric firmware version string. Confirms the plan's assumption
  that firmware version is discoverable up front, before any session is established - useful for
  the version-detection mitigation noted against the "console firmware updates silently change
  wire format" risk.

No further use of the discovered `host-request-port` was observed in this capture - the client
proceeded directly to the TCP control channel below, using the console's already-known LAN address
and control port from... elsewhere (most plausibly a cached value from a prior session, since this
is a "known console"; a genuinely first-ever local-only discovery would need its own capture to
confirm how the request/wake port fits in).

## The alternate control channel: plain TCP

Where every earlier capture's session-establishment exchange (`ps5-session-establishment.md`) rode
on RUDP-wrapped UDP port 9303, this capture's equivalent exchange happened over a **plain TCP**
connection to port 9295 (the low end of the same 9295-9297 port pool documented in
`ps5-session-transport.md` - see that document's note on port selection being pool-based, not
fixed).

Observed behavior:
- Two TCP connections were opened to port 9295, both immediately following the SRCH exchange.
- The first (`Connection: close`) carried a single `GET /sie/ps5/rp/sess/init HTTP/1.2` request/
  response pair, then closed.
- The second (`Connection: keep-alive`) carried `GET /sie/ps5/rp/sess/ctrl HTTP/1.1`, then **stayed
  open for the rest of the session**, carrying the same `RPCS`-magic binary control frames
  documented in `ps5-session-transport.md` - byte-for-byte the same frame format (`RPCS` + 4-byte
  big-endian length + payload), just carried directly by the TCP byte stream instead of wrapped in
  the RUDP framing. This confirms the `RPCS` frame format is transport-agnostic.
- **`POST /sie/ps5/rp/sess/rgst` was never sent** - the client went straight from SRCH discovery to
  `/sess/init`, presenting a registration key that was still accepted. Confirmed by searching the
  entire capture for the `rgst` string: no match.
- The registration key presented was the same value observed in every other capture of this
  console+account pair (LAN reconnect, WAN, and the online-first-pairing capture) - i.e. it is
  genuinely a stable, transport-independent, account+console-scoped identifier, not something
  re-issued per transport or per network path.
- The request/response headers (`RP-Registkey`, `RP-Pubkey`, `RP-Hmac`, `RP-Nonce`, `RP-DevACha`,
  `RP-DevAChaTag`, `RP-Data`, `RP-Tag`) are otherwise identical in name and apparent structure to
  the UDP/9303 exchange - only the transport differs, not the application-layer protocol.
- `User-Agent` on this capture's requests was `remoteplay OSX` (see the platform-varies note
  already in `ps5-session-establishment.md`).

**Read as**: the console (or the client, or both, adaptively) chooses between two equivalent
control-channel transports - RUDP-over-UDP/9303 for cloud/relay-mediated connections, and plain
TCP on a pool port for connections to a directly-reachable LAN console. Both carry the identical
`RPCS` binary frame protocol once established.

**Update (2026-07-12) - the TCP path is not offline-specific.** This capture first suggested the
TCP transport might be tied to the console lacking internet access. A later home-LAN capture
(`session5`, console fully online, normal cloud sign-in completed) *also* used plain TCP on a pool
port (9295) for control, with the UDP stream on 9296/9297. So the TCP control channel is the normal
path for a **directly-reachable LAN console**, regardless of the console's own internet state - not
a fallback for offline consoles. The RUDP/UDP-9303 path, by contrast, was seen on the cloud-relay
WAN case. Current best reading: **LAN-direct → TCP control; relay/WAN → RUDP-over-UDP control.** The
`/sess/rgst`-skip question below is therefore better framed as "does the LAN-direct/TCP path skip
`rgst`" - still not fully isolated, but no longer plausibly explained by the console being offline,
since the online-console LAN case behaved the same way.

## The stream

The high-volume A/V+input stream in this capture ran on UDP port 9296 (not 9297 - again, pool
selection, not a fixed port), and its packet structure (channel byte, size classes: ~1459/1466 B
video fragments, 310 B audio, 70/71 B control) is identical to the 9297 stream documented in
`ps5-av-stream.md`. No differences were found beyond the port number itself.

## What this changes for the plan

- **A LAN-only implementation must support two control-channel transports**, not just the
  RUDP/UDP-9303 one documented first: a plain-TCP alternative on a pool port, which now looks like
  the **primary** path for a directly-reachable LAN console (seen in both the offline-console case
  and a fully-online home-LAN case, `session5`). One of the two questions this section previously
  raised is answered: a cloud-reachable console *does* use the TCP path. Prioritize TCP control for
  Phase 1's LAN target accordingly.
- **`/sess/rgst` is not always required** - a client presenting a valid, previously-obtained
  registration key can sometimes skip straight to `/sess/init`. Combined with the online-pairing
  capture's finding that `/sess/rgst` *was* present there, this suggests `/sess/rgst`'s presence
  correlates with transport/path chosen (or with whether the key needs cloud re-validation) rather
  than with "first connection ever."
- **UDP 9302 SRCH discovery is now fully specified** and ready to implement for Phase 1's LAN
  discovery.

## LAN wake (rest mode -> awake)  **[V]** from cap49 (2026-08-03)

Confirmed by capturing the vendor client waking a console with the **console's internet blocked at the
router**: the wake is a purely local exchange, no cloud. The sequence, all on UDP **9302** (the discovery
port):

```
client -> 9302  SRCH * HTTP/1.1 ...                 (broadcast)
console         HTTP/1.1 620 Server Standby ...      (the sleeping-console status line)
client -> 9302  WAKEUP * HTTP/1.1 ...                (the wake; no reply)
client -> 9302  SRCH * HTTP/1.1 ...                  (poll)
console         HTTP/1.1 200 Ok ...                  (awake)
```

**The WAKEUP datagram** (verbatim; note the lines are **LF-terminated, not CRLF**, unlike SRCH):

```
WAKEUP * HTTP/1.1
client-type:vr
auth-type:R
model:w
app-type:r
user-credential:<registkey-dec>
device-discovery-protocol-version:00030010
```

The single-letter values (`client-type:vr`, `model:w`, `app-type:r`, `auth-type:R`) are copied verbatim;
their meaning is not separately derived. **`user-credential`** is fully derived: the RegistKey read as a
base-16 integer, in decimal. cap49's `<registkey-dec>` = `0x<registkey-hex>`, and the RegistKey wire value
`<registkey-wire>` hex-decodes to the ASCII `"<registkey-hex>"`. So
`user-credential = int(ascii(hex_decode(RP-Registkey)), 16)`.

**The advertised `host-request-port` is a red herring for wake.** The standby reply advertises
`host-request-port:997`, but the WAKEUP goes to **9302**, not 997 and not the public-documentation 987. At
protocol version `00030010` the wake rides the discovery port, so the 997-vs-987 discrepancy below is moot
for this purpose.

**Not yet captured: the reverse (awake -> rest mode).** The disconnect-to-standby command in cap49 went over
the encrypted Takion control channel after the plaintext TCP control channel closed, so it is not readable
here.

## PS4 — same protocol, family-parameterised  **[W]** from cap53 (2026-08-03)

A PS4 remote-play session (account/PIN login, then LAN reconnects) captured on the same LAN shows the PS4
speaks the **same** Remote Play protocol as the PS5 documented above — the discovery/wake exchange, the TCP
9295 HTTP-like control channel with an encrypted body, and the UDP 9296/9297 Takion four-way handshake all
match. Only **three** wire values differ, and each is a straightforward console-family parameter:

| Parameter | PS4 (cap53) | PS5 (cap49 / earlier) |
|---|---|---|
| Discovery + wake UDP port (`SRCH`/`WAKEUP`) | **987** | **9302** |
| `device-discovery-protocol-version` (SRCH + WAKEUP) | **`00020020`** | **`00030010`** |
| Control-listener-arming probe (UDP 9295 broadcast) | **`SRC2`** → reply **`RES2`** | `SRC3` → `RES3` |
| `/sess/*` URL family segment | **`/sie/ps4/rp/sess/…`** | `/sie/ps5/rp/sess/…` |
| `RP-Version` header on `/sess/rgst` | **`10.0`** | `1.0` |

All five were **already in our code as PS4 groundwork** (`HalyardConsolePlatform.Ps4`,
`HalyardRegistrationMessage.EndpointPath`/`RpVersion`, `HalyardControlSearch`) — cap53 is their first
on-wire confirmation, so this validates existing behaviour rather than changing it. Note there are **two**
distinct probes, exactly as on PS5: the 4-byte binary `SRC2`/`RES2` that arms the console's TCP 9295 control
listener (cap53 frames 9913 client→`.255:9295` `SRC2`, 9917 console→client `RES2 01 05 00`), and the
HTTP-like `SRCH * HTTP/1.1` discovery/wake broadcast on 987. Do not conflate them.

Everything else is byte-for-byte the same shape: the `SRCH` (CRLF-terminated) and `WAKEUP` (LF-terminated)
line formats, the full WAKEUP field set (`client-type:vr` / `auth-type:R` / `model:w` / `app-type:r` /
`user-credential` / `device-discovery-protocol-version`), and the
`user-credential = int(ascii(hex_decode(RP-Registkey)), 16)` derivation — the PS4's credential is **negative**,
which pins the field as a **signed** 32-bit decimal (a detail the positive PS5 sample left ambiguous). The
`/sie/ps4/…` path had been noted in `ps5-remoteplay-v1-spec.md` §2.0 only as DLL-present; this is its first
on-wire confirmation.

**That 987 actually wakes a PS4 — settled `[V]` (cap54–cap57, 2026-08-03).** cap53's wake drew zero replies
because that console still had network-wake disabled; once re-enabled, all of cap54/55/56/57 show the console
answering `SRCH` with **`620 Server Standby`** (resting), then — after a `WAKEUP` to `:987` — **`200 Ok`**
(awake) ~20 s later, followed by a stream. The `WAKEUP` is the only stimulus in between, so the PS4 LAN wake
path is fully `[V]`, mechanism and failure mode both.
### PS4 session control-field crypto — derived and validated **[V]** (2026-08-03)

Initially this did **not** reproduce with our PS5 control-field KDF (a full parameter sweep failed). The cause
was the KDF *variant*: the dispatcher `FUN_101ede80` selects one of ~15 variants by a **mode** (protocol/
version discriminator; PS5 = 1, PS4 = 0) and then by **RP-KeyType** (1/2/3/4/6). PS5 is (mode 1, keytype 2) →
`FUN_1fe340` (our existing `HalyardControlKdf`); **PS4 is (mode 0, keytype 2) → `FUN_1fdd80`**, reversed
statically from our own DLL:

```
material[i] = ((nonce[i] + 0x36 + i) & 0xff) ^ Tb[i]                        # Tb @ RVA 0x2fc86d, idx nonce[0]>>3
key[j]      = (((Ta[j] ^ companion[j]) + 0x21 + j) & 0xff) ^ nonce[j]       # Ta @ RVA 0x2fba45, idx nonce[7]>>3
```

Same role convention as PS5 (companion-derived = AES key, nonce-derived = IV material) and the same
field-IV/CFB machinery; only the context key differs, and it follows from the mode — `select_context_key(sel1=0)`
→ **`B_eq_0`** (PS5's mode 1 → `B_eq_1`). Validated three ways against cap53: all three sessions' on-wire
`RP-Auth` reproduce byte-for-byte, and `RP-OSType` decrypts to the readable ASCII `Win11.0\0`. Implementation
needs PS4's two KDF tables added to the bundled interop constants (widening the committed-constants exception →
amend `NOTICE` + `CLAUDE.md`) and the KDF/context selection keyed on console family.

### PS4 A/V stream key schedule — same as PS5, **[V]** (2026-08-03)

cap53's Takion `SESSION_REPLY` (frame 10089) parses as the **identical** PS5-v17 protobuf: `clientVersion = 17`,
`ecdhPublicKey` = 133-byte **P-521**, `ecdhSignature` = 32-byte HMAC-SHA256, empty `encryptedKey`. So the PS4
this project targets negotiates the **modern P-521/v17 stream handshake** — *not* a P-256 "older protocol" (an
early high-uncertainty note, now corrected). Unlike the control-field KDF (dispatched by `mode`, giving PS4 its
own variant), the stream key schedule `DeriveDirection` is a plain SP800-108 block with **no console-family or
mode input** — only the curve is version-dependent, and `clientVersion 17` selects P-521 through the existing
`CurveForVersion`. So `HalyardStreamKeySchedule` already covers PS4; no PS4-specific stream algorithm exists to
derive. Handshake structure is `[W]` (parsed from cap53); the key-schedule *identity* is `[C]` — a passive
capture has neither ECDH private key, so the derived A/V keys can't be reproduced from cap53. Final `[V]` needs
a live PS4 stream-key dump (as done for PS5), which would also rule out any separate `<engine module>` re-dispatch.

**Validated on PS4 hardware + statically (2026-08-03), two independent ways:**
- **handshakeKey + ecdhSignature `[V]`** — live Frida dumps of the client's stream-enable path gave the session
  handshakeKey for two sessions (cap54, cap57), and each console `SESSION_REPLY` satisfies
  `ecdhSignature == HMAC-SHA256(handshakeKey, ecdhPublicKey)` byte-for-byte.
- **`DeriveDirection` `[V]` — static proof, stronger than a dump.** The per-direction KDF is
  `generateKeyIV = FUN_1012d9e0` in the vendor control DLL (the clean v1 binary PS4 aligns with). It builds
  `info = 01 ‖ dir ‖ 00 ‖ handshakeKey ‖ 01 00`, `out = HMAC-SHA256(ECDH_secret, info)`, `key = out[0:16]`,
  `IV = out[16:32]` — byte-identical to `HalyardStreamKeySchedule.DeriveDirection` (it even confirms the `01 00`
  length field). It has **no internal version/family branch** and **exactly two call sites**, both in the single
  ECDH key-agreement routine (`dir=2` / `dir=3`), gated only by ECDH success — **no dispatcher**, unlike the
  control KDF. Since this function's output was live-validated against PS5 hardware (2026-07-22) and PS4 provably
  runs the identical unconditional code, `DeriveDirection` is `[V]` for every PS4 session. The earlier heap-key
  search came up empty (the raw key isn't resident contiguously at dump time) — moot, since the static proof
  supersedes it.

**Nothing open for PS4.** The 987 wake is now `[V]` (cap54–cap57 show 620→WAKEUP→200), and the crypto path is
complete — every layer `[V]`/`[W]`.

## What's still needed

- A capture of local discovery from a **console that does have internet access**, to see whether
  the TCP control-channel path is specific to the console being offline, or is chosen for some
  other reason (e.g. always used for SRCH-discovered consoles regardless of their own internet
  state).
- ~~Resolve the `host-request-port` 997-vs-987 discrepancy~~ / ~~determine what runs on the advertised
  `host-request-port`~~ — **resolved.** Wake does not use the advertised port at all: a PS5 wakes on 9302
  (cap49), a PS4 on 987 (cap53). The 987-vs-997 split was a PS4-vs-PS5 difference. See the LAN wake and
  PS4-family sections above.
- **Confirm a PS4 actually wakes on 987** — cap53's wake got no reply (network-wake was disabled on that
  console). Re-capture with network-wake enabled to promote the PS4 wake from `[W]` (packet) to `[V]` (wakes).
- **Confirm PS4 session crypto matches PS5** by decrypting a cap53 session with the PS4 registration key.
- The **awake -> rest mode** command, which cap49 could not read (it is on the encrypted Takion channel).
- A capture of local discovery + `/sess/rgst` actually occurring over the TCP path, to see whether
  `/sess/rgst` uses the same TCP transport or falls back to something else when it does occur.
