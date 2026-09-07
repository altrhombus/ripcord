# PS5 Remote Play — session transport (RUDP framing)

Status: draft, based on one capture session (see provenance log). Covers the **reconnect to an
already-registered console** flow only — first-time registration/pairing was not captured yet
(see `ps5-session-establishment.md` for why, and what's still needed).

Source: own packet capture of network traffic on the author's own LAN between a real PS5 console
and a connected client, at IP/port level only. Capture and this document written 2026-07-10. No
source code from any existing Remote Play client project was consulted for this document — see
`docs/protocol-research-log.md`.

## Scope of this document

The low-level reliable-UDP-style framing layer carrying both the HTTP-style session-establishment
exchange (`ps5-session-establishment.md`) and the binary `RPCS` control frames described below.
Video/audio streaming (a separate UDP flow, different port) and controller-input framing are not
covered here — see the "Not yet captured" section.

## Transport overview

| | |
|---|---|
| Protocol | UDP |
| Observed control port | 9303 (destination on the console side for this exchange) |
| Client port | ephemeral, one per session (52533 in the captured session) |
| Framing | Custom reliable-UDP-style layer, not a standard protocol Wireshark recognizes |

A separate, much higher-volume UDP flow was observed on port 9297 immediately after this exchange
completes — the multiplexed audio/video/input/feedback stream, now documented in `ps5-av-stream.md`.
For where both of these fit in the overall client→PSN→console picture (cloud auth, the persistent
WebRTC signaling channel, and this direct LAN link), see `ps5-network-architecture.md`.

## RUDP header (observed on every packet in this flow)

Every UDP payload in this flow begins with a short header before any HTTP or `RPCS` content. Two
header shapes were observed:

**Bootstrap sub-phase** (first 4 packets of the exchange only): 4-byte little-endian value (6, then
7, then 6, then 7 again in the observed exchange - looks like a small packet-type/step counter,
not a full sequence number) followed by an apparent 16-byte value and a 20-byte value. The client's
first packet's 20-byte value reappears at the start of the console's response payload (right after
the response's own 4-byte type field) - a cookie/nonce the console echoes back, standard practice
for anti-spoofing before committing more state to a new UDP peer.

**Main sub-phase** (all subsequent packets), revised in a later multi-capture pass (see
`ps5-wan-relay.md` for the evidence):

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 1 byte | Flags | Constant `0xC0` in every packet checked across every capture. |
| 1 | 1 byte | Payload length | Exactly equals the length of the rest of the UDP payload (wire length minus the 42-byte Ethernet/IPv4/UDP prefix), in every packet checked. |
| 2 | 4 bytes | Fixed magic constant | `24 4F 24 4F` in every capture checked so far - **not a per-session connection ID**. An earlier draft of this document called this "almost certainly a randomly generated per-session connection ID, comparable in purpose to a QUIC connection ID" - that was wrong. The identical value was found at this offset across four independently-established sessions (different client devices, different networks, different times), which a genuinely random per-session ID would not produce. It reads as a fixed protocol/session-type magic instead. |
| 6 | 4 bytes | Unknown, varies per packet | Sequence/ack-like field - still not fully decoded. Now the leading candidate for whatever *does* identify a session/ordering at this layer, since the field once assumed to be a connection ID isn't one. |
| 10+ | rest | Payload | Either an HTTP/1.1 request/response line (see `ps5-session-establishment.md`), the 4-byte ASCII magic `RPCS` marking a binary control frame (below), or a short frame with no visible payload beyond the header (22 or 30 bytes observed) - very likely a pure ACK for the RUDP layer, interleaved between the larger frames in both directions throughout the exchange. |

Every application-level frame (HTTP request/response or `RPCS` frame) was observed to be followed
shortly after by one of these short ACK-like frames from the receiving side, consistent with a
per-frame ACK.

**Superseded — read "Phase 2 — the chunk layer" below instead.** The table above is the capture-only reading
of this same framing, and it is wrong in three ways now that the vendor's own parser has been read: the
constant `0xC0` at offset 0 is a 2-bit word count (3) beside the high bits of an 11-bit length, not a flags
byte; the length is that 11-bit field spanning both bytes, not a byte of its own; and `24 4F 24 4F` is a pair
of 16-bit **port** words (9295, the control port) whose presence is what a word count of 3 *means* — not a
fixed 4-byte magic. It is kept here because the reasoning that killed the "per-session connection ID" reading
is still sound and worth preserving.

## The account-route control transport (UDP 9303) — derived 2026-09-03 **[C]**

> **Provenance.** Derived by parsing this project's own `cap64.pcapng` (the authors' own client, own
> console, own account) with a purpose-written pcapng reader. No third-party implementation was consulted.
> See `docs/protocol-research-log.md`. The account route's `/sess/rgst`, `/sess/init` and `/sess/ctrl` all
> run over this transport; a PIN-route console uses plain TCP 9295 for the same HTTP shapes instead.
>
> **cap64 is a WAN pair.** The client sat on a hotspot (`<client-ip>`) with the console off that network, so
> every exchange below went to the console's reflexive address on 9303. The LAN candidate was probed and
> never answered. **[X] Whether a same-LAN client may skip straight to the chunk layer is untested** — the
> faithful implementation performs the prelude either way.

### Phase 1 — the 88-byte INIT/COOKIE prelude (types 6 and 7)

Five to seven datagrams, all exactly **88 bytes**, sent before any chunk traffic. Layout (all fixed-size,
little-endian for the leading type):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Type, LE32: **6** = INIT (both directions), **7** = COOKIE-ECHO |
| 4 | 20 | **Sender's `localHashedId`** — the same 20-byte value that side published in its signaling OFFER |
| 24 | 12 | Zero |
| 36 | 20 | **Peer's `localHashedId`** — known in advance from the peer's OFFER |
| 56 | 12 | Zero |
| 68 | 4 | Two BE16 fields, `(a, b)`. The opener sends `(0x0001, X)`; the answering side replies with them **swapped**, and keeps that form for the rest of the association. This is how to tell which side opened: in a same-LAN vendor capture the client sent `0001dee0` and the console answered `dee00001`, i.e. the console *responded* |
| 72 | 4 | **A counter, not a LAN/WAN discriminator** `[C]`. Zero in the console's `Init`; in a client's it is a small value that grows over the client's lifetime — surveyed across every 9303 capture we hold it takes `0x00`, `0x03`, `0x17`, `0x19`, `0x1c`, `0x1e`, `0x145`, `0x146`, rising within each capture series. An earlier reading here had it as `0x19` for WAN and `0x40` for same-LAN; that is wrong (the same-LAN no-PIN capture sends `0x145`) and **`0x40` appears in no capture at all**, so Ripcord's chosen value was invented. It travels with the field below: an echo mirrors the pair `(this, token)` from the `Init` it answers |
| 76 | 4 | **A microsecond timestamp, near-certainly:** a live console's value rose by ~500,190 between `Init`s sent half a second apart, every time. So the exchange is a periodic RTT probe rather than a one-shot handshake — the peer probes for as long as the association lives (every 0.5s while opening, every 10s once established) and **each probe must be echoed**. A client that echoes once and goes quiet is dropped after about ten seconds. Formerly read as a **connection token.** In an `Init` it is the *sender's own* (the two sides announce different values); in the client's type-7 it is the *console's*, returned verbatim. A mutual verification-tag exchange rather than a one-way cookie — an earlier draft of this table had it as "zero in the client INIT, set by the console", which the captured bytes falsify |
| 80 | 8 | **The sender's obfuscated view of the peer's own address and port** `[C]`. Zero in an `Init`; in a `CookieEcho`, `peerIPv4 XOR tagPair` (4 bytes) then `peerPort XOR tagPair >> 16` (2 bytes) then two zero bytes — the tag pair being the one in that same datagram. STUN's `XOR-MAPPED-ADDRESS` trick with the tag pair standing in for the magic cookie, and it does the same job: each side tells the other which address it is actually reached on. Confirmed on all six bytes against every echo in every capture we hold, both directions, LAN and WAN. Carried here as "6 further bytes, **[X]** unexplained" until 2026-09-04, and sent as zeroes |

**This is the join between the cloud signaling and the direct transport [C].** The 20-byte blobs are the
`localHashedId` values from the two OFFERs — which is why the earlier reading called them "two ~20-byte
high-entropy blobs of unknown origin". A client therefore cannot open this transport without having
completed the candidate exchange; the console's `localHashedId` arrives only in its OFFER.

### Phase 2 — the chunk layer

Every subsequent datagram is one or more **chunks**, concatenated with no padding:

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | BE16 header. Bits 15..14 are a **count of 16-bit words in the prefix, this word included** — so 1, 2 or 3, and `count - 1` port words follow. Bits 13..11 are **reserved and masked away by the receiver**. Bits 10..0 are the chunk's **total length including this word** `[C]` |
| 2 | 2 | Present when the count is 3: the first port word. `0x244F` = 9295, **the control port** `[C]`. The receiver does not key its lookup on this one, so which of the pair is "source" is `[X]` |
| 4 | 2 | Present when the count is 3: the second port word, likewise `0x244F`. **This is the one the receiver demultiplexes on** `[C]` |
| 6 | 1 | Chunk type (below) — offset 6 for a count of 3, 4 for a count of 2, 2 for a count of 1 |
| 7 | 1 | Flags: `0x30` on nearly all chunks, `0x2F` on a few, `0x00` on the server's cookie/close |
| 8 | .. | Type-dependent |

**The word count, and two superseded readings of it `[C]`.** Read from the vendor library's own receive loop
(`FUN_22c100`), which does:

```c
word  = read_be16();
len   = word & 0x7ff;                    // 11 bits, not 14
count = (word >> 8) >> 6;                // 1..3; zero abandons the datagram
if (len < count * 2) break;              // must cover the prefix it claims
body  = len - count * 2;                 // count includes the header word
```

then reads `count - 1` further words and dispatches on `count - 1`:

- **count 3** (every chunk this protocol sends): two port words, and the connection is looked up by the
  *second* one.
- **count 2**: one port word, which the receiver requires to equal **both** the local and the peer port
  (`ctx+0x206` and `ctx+0x22a`) of the connection it selects.
- **count 1**: no port words; the receiver matches on the peer address record alone.

Two earlier readings in this document are wrong and are superseded. The `0x244F` pair is **not fixed
structure** — it is what a count of 3 means, and a peer may legitimately send either shorter shape, which a
parser assuming an 8-byte prefix puts the type and flags two bytes out of place. And the length is **11 bits,
not 14**: a chunk with anything set in bits 13..11 would be read as thousands of bytes long by a 14-bit
parser, which then abandons the datagram. The 11-bit field also caps a chunk at **2047 bytes total**, so a
sender with more to say must fragment; the 744-byte `rgst` POST fits comfortably, but this is a real limit to
respect rather than discover.

`0xC0…`/`0xC1…` are just the high bits of that length, not distinct message types — a datagram of 286 bytes
begins `C1 1E` (0x11E = 286). Concatenation is real and must be handled: the `rgst` POST arrives as a
12-byte type-`0x20` chunk immediately followed by a 744-byte type-`0x02` chunk in one 756-byte datagram.

#### The type byte is a flags bitmap `[C]`

Read from the vendor library: its header codec guards with a **mask** (`(type & 0x24) == 0x24`) and its
connection engine branches on a single cookie-present bit. The bits, so far as they are evidenced:
`0x02` carries data · `0x20` carries an acknowledgement · `0x04` carries the extra 16-bit field · `0x80` is
connection control · `0x10` carries a cookie. So `0x24` means *ack **and** extra* — which is precisely why
only that combination has a trailing field — and `0x80`/`0x90`/`0xD0` are hello / hello-with-cookie /
cookie-offer. The table below therefore lists **observed combinations**, not an enumeration.

#### The responder role exists, and how it works `[C]`

The client library implements the *listening* side, which no capture of ours can show:

- A **bare hello** is copied into a 36-byte record and pushed onto a ring buffer (a listen/accept queue).
- A **cookie issuer** dequeues it, takes `now/1000` as a network-order timestamp, mixes `rand()`, and runs
  **SHA-256** — confirmed by both the 104-byte context seeded with 32 bytes of initial state and the presence
  of SHA-256's initial constants in the module, with SHA-1's absent.
- A **hello carrying a cookie** takes the other branch: SHA-256 is recomputed over
  `(client_timestamp ‖ 4 bytes ‖ a stored value)`, its **first 24 bytes** compared against the received
  cookie — the same 24-byte cookie region the captures show — and rejected if older than a staleness window.

So the responder position is a **supported protocol role**, not a gap. Two consequences for an
implementation: a cookie is opaque to the peer, so issuing 24 bytes of anything works *provided the issuer
does not later validate them*; and a faithful implementation must recompute rather than remember, because
that is what the vendor does.

**Correction to an earlier reading in this document.** The blocker was recorded as "the console will not open
a chunk connection to a responder". The console **never opens a chunk connection in any capture** — the
client always does, being the side with a request to send, and the console only answers. There was no such
behaviour to explain.

#### Chunk types

| Type | Direction | Body after the prefix (8 bytes for the word-count-3 shape this protocol sends) |
|---|---|---|
| `0x80` | client | `seq`(2) · `0B 01 01 00 01 00` · **connection tag**(4, random per connection) · `05 82` |
| `0xD0` | console | no seq · `00 00 00 02` · zero(4) · `81 20 00 96` · `84 D0 00 00 00 00` · **24-byte cookie** |
| `0x90` | client | exactly the `0x80` body, then the console's cookie region echoed verbatim |
| `0xA0` | console | `seq`(2) · `ack`(2) · `0B 01 01 00 01 00` · console tag(4) · `05 82` |
| `0x20` | either | `seq`(2) · `ack`(2) — a pure ack, typically prepended to a data chunk in the same datagram |
| `0x02` | either | `seq`(2) · **payload** — HTTP/1.1 text, or a binary control frame |
| `0x24` | either | `seq`(2) · `ack`(2) · 2 further bytes, **[X] unexplained** (values 0x21–0xD2, no checksum or window relation found) |
| `0xC0` | console | no seq · zero(4) · the console's connection tag — the teardown |

`05 82` = 1410 decimal, constant across every connection in the capture, in the position an MTU or receive
window would occupy — **[X] read as MTU, unconfirmed.** `0B 01 01 00 01 00` is likewise constant and reads
as a version/capability block.

**Sequence numbers are BE16 and per-chunk.** Each side picks a random initial value (client `0x2BF9`,
console `0xA65E` in the first round) and increments by one per chunk sent; the `ack` field carries the
peer's next expected value.

#### The library this framing belongs to `[C]`, 2026-09-03

It is not account-route-specific. A leftover assert path in the vendor control library names
`clientcomponents/rudp/.../rudp/lib/rudpnetworker.cpp`, and the surrounding files are
`lib/rudpheader.cpp` (this header codec), **`lib/rudpaggregator.cpp`** (which is chunk concatenation — the
behaviour we had inferred from a single 756-byte datagram splitting into a 12-byte ack and a 744-byte data
chunk) and `socket/gaikaisocketwin.cpp`. So this is the vendor's **reliable-UDP library**, multiplexing
logical streams by the port pair: Takion's A/V on one, the 9295 control stream on another. That is the
structural reason `rgst`/`init`/`ctrl` are byte-identical over 9303 and TCP 9295 — same stream, different
wrapper — and the reason the `0x24` acknowledgement and the `0xD0` cookie are **librudp control messages**
rather than anything the account route invented, which is why no account-route capture explains them.

Vendor file names are cited here as evidence about layout only; our own naming stays independent of them
(CLAUDE.md).

#### Who opens it, and why it matters `[C]`

The opener of the *association* is the side that goes on to open *connections* on it. Every capture we hold has
the client winning that race — including `cap91`–`cap94`, which are **same-LAN** pairings — and in each the
console answers the client's hello with a cookie. Ripcord's live runs land the other way round: the console
begins probing the instant our signaling `ACCEPT` reaches it, and our own `Init` sent moments earlier is
ignored because the console has not yet received our `OFFER` over the push channel. We therefore end up the
responder, and the console then never opens a chunk connection at all. **[X] What a responder is supposed to do
about that is not in any capture we hold**, since no recorded client has ever been in that position.

The client's first hello frequently goes unanswered and its second, about a second later, is the one the
console replies to — so a single attempt proves nothing.

#### The handshake, and one connection per request

    client  0x80  hello (tag, mtu)
    console 0xD0  cookie
    client  0x90  hello + cookie echoed
    console 0xA0  accept (its seq, acking ours)
    client  0x20  ack  +  0x02  data: "POST /sie/ps5/rp/sess/rgst HTTP/1.1 …"
    console 0x02  data: "HTTP/1.1 200 OK … " + the encrypted pairing record
    console 0xC0  close

**Each HTTP request opens its own chunk-layer connection** and is torn down after the response — the
requests carry `Connection: close`, and cap64 shows the full `0x80…0xC0` cycle three times over, once each
for `rgst`, `init` and `ctrl`, with a fresh connection tag and fresh initial sequence numbers each time.
So **registration needs exactly one cycle** and never touches the `0x24` ack chunks, whose trailing field is
the one part of this layer still unexplained.

#### What rides inside a `0x02` chunk

Either HTTP/1.1 request/response text, or — once `ctrl` has completed — the **same binary control frames
the PIN route carries over TCP 9295**: `[u32 payload_len][u16 type][u16 flags][payload]`. cap64 shows
`0x8004` (the encrypted login passcode), `0x0033` (session ready) and the `0x00FE` heartbeats inside these
chunks, byte-identical in shape to the TCP path. The control-frame codec is therefore transport-independent
and needs no second implementation.

#### Control-frame payloads are readable — the counter model `[V]` (2026-09-04)

A payload-carrying control frame is encrypted with the **§2.1 control-field cipher**, on a counter that is
**per-connection and shared across the whole direction**: the console's `/sess/ctrl` *response* spends
counter 0, and every payload-carrying frame after it takes the next one, in arrival order. Heartbeats carry
no payload and spend nothing. Each direction has its own counter starting at 0, which is why the client's
first frame is counter 5 — its five `/sess/ctrl` request fields spent 0–4 (and why the login passcode, the
only client frame previously implemented, is documented at counter 5).

Confirmed with a **known-plaintext oracle** rather than by inspection: the session-id frame decrypts at
counter 2 to a length-prefixed ASCII `"InvalidSessionId"`, and at no other counter in 0–31 to anything at
all. The model then predicted every subsequent frame in the same session, each landing on structured,
low-entropy plaintext:

| Type | Dir | Counter | Payload plaintext | Reading |
|---|---|---|---|---|
| `0x0005` | ← | 1 | `00` / `01` | **login result: `00` accepted, `01` rejected** — see below |
| `0x0033` | ← | 2 | `10` ‖ `"InvalidSessionId"` | session id, length-prefixed — literally the same string the `SESSION_REQUEST` carries |
| `0x0017` | ← | 3 | `02 00 04 02 24 00 55 12 34` | a counted list — see below |
| `0x0016` | ← | 4 | `01 FF` | one 16-bit value — see below |

This closes the "high entropy, no plaintext structure visible" note further down this file, which was
written before the cipher was available: the frames are not opaque, they were merely undecrypted. Anything
still marked `[X]` above is unknown *content*, not unknown encoding.

#### Frame shapes from the client's own receive dispatcher (2026-09-05)

`FUN_102089b0` is the client's control-frame handler — the receive counterpart of the sender, reached from
`FUN_10209b00` once a frame has been decrypted. It switches on the frame type, and each case states the
length it demands, which is a decode even where the meaning is not:

| Type | Length | Shape | Then |
|---|---|---|---|
| `0x0016` | exactly 2 | one 16-bit value | raises internal event `0x26` |
| `0x0017` | `count * 4 + 1` | a **count byte**, then `count` entries of two big-endian `uint16` | raises internal event `0x27` with the count |
| `0x0041` | exactly 8 | eight bytes, copied verbatim and not interpreted here | raises internal event `0x21` |

A frame whose length does not match is dropped silently, which is worth knowing: these are not
variable-length payloads to be parsed loosely.

`0x0017`'s live payload decodes exactly: `02 | 0004 0224 | 0055 1234` is count 2 followed by the pairs
(4, 548) and (85, 4660). **[X]** what the pairs mean — 548 is the size of the bandwidth probe's datagrams,
which may be nothing. `0x0016`'s byte order is **not** determinable from one sample: the surrounding protocol
is big-endian, but this field is loaded natively where its neighbours are assembled byte by byte, and `01 FF`
is unremarkable read either way.

So these three move from "unknown contents" to "known shape, unknown meaning" — which is the difference
between a field you can parse and one you can only stare at. Nothing depends on any of them.

#### `0x0005` is a status byte, and the old note about it was an artefact `[C]` (2026-09-05)

`00` means the passcode was accepted, `01` means it was wrong. Established by controlled experiment against
one console — one variable changed, two outcomes — and not by inference: the right passcode drew `00`, a
deliberately wrong one drew `01`, repeatedly. Values other than these two have not been seen.

This is worth recording as a **methodological correction**, not just a fact. The byte had been written down
as "opaque and different every time (cap51)", and it *is* different every time — as **ciphertext**. A stream
cipher at a fresh counter produces different bytes for the same plaintext by construction, so what was
recorded as a property of the frame was really a property of not being able to decrypt it. It then sat as
settled knowledge, discouraging exactly the check that would have overturned it. When the control channel
became readable the claim was never revisited, because nothing marked it as depending on that.

The practical consequence was a client that could not tell a wrong passcode from a right one: both looked
like "no session-ready before the timeout". And on the rendezvous route session-ready is **slow** — the
console answers the login in ~0.2 s and then **renegotiates the connection**, running a second candidate
exchange over the cloud before declaring the session ready at ~**5.0 s** (measured). Against a six-second
timeout that is a race, which is why a correct passcode was sometimes reported as rejected.

**Recorded as a correction:** this was briefly written up here as "the console accepts the passcode and then
abandons the session", inferred from runs that timed out inside that five-second window. It does not abandon
anything — it is slow, not indifferent. One earlier run did sit for twenty-five seconds with nothing, on a
console locked by idling rather than woken from standby; that remains `[X]` and unexplained rather than
tidied away.

#### The client's frame senders, from first-party static RE (2026-09-05)

From our own installed client's control DLL, analysed independently — vendor symbol names are
deliberately not used; everything is cited by RVA.

`FUN_1020a0a0(type, payload, length)` is **the** client→console control-frame sender: it encrypts `payload`
with the §2.1 field cipher (it is one of only seven callers of the field-encrypt `FUN_101f8cc0`) and emits
the 8-byte frame. It has 27 wrapper functions, one per frame type, which between them give the client's
whole vocabulary and each frame's payload length:

| Type | Len | Type | Len | Type | Len |
|---|---|---|---|---|---|
| `0x08` | 2 | `0x09` | 1 | `0x0d` | 16 |
| `0x0e` | 24 | `0x11` | 16 | `0x13` | var |
| `0x14` | 16 | `0x18` | var | `0x19` | 16 |
| `0x20` | 1 | `0x23` | var | `0x25` | 4 |
| `0x30`/`0x31` | 2 | `0x36` | 4 | `0x40` | 2 |
| `0x50` | 0 | `0x68` | 16 | `0x106` | 1 |
| `0x505` | 16 | `0x50a` | 12 | `0x902` | 12 |
| `0x910`/`0x911` | 4 | `0x8004` | 4 | `0x8064` | 8 |

The table is **not** taken on trust: five of its entries are independently confirmed by our own captures —
`0x50`/0 (rest mode), `0x8004`/4 (login passcode), `0x36`/4, `0x910`/4, `0x0e`/24 — all matching the lengths
observed on the wire. Two of those (`0x50`, `0x8004`) were already implemented from capture alone, so they
are a check on the RE rather than a product of it.

`0x0d` and `0x0e` are emitted as `0x50d`/`0x50f` instead when `FUN_101dec40` returns non-zero, which happens
for four values of an internal mode word. Both captured console families send the low forms, so `0x0d` is
what we want.

#### `0x000d`'s structure — solved; its field meanings are not (2026-09-05)

`FUN_1020c1c0` builds it. The payload is **four `uint32` in network byte order** — each of the four goes
through `FUN_101ee560`, a thunk onto `FUN_101fee50`, which is a bare `htonl` — read from a four-slot object
at `+4`, `+8`, `+0xc`, `+0x10` and emitted in **that** order. `FUN_1021dc10` refuses to produce the payload
at all unless **all four slots differ from `-1`**, which is their initial value: the frame is not sent until
every slot has been measured, which is consistent with its position right after the bandwidth probe.

What the four are is **`[X]`**, and is not guessed at here. What the binary does say:

- The slots are written by exactly four setters (`FUN_1021dce0`→`+4`, `FUN_1021dcc0`→`+8`,
  `FUN_1021dcd0`→`+0xc`, `FUN_1021dc70`→`+0x10`), each reached through one thunk, and **all four are fed
  from a single event dispatcher, `FUN_101f2710`**, from its cases 4, 6, 7 and 8.
- Case 6 sets `+4` alone, case 7 sets `+8` alone, and case 8 sets `+0xc` and `+0x10` **together** — so the
  four slots are three independent measurements, the last being a pair. The dispatcher records the same
  three as mask bits 1, 2 and 4 alongside.
- **`+0x10` is a time in milliseconds.** Case 4 writes it as `value / 1000` where case 8 writes the same
  quantity raw, which is a µs→ms conversion and nothing else.
- The client's probe module reports its results in exactly two log lines — `Senkusha Results - rtt:%d` and
  `Senkusha Results - mtu:%d, bw:%d, loss:%f` — so the probe yields **exactly four quantities: rtt, mtu, bw
  and loss**, which is the number of slots. That is as close to a naming as static analysis has got.

**Where that leaves it (2026-09-05).** `+0x10` is **rtt**, derived and solid: it is the slot one path writes
as `value / 1000` where another writes the same quantity raw, and rtt is the only time among the four. The
remaining three — `+4`, `+8`, `+0xc` — are **mtu, bw and loss in an order that is still `[X]`**. Two further
constraints, for whoever picks this up:

- `+8` is written **only** by dispatcher case 7, and is the one slot the combined case-4 event does not
  carry. The other three arrive together there.
- `+0xc` is written **together with** `+0x10` (rtt) by case 8, so whatever it is, it is measured by the same
  test as the round trip rather than by the mtu or bandwidth tests.
- `loss` is a float in the logs and a `uint32` on the wire, so it is scaled somehow — which is itself a hint
  as to which slot it is, since a scaled loss and a raw mtu look nothing alike in a capture.

So the honest state is three unknowns reduced to a three-way ordering among named quantities.

**And the ordering does not appear to matter `[V]` (2026-09-05).** Three live sessions, identical in every
other respect, varying only the payload:

| Slots sent | Video | Bytes |
|---|---|---|
| `[10000, 1454, 0, 0]` (our real measurements) | 594 frames | 1098 KiB |
| `[500, 500, 500, 500]` | 594 frames | 1061 KiB |
| `[0, 0, 0, 0]` | 595 frames | 1125 KiB |

Indistinguishable — the spread is scene variation on a looping attract screen. **The console requires the
frame and does not read it**, at least not in any way that reaches the stream: an implausible 500 kbps and a
flatly impossible zero both produced the same 60 fps at the same bitrate as the truth did.

Two things follow. First, the ordering is not blocking anything, and should stop being carried as though it
were. Second, **keep sending real values anyway** — "not observably read today" is not "never read", and a
console that begins using them, or an operator reading its own telemetry, is better served by the truth than
by whatever we happened to put in slot 0. The cost of being right here is nil.

Settling the ordering properly would need the event producers traced back through the pump (`FUN_101f0560`
is the dispatcher's only caller; events arrive on a queue as category `0x1a`, subtypes 4/6/7/8) or dynamic
analysis. That is now a curiosity rather than a dependency.

#### First live attempt at `0x000d` — negative, and what it does and does not tell us (2026-09-05)

Sent on the rendezvous route at client counter 5 with slots `[10000, 1454, 0, 0]`. The console **did not
answer**: no `0x0010`, no stream-ready, and the `SESSION_REPLY` was keyless as before.

That result is weak evidence and must not be over-read. The slot values were a guess, two of them were zero,
and one of those (`+0x10`, the millisecond time) is zero *legitimately* on a LAN, where the probe's round
trip rounds to under a millisecond. A console that validates its inputs would reject that, and a console
that ignores contents entirely would have answered anyway — so the run rules out neither the structure nor
the counter. What it does establish is that sending the frame is not sufficient on its own.

The client counter of 5 is, separately, **not** a guess and is now confirmed twice over: the login passcode
is a client control frame, and the same code path at counter 5 was accepted by a real console on the LAN
route in this session (`0x0004` prompt → our submit → `0x0005` = `00` → session id → stream). Each direction
continues from its own encrypted `/sess/ctrl` fields — the client's five spend 0–4, the console's single
response field spends 0 — which is why the client starts at 5 and the console at 1.

#### SOLVED — both "route-specific" refusals were one bug of ours, and neither was crypto (2026-09-05)

A payload chunk's sequence must **advance per payload**. Our sender reused the sequence the console hands
out in its accept, so the console took the first payload on each connection and discarded every one after it
as a duplicate — which its own acknowledgements had been reporting all along, asking for the next sequence
while we resent the previous. Request/response never noticed: `rgst`, `init` and `ctrl` each open a fresh
connection and send exactly one payload on it. The casualty was the persistent control channel that follows.

That single fault produced two symptoms that both looked route-specific and neither of which was:

- the login passcode the LAN route accepted and this one ignored, and
- a `SESSION_REPLY` with no ECDH material — because the console will not arm its stream service until the
  client reports its probe results (`0x000d`), and that report was among the dropped frames.

With the sequence advancing, the rendezvous route completes: probe report → `0x0034` stream-ready → the
console serves the session, and the stream loop runs.

**Method note, worth keeping.** Three theories fitted the evidence — wrong counter, wrong key, route-specific
key schedule — and all three were wrong. What settled it was picking a question they could not all survive:
*is our encryption wrong, or is the console not reading us at all?* `0x0910` answers it, being the one client
frame with a guaranteed reply (`0x8910`). The console echoed it on LAN and ignored it on the rendezvous
route, localising the fault to the transport rather than the cipher; the capture then showed every one of our
data chunks carrying the same sequence number.

#### Superseded — written before the above was found (2026-09-05)

Against one locked console, back to back: the **LAN** route accepted the passcode and streamed, while the
**account** route's identical submit drew no response at all — no login result, no session id, just the next
heartbeat. Counters 0, 1, 5 and 6 were each tried; all four were ignored in the same way, so this is not the
counter. Since the console's *own* frames on that same association decrypt correctly with our control key
(the session-id oracle above), the control key is not wrong either. Unexplained.

**Still `[X]`: the client→console `0x000d`.** 16 bytes, sent a second or two after the bandwidth probe
finishes, on both routes; the console answers `0x0010` (8 bytes). Its plaintext cannot be recovered from the
captures we hold — those are the vendor client's sessions, whose control key is its own — and our client
does not send one, so there is nothing of ours to decrypt. It is the last frame standing between the
rendezvous route and a served session: see `ROADMAP.md`.

## `RPCS` binary control frame format

Observed structure, present in every `RPCS`-prefixed packet in the capture:

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 bytes | Magic | ASCII `RPCS` (`52 50 43 53`) |
| 4 | 4 bytes | Length | Big-endian; observed values (32, 33, 36, 37, 40...) matched the actual byte count of the field below exactly in every packet checked |
| 8 | *Length* bytes | Payload | High entropy (consistent with encrypted content) in every frame observed; no plaintext structure visible |

`RPCS` frames appeared in both directions (client→console and console→client) after the HTTP
exchange completed, at irregular intervals - consistent with an ongoing encrypted control channel
used for session keepalive/parameter negotiation once the initial handshake finishes, rather than
a one-time exchange.

**Transport-agnostic, confirmed**: the identical `RPCS` magic-plus-length-plus-payload framing was
later observed carried directly over a plain TCP byte stream (no RUDP wrapper at all) in a capture
where the console was reached via local discovery rather than the cloud/RUDP path - see
`ps5-local-discovery.md`. The `RPCS` frame format itself is independent of which underlying
transport carries it.

**Redundant transmission observed**: in one instance, the identical `RPCS` frame (same length,
same payload bytes) was sent three times in quick succession, each wrapped in an RUDP header
carrying what looks like a small fragment/attempt counter (1, 2, 3) rather than being spread across
three different packets as genuinely different fragments. Read as deliberate forward redundancy
(mitigating loss without waiting for a retransmit round-trip) rather than message fragmentation,
since all three payloads were byte-for-byte identical - but this interpretation should be treated
as tentative until corroborated by a second capture.

## What's still needed

- What actually identifies a session/peer at this layer, now that the field once assumed to be a
  connection ID is known to be a fixed constant (see the revised header table above) - the
  remaining undecoded 4-byte field at offset 6 is the leading candidate.
- A PIN-based/local "Link Device" pairing capture - see `ps5-session-establishment.md` for why
  this, not general "first-time registration," is the remaining pairing gap.
- A capture of the PS4 equivalent flow, once available, to identify what differs from PS5.
- Whether the plain-TCP control-channel transport (`ps5-local-discovery.md`) is used under any
  conditions beyond "console has no internet and was found via local SRCH broadcast."
