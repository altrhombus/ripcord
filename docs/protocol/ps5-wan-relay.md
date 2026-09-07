# PS5 Remote Play — WAN/relay session behavior

Status: draft, from one capture session (see provenance log): a connection attempted from a
different network entirely (a cellular-hotspot-style client network, distinct from the console's
home LAN) to a console that was already awake. Multiple connection attempts failed before one
succeeded; once connected, a documented sequence of ~30 distinct controller inputs was performed,
ending with closing the client app and choosing to put the console to sleep.

Source: own packet capture of network traffic on a network distinct from the console's LAN.
Capture and this document written 2026-07-10. No source code from any existing Remote Play client
project was consulted - see `docs/protocol-research-log.md`.

**Redaction note**: this capture necessarily involves real public IP addresses (both the client's
and the relay's) - none are reproduced here, only their roles. The registration key, public keys,
HMACs, nonces, and any other cryptographic material are never reproduced verbatim, consistent with
every other document in this set.

Out of scope per the plan (LAN-only for Phase 1) - this document exists to record what was
learned, not to propose implementing WAN support now.

## The relay carries real media, transparently

The full session's UDP traffic - **134 MB across ~131,000 packets** - went to a single WAN address:
the same address seen acting as a signaling/rendezvous peer in earlier LAN captures (see the "WAN
dual-path" findings in `ps5-network-architecture.md`). This settles a question that document left
open: **for a WAN connection, this address is a full media relay, not just a signaling/rendezvous
point.**

Critically, the traffic to this relay uses **the exact same ports as a direct LAN session** - 9303
for control, 9297 for the A/V+input stream - and the exact same `RPCS` binary control-frame format
and multiplexed stream framing documented in `ps5-session-transport.md` and `ps5-av-stream.md`.
Nothing about the client's own protocol implementation differs; only the destination IP changes,
from the console's LAN address to the relay's WAN address. **The relay is a transparent UDP
forwarder at the IP level**, not a different transport or a re-encoding hop. A client implementation
that speaks the direct-LAN protocol correctly could, in principle, speak the WAN-relayed protocol
by only changing the destination address.

One addressing detail: in the `/sess/init` and `/sess/ctrl` HTTP-over-RUDP requests (see
`ps5-session-establishment.md`), the `Host:` header this session carried the **relay's** address,
not the console's - consistent with the client never needing or having the console's real address
at all for a relay-mediated session.

The registration key presented was, again, the identical value seen in every other capture of this
console+account pair - confirming once more that it is scoped to the account+console relationship,
not to network path or transport.

## Multiple connection attempts before success

The successful relay-addressed session begins cleanly at a single point in the capture and runs
uninterrupted afterward - no retries are visible *within* the relay/control-channel traffic itself.
The reported failed attempts therefore happened earlier, at the cloud/signaling stage, before ever
reaching the relay.

Evidence for this: the TLS SNI sequence (cloud PSN hosts - remote-play config, auth, key-management,
community/presence, push - see `ps5-network-architecture.md` for what each role means) shows this
same cluster of hosts hit together **twice** during the capture, well before the successful
connection: once roughly a quarter of the way through the capture, and once immediately preceding
the successful relay connection. Only the second cluster was followed by relay traffic. Read as (not
directly provable from encrypted traffic alone): the first full cluster corresponds to a failed
connection attempt that got as far as cloud auth/session setup but never established a working
relay path; the second succeeded. This is consistent with, though doesn't precisely count, "failed
a couple of times, succeeded on the last."

No local SRCH-style discovery (see `ps5-local-discovery.md`) ever reached the console in this
capture, for the obvious reason that the console isn't on the client's local network - but the
client *did* still send a small number of local SRCH-style probes addressed to a specific device on
its own local network (not a broadcast, and not the console) during the same time windows as the
cloud auth clusters above. Read as: the client always races a local-discovery attempt alongside the
cloud/WAN path regardless of whether local discovery has any realistic chance of finding the
target - consistent with the general "try every path in parallel" pattern already observed in the
WAN dual-path finding.

## A correction: the RUDP "connection ID" is not per-session

`ps5-session-transport.md` previously described a 4-byte value (`24 4F 24 4F` in the original
capture) appearing after the RUDP bootstrap phase as "almost certainly a randomly generated
per-session connection ID, comparable in purpose to a QUIC connection ID."

**This capture disproves that.** The identical 4-byte value was found at the identical header
offset in this WAN session - a completely independent session, different client device, different
network, established at a different time. The same value was then cross-checked directly against
two more independently-captured sessions (the original LAN capture and the online-first-pairing
capture) and found present in all of them. A randomly generated per-session value would not
plausibly repeat across four independent sessions.

**Corrected reading**: this 4-byte value is a **fixed protocol/session-type magic constant**, not a
connection identifier. Byte-level re-examination in this pass also clarified the field immediately
before it: a 1-byte value that, in every packet checked, exactly equals the UDP payload length
(payload length = wire length minus the 42-byte Ethernet/IPv4/UDP prefix) - i.e. a explicit 1-byte
length field, not an unexplained flag byte. Revised header shape for the "main sub-phase" (see
`ps5-session-transport.md` for the full context):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 byte | Flags (constant `0xC0` in every packet checked) |
| 1 | 1 byte | Payload length (of the *rest* of this UDP payload) |
| 2 | 4 bytes | Fixed magic constant (not a connection ID - see above) |
| 6 | 4 bytes | Varies per packet - sequence/ack-like field, still not fully decoded |
| 10+ | rest | HTTP request/response, or `RPCS` frame, or a short ACK-only frame |

What actually identifies a session/peer at the RUDP layer, if not this field, is now an open
question rather than a solved one - flagged below.

## Up-direction channels scale up significantly on WAN

The multiplexed stream's up-direction (client → relay) channel mix, compared against
`ps5-av-stream.md`'s LAN-derived channel map, is far richer here:

- The controller-input channel (`0x0e`) ran at a **constant ~3.7 packets/second** for the entire
  ~200-second active session, regardless of whether the documented button-press sequence was being
  performed or the session was idle. Same fixed 66-byte payload size in every packet, matching
  the LAN captures exactly. Its second header byte, initially misread (in an earlier draft of this
  document) as an 11-value rotating sub-flag, is actually a **very slowly incrementing counter** -
  it went from `0x00` at session start to `0x0a` roughly 180 seconds later, i.e. about one
  increment every 16-18 seconds. See the correction in `ps5-controller-input-packet.md` - this
  reads as a coarse, session-relative counter (minutes-elapsed or a long keepalive interval), not a
  per-packet rotating tag, and was never observed to wrap back to `0x00`.
- A feedback/report channel (`0x00`, sub-flags `0x2d`) ran at a much higher, also-constant rate -
  roughly 9.5 packets/second, and was the single largest up-direction class by raw packet count.
- An entirely new high-volume channel family (`0x06`, spanning multiple sub-flag values `0x00`-
  `0x0a`) ran at a **combined ~54 packets/second** - by far the largest up-direction channel family
  observed in any capture so far, LAN or WAN. Not seen at meaningful volume in any LAN capture (the
  original LAN spec noted channel `0x06` only as "rare control, few packets"). Whether its own
  sub-flag byte behaves like the slow counter above or differently was not checked in this pass -
  flagged below.

**Read as**: the client sends substantially more network-quality/congestion telemetry when
connected over a real WAN path with real RTT and loss characteristics than it does on a
near-zero-latency LAN - unsurprising for a system that needs to adapt bitrate/resolution to actual
conditions, but not something the LAN-only captures could have shown. This is directly relevant to
Phase 4 (adaptive bitrate): the `0x06` channel family is the leading candidate for where the bulk of
that telemetry lives, and is worth prioritizing over the smaller `0x00`/`0x2d` feedback channel
already flagged in `ps5-av-stream.md`.

## Button-press correlation: a negative result

This capture included a documented, deliberate sequence of roughly 30 distinct controller actions
(D-pad, face buttons, all shoulder/trigger buttons, both analog sticks in all four directions,
system buttons, mic toggle, touchpad press) performed in sequence after connecting - exactly the
kind of annotated data the earlier controller-input spec said was needed to confirm button-to-byte
correlation.

**Result: no correlation is recoverable from traffic analysis alone.** The input channel's packet
rate, packet size, and inter-packet timing are all constant regardless of whether any of these ~30
actions was actively occurring. There is no burst, no size change, and no discernible pattern
distinguishing "user is pressing buttons" from "user is idle" anywhere in the plaintext framing.
This rules out the specific follow-up the earlier document asked for (rate/timing correlation) and
narrows down what would actually work:

- The protocol reads as a **fixed-rate full-state stream** (send current complete controller state
  on a timer, regardless of whether anything changed), not an event-driven "send a packet when a
  button changes" design. This matches how several other real-time-input protocols are built
  (deliberately redundant against packet loss, since a dropped packet is just superseded by the
  next tick rather than needing retransmission), but it does mean **the only way to confirm the
  controller-input byte layout is to recover the session key and decrypt** - no amount of
  additional traffic-pattern capturing will substitute for that.
- This is a genuine, useful negative result for scoping future work: don't spend more capture
  sessions chasing timing/size correlation on this channel; the effort belongs on the key-recovery/
  decryption side instead (which itself depends on fully nailing down the ECDH exchange in
  `ps5-session-establishment.md`).

## Session end: no separate "sleep" command found on the wire

The session ended with the client app being closed and the user choosing an option to put the
console to sleep. No distinct HTTPS/cloud API call was observed near the end of the capture that
obviously corresponds to a "sleep" command, and no clearly distinct `RPCS` control-frame type stood
out from the ordinary teardown sequence.

What *was* observed, in order:
1. A large burst of full-MTU video fragments (matching the keyframe-sized packets described in
   `ps5-av-stream.md`) right at the point the controller-input channel stops sending - consistent
   with the console rendering a system-level confirmation dialog (a full-screen UI change forces a
   keyframe) rather than continuing normal gameplay video.
2. The input channel (`0x0e`) stops entirely at that same point - consistent with the Remote Play
   app itself no longer being the focused/active application once its own "close app" UI takes
   over.
3. A short run of small control-channel (`RPCS`) frames follows, differently sized than the
   preceding steady-state keepalive pattern, then all relay traffic stops.

**Read as**: "put the console to sleep" is not a separate protocol action visible at this layer -
it most plausibly happens either (a) as part of the ordinary session-teardown `RPCS` exchange, with
the "sleep vs. just disconnect" choice encoded somewhere in the (encrypted) teardown payload rather
than as a distinguishable frame type, or (b) as a follow-up cloud API call.

**Update (2026-07-12): option (b) is ruled out.** A later capture taken through a TLS-terminating
proxy (see `ps5-cloud-session-api.md`) recorded a disconnect that explicitly chose "put the console
into rest mode", with the cloud side fully decrypted. The only disconnect-related cloud call was a
bare `DELETE .../remotePlaySessions/<id>/members/me` with **no body and no query parameters** -
there is no cloud "sleep"/"rest" request. So the rest-mode instruction is delivered over the direct
control channel (option (a), the `RPCS` teardown) or is inferred by the console when its last
remote-play member leaves. Still outside Phase 1's scope to chase to the byte level, but the cloud
possibility is now closed off.

## Update (2026-09-05) — how the relay address would reach a client

This document predates the cloud signalling being decoded, which is why it does not say how the client
learned the relay's address. It is now the obvious question, and it is **`[X]`**.

What is known since: a rendezvous session's candidates come from the console's `OFFER`, and across twelve
signalling captures the only candidate types that ever appear are `LOCAL`, `STATIC` and `STUN` — **no relay
type**. That and this document reconcile if a relay is simply handed over *as* the console's ordinary
candidate, which this document's own central finding supports: the relay is a transparent forwarder at the
IP level, and "only the destination IP changes". If that is right, a client that connects to whatever the
`OFFER` names would use a relay without knowing it, and Ripcord may already do so.

**Untested either way.** Our own off-network session (2026-09-05) was *direct*, not relayed: our STUN
reflexive address measured on the console's LAN was the same address the console advertises as its `STATIC`
candidate, i.e. the home network's public IP, so the client hole-punched straight to it. Nothing has yet
exercised a path where a direct one was impossible.

## What's still needed

- Determine what actually identifies a session at the RUDP layer, now that the previously-assumed
  "connection ID" field is known to be a fixed constant rather than per-session (see correction
  above) - likely candidates are the still-undecoded sequence/ack-like field, or the session is
  identified purely by the UDP 5-tuple with no application-layer session token at this layer at
  all.
- Decode the `0x06` channel family's payload structure (still encrypted, but its sub-flag/framing
  pattern could be characterized further, including whether its sub-flag byte behaves like the
  input channel's slow counter or differently) given how central it appears to be to WAN quality
  telemetry.
- A capture with TLS decryption (e.g. via a trusted local proxy/CA, if the client platform
  supports it) would be the only way to actually resolve the "sleep command" question and the
  broader cloud-API surface - out of scope for this capture-only methodology but worth flagging as
  a distinct, heavier-weight technique if that gap ever needs closing.
