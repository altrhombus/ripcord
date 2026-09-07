# PS5 Remote Play — end-to-end network architecture (overview)

Status: draft, from seven capture sessions now (see provenance log): a full reconnect session, a
short app-startup-only session (account info + NAT status check, no console connection), a
brand-new client device's first-ever connection to the console (account-SSO "online" pairing, no
PIN), a locally-discovered connection to a console with its internet access blocked, a
WAN/relay-addressed connection from a different network entirely, and two TLS-decrypted captures via
a local debugging proxy that finally make the PSN cloud (tier 1) readable. This is the "big picture"
document that frames the more detailed per-flow specs (`ps5-session-transport.md`,
`ps5-session-establishment.md`, `ps5-av-stream.md`, `ps5-controller-input-packet.md`,
`ps5-local-discovery.md`, `ps5-wan-relay.md`, `ps5-cloud-session-api.md`). It exists because a
full-session capture turned out to contain considerably more than the console-local handshake the
earlier drafts described: there are (at least) three distinct network tiers involved, and both the
WAN tier and the cloud tier turned out to be richer than first assumed - see the corrections below.

Source: own packet capture of network traffic on the author's own LAN, covering a full session
lifecycle — account sign-in, console connect, a short period of live streaming with a few
controller inputs, then disconnect. Observed at IP/port level plus whatever each protocol layer
exposes in the clear. Capture and this document written 2026-07-10. No source code from any
existing Remote Play client project was consulted — see `docs/protocol-research-log.md`.

**Redaction note**: no IP addresses, hostnames tied to a specific device, account identifiers, or
captured cryptographic values are reproduced here. Only structure, port numbers, well-known public
hostnames, protocol layers, and field encodings are documented — which is what a from-scratch
implementation actually needs.

## The three tiers

A single "connect to my PS5" action turned out to involve three independent network relationships,
established in this order:

1. **PSN cloud services (TCP/TLS, WAN)** — account authentication, entitlement, remote-play
   configuration, and (the strong inference) registration-key management. All HTTPS; opaque to
   capture by design. This is where sign-in happens and where the crashes in this session
   occurred.
2. **A PSN signaling/relay channel (UDP, WAN)** — a *persistent* ICE + DTLS session to a
   PSN-operated server, established at sign-in and held open for the entire session regardless of
   whether a console is connected. Uses a WebRTC-style stack (see below). This is the rendezvous /
   NAT-traversal / WAN-relay tier.
3. **The direct console link (UDP, LAN)** — the actual Remote Play session: a custom
   reliable-UDP control channel plus a separate high-volume A/V+input stream, both directly to the
   console's LAN address. This is the only tier that carried real media in this capture, and it is
   what the transport/establishment/stream/input specs describe in detail.

The important architectural takeaway: **for a LAN session the media does not traverse the PSN
relay** — tier 2 is used for presence/rendezvous while tier 3 (the directly-negotiated LAN path)
carries the session. **Correction, from a later WAN capture** (see `ps5-wan-relay.md`): when a LAN
path genuinely isn't available, the same tier-2 relay address *does* carry real media - the client
simply re-addresses its ordinary tier-3 protocol (same ports, same framing) to the relay instead of
the console. So tier 2 isn't purely signaling; it's signaling-only when LAN succeeds, and a full
transparent relay when it doesn't. A LAN-only implementation (Phase 1's scope) must reproduce
tier 3, and needs *some* mechanism from tier 1/2 to learn the console is online and reachable — but
does not need to implement the relay-addressed case.

## Tier 1 — PSN cloud (TCP/TLS)

**This tier is now documented in full in `ps5-cloud-session-api.md`**, from two captures taken
through a local TLS-terminating debugging proxy on the author's own machine/account - the HTTPS
bodies that were opaque when this document was first written are readable there. The summary below
is kept for the big-picture map; the API details, ordering, and the crucial
`sessionManager/v1/remotePlaySessions` create/read/delete lifecycle live in that document.

Numerous short-lived HTTPS connections to PSN/Sony service hostnames were observed throughout
sign-in. The hostnames (from TLS SNI) map to recognizable service roles:

- Account/identity and OAuth-style token services (`*.sonyentertainmentnetwork.com`,
  `accounts.api.playstation.com`).
- A remote-play configuration/asset host (`remoteplay.dl.playstation.net`) — also the source of
  the public router/port documentation cited in the research log.
- An account-info/identity host (hostname contains `.km.`, path `users/me/info`) contacted during
  the sign-in flow. **Correction (2026-07-12, TLS-decrypted capture)**: earlier drafts guessed this
  `.km.` host was a "key-management" service issuing the console **registration key**, from its name
  and timing. Decrypted, it plainly returns account identity/region/age context, **not** a
  registration key, and no registration key is fetched from the cloud on a reconnect at all. The
  guess is withdrawn; see `ps5-cloud-session-api.md` for the real call. What remains true: this host
  is contacted on every sign-in (including app-startup-only, no console), so sign-in is a genuine
  cloud/account operation - just an identity one, not key issuance.
- Push/communication and community/presence hosts (`*.np.communication.playstation.net`,
  `*.np.community.playstation.net`).
- **The remote-play session manager** (`web.np.playstation.com/api/sessionManager/v1/remotePlaySessions`)
  — decrypted, this is the cloud coordination point for a session: the client `POST`s to create/join
  a session (adding only itself, with a push-context id; it does **not** name the console), reads it
  back, and `DELETE`s its own membership to disconnect. The console is notified out of band via a
  push (WebSocket) channel rather than by being named in the request or appearing in the readback.
  Full detail in `ps5-cloud-session-api.md`. This is the concrete mechanism behind "tier 1/2 tells
  the client the console is reachable" that this document previously could only infer.

Two distinct sign-in waves were observed in this session (a second full auth sequence part-way
through), consistent with the client process having been restarted mid-session. Practical
implication: **registration and authentication are PSN-cloud operations, not console-local.** The
on-console PIN step (not captured here — see the first-time-pairing to-do) presumably ties a
cloud-issued key to the specific console; documenting that requires a clean first-time-pairing
capture.

## Tier 2 — PSN signaling/relay (UDP: ICE + DTLS)

A single long-lived UDP flow to one PSN-operated server (a public IP) ran for the **entire**
capture, beginning at sign-in — before any console connection — and persisting throughout. It is a
textbook WebRTC transport:

- **ICE connectivity checks (RFC 8445)**: continuous STUN Binding request/response pairs carrying
  `USERNAME`, `MESSAGE-INTEGRITY`, `FINGERPRINT`, `PRIORITY`, `ICE-CONTROLLING`/`ICE-CONTROLLED`,
  `USE-CANDIDATE`, and `XOR-MAPPED-ADDRESS`. The presence of the `GOOG-NETWORK-INFO` attribute
  identifies the implementation as Google's libwebrtc.
- **Candidate gathering** via a public STUN server (Google's `stun.l.google.com:19302` was
  contacted) plus the local gateway — standard host/server-reflexive candidate discovery.
- **DTLS** application-data records (content-type 23) exchanged over the same 5-tuple in periodic
  bursts (~every 30 s) — a keepalive/heartbeat on the established secure channel rather than a
  media flow (the whole channel moved only tens of kB over the session).

Read as: the client keeps a persistent, DTLS-secured WebRTC data channel open to PSN for presence
and rendezvous. This is the mechanism by which a client and a console can find each other and hole-
punch across the internet for the WAN remote-play case. For Phase 1 (LAN-only) we do not need to
implement this tier to carry media, but understanding it explains (a) how the client knows the
console is awake/reachable without a LAN broadcast, and (b) what the eventual WAN path would build
on. libwebrtc is the reference stack the official client uses here.

**How candidates are exchanged (now confirmed)**: the STUN tier *gathers* the client's reflexive/
relay candidate, but the client and console *exchange* their candidate lists over the cloud session
manager's `sessionMessage` channel (an `OFFER`/`ACCEPT`/`RESULT` handshake carrying a `connRequest`
with `LOCAL` + `STATIC` candidates), readable in the TLS-decrypted capture - see
`ps5-cloud-session-api.md`. So tier 2 (STUN/DTLS) and tier 1 (session manager) work together: gather
via STUN, exchange via HTTPS signaling. This resolves the earlier "candidate exchange must happen
somewhere we can't see" open question.

**Confirmed as a real media relay, not just signaling**: a capture of an actual successful
WAN connection (different network from the console's LAN, console already awake) showed **134 MB
across ~131,000 packets** flowing through this same relay address, using the identical ports and
framing as a direct LAN session (`ps5-session-transport.md`, `ps5-av-stream.md`) - the relay
forwards the client's own protocol transparently at the IP level rather than re-encoding it. Full
details, including multiple failed connection attempts before success and WAN-specific stream
behavior, are in `ps5-wan-relay.md`.

**WAN dual-path, confirmed directly**: in the full session capture this was inferred from a
public-IP flow observed "in parallel" with the LAN connection. A second, shorter capture made this
unambiguous: at connect time, the client sent a single UDP probe packet to the *same* public relay
address, on the *exact same source ports* subsequently used for the direct LAN session with the
console, before falling back to the direct LAN path. Reads as: the client always prepares/attempts
both a WAN-relayed path and a direct LAN path using the same local ports for both, and simply uses
whichever succeeds/responds first — consistent with a classic ICE-style "happy eyeballs" race
between candidate paths, with LAN winning whenever it's viable. Still out of scope to implement for
Phase 1 (LAN-only), but the mechanism is now well understood.

**Separate NAT-type detection (classic STUN)**: independently of the ICE/DTLS channel above, the
app was observed running a short, separate **classic STUN** (RFC 3489-style Binding Request/
Response, not the ICE-flavored STUN used in tier 2) exchange against a third-party-hosted STUN
service on startup — even when no console was ever contacted, triggered simply by opening the
app's account/status panel. This is a distinct, simpler mechanism from the persistent ICE channel:
a one-shot NAT-type probe (open/moderate/strict, in the terminology STUN-based NAT detection
tools use) used to drive the "NAT status" indicator the app's UI shows next to account info. Worth
replicating for Ripcord's own diagnostics/status UI, but not required for LAN-only Phase 1
streaming.

## Tier 3 — Direct console link (LAN)

Once negotiated, the session ran directly to the console's LAN address over two UDP ports:

- **Control** (console port 9303 in most captures): the reliable-UDP framing + HTTP-over-RUDP
  handshake (`/sess/rgst`, `/sess/init`, `/sess/ctrl`) + ongoing `RPCS` binary control frames. See
  `ps5-session-transport.md` and `ps5-session-establishment.md`.
- **Stream** (console port 9297 in most captures): a single multiplexed reliable-UDP flow carrying
  video, audio, controller input, and congestion/reliability feedback, distinguished by a channel
  byte in a common per-packet header. See `ps5-av-stream.md`.

Both port numbers are drawn from a pool (9295-9297 for the stream role, seen at 9296 in one
capture; the control role has so far only been seen at 9303 over UDP) rather than being hard fixed
- see `ps5-local-discovery.md`.

**A second, TCP-based control-channel transport exists**, and it is more common than first thought.
It was first seen on a connection to a console with no internet access; a later home-LAN capture
(console fully online, normal cloud sign-in) *also* used plain TCP on a pool port for control rather
than RUDP-wrapped UDP/9303. So the TCP control path is **not** specific to an offline console — it
appears to be the normal transport for a directly-reachable LAN console, with the RUDP/UDP-9303 path
associated with the cloud/relay-mediated cases. Both carry the identical `RPCS` frame format. Full
details in `ps5-local-discovery.md`; it is still not pinned down exactly what selects the transport,
but "LAN-direct → TCP, relay/WAN → RUDP-UDP" is the current best reading.

No console-local registration was observed in the original full-session capture — the client
already knew the console's LAN address and already held a registration key, both consistent with
tier 1/2 having supplied them. The console *was* independently visible on the LAN via mDNS (see
Discovery below). A later capture *did* observe genuine LAN discovery broadcast traffic (UDP 9302
`SRCH`) when the console had no cloud path available - see Discovery below.

### Session timeline (this capture)

Times relative to capture start (~264 s total):

| ~t (s) | Event |
|---|---|
| 0 | PSN signaling/relay channel (tier 2) comes up at sign-in; runs to end |
| 4–55 | First PSN sign-in wave (tier 1); remote-play config fetched (~55) |
| ~110 | Client restart / second sign-in wave begins |
| 149–200 | Second auth wave incl. key-management host; push/presence hosts |
| 222 | Direct console control channel (9303) opens: bootstrap + handshake |
| 225–228 | Three HTTP-over-RUDP exchanges (rgst/init/ctrl) complete |
| 233 onward | Steady 5 s `RPCS` control keepalive |
| 236 | A/V stream (9297) begins |
| 236–260 | ~24 s of live streaming (video + audio + a few controller inputs) |
| 260–263 | Teardown on the control channel |

The direct-console tier occupied only the final ~41 s; everything before it was cloud auth and
signaling. The crashes reported during this session all fell in the pre-connect (tier 1/2) window,
which is why the direct-console flow reads as a single clean attempt.

## Discovery (mDNS / LAN presence, and UDP 9302 SRCH)

The console announced itself on the LAN over mDNS / DNS-SD (UDP 5353) independently of Remote Play,
in the original full-session capture:

- Hostname of the form `PS5-<6 hex digits>.local` (the hex suffix is a device-specific identifier
  and is **not** reproduced here).
- A `_spotify-connect._tcp.local` service advertisement (Spotify Connect), plus standard
  `_services._dns-sd._udp.local` enumeration and reverse-DNS (`in-addr.arpa`) records mapping the
  hostname to its LAN IP.
- **No** Remote-Play-specific mDNS service type was advertised, and no UDP 9302 broadcast was seen
  in that particular session (the client already had the console's address from elsewhere).

**UDP 9302 SRCH discovery is now fully captured and documented**, in a session where the console
had no cloud path available and the client fell back to it: a lightweight, non-HTTP broadcast
request/response (`SRCH * HTTP/1.1` / `HTTP/1.1 200 Ok`) returning the console's hardware
identifier, host type, user-assigned name, a `host-request-port`, protocol version, and firmware
version. Full field-by-field detail in `ps5-local-discovery.md`. Note: the response's
`host-request-port` was `997` in the capture, not the `987` cited in the public router-
configuration documentation referenced in the research log for "remote wakeup" - an unresolved
discrepancy, flagged there.

There is also a **third, cloud** discovery mechanism, seen decrypted in the sign-in capture:
`GET web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/clients?platform=PS5` returns
the account's consoles with their names, `duid`s, `enabledFeatures`, and per-console remote-wake
capability (`wakeupEnabledPowerModes`). See `ps5-cloud-session-api.md`. This is what populates the
official app's console picker when signed in, and is the only one of the three that works off-LAN
and reports wake capability.

Implication for our discovery implementation: three complementary mechanisms - mDNS (lightweight LAN
presence → address), UDP 9302 SRCH (Remote-Play-specific LAN probe returning firmware version etc.),
and the cloud `clients` list (account-wide, off-LAN, reports wake capability). A full client wants
all three; Phase 1's LAN target can start with mDNS + SRCH, adding the cloud list alongside the
sign-in work.

## What this changes for the plan

- **Discovery** should implement both mDNS and UDP 9302 SRCH - the latter is now fully specified
  (`ps5-local-discovery.md`) and is the Remote-Play-specific mechanism.
- **Registration for account-SSO ("online") pairing is a pure PSN-cloud flow (tier 1)**, confirmed
  across a reconnect and a brand-new device's first connection — the Phase 1 "pairing" work for
  this path is an account/OAuth + key-management client, not an on-LAN handshake at all; the
  console-local exchange is identical either way (see `ps5-session-establishment.md`). PS5 also
  exposes a **separate, first-party PIN-based "Link Device" pairing mode** (Settings → System →
  Remote Play → Link Device) that is a genuinely different feature from account-SSO pairing and may
  not go through the cloud key-management step the same way — this is the real remaining gap, not
  "first-time pairing" in general.
- **A LAN-only implementation needs to support two control-channel transports**, not one: the
  RUDP/UDP-9303 path (cloud-mediated connections) and a plain-TCP path on a pool port
  (locally-discovered connections to a console without cloud access) - see
  `ps5-local-discovery.md`. Both carry the identical `RPCS` frame format once established, so the
  implementation cost is mainly in the outer framing/handshake layer, not the ongoing protocol.
  `/sess/rgst` was skipped entirely on the TCP path in the one capture that used it - worth
  designing for as optional, not assumed-required.
- **LAN session** = tier 3 only; no PSN media relay needed for Phase 1. Defer tiers 1/2's WAN
  relay path as originally planned - now confirmed to be a real, transparent media relay (not just
  signaling) when actually needed, using the client's own unmodified protocol. Full findings in
  `ps5-wan-relay.md`, not something Phase 1 needs to act on.
- The RUDP header's previously-assumed "connection ID" field is corrected in
  `ps5-session-transport.md` - it's a fixed constant, not a per-session identifier. Relevant to
  anyone implementing the framing layer: don't rely on that field to distinguish sessions/peers.
- **Traffic-pattern analysis cannot recover the controller-input byte mapping** - confirmed with a
  negative result from a capture with ~30 documented button presses (`ps5-wan-relay.md`). Payload
  decryption (which depends on fully resolving the ECDH exchange in
  `ps5-session-establishment.md`) is the only path forward there, not more capture sessions of this
  particular kind.
- A **NAT-type status indicator** (classic STUN, one-shot, independent of the ICE channel) is a
  small, easy piece of UX worth replicating for Ripcord's own diagnostics.

## Still needed

- **PIN-based/local "Link Device" pairing capture** — the one pairing mode not yet seen at all,
  across six captures now. Possibly relevant: whether this flow works/behaves differently with the
  console offline from the internet, which would indicate a genuinely console-local trust step
  distinct from the cloud-mediated account-SSO path documented here.
- What determines choice of control-channel transport (RUDP/UDP-9303 vs. plain TCP) - console
  internet access, discovery method, or something else - see `ps5-local-discovery.md`.
- What actually identifies a session/peer at the RUDP layer, now that the field previously assumed
  to be a connection ID is known to be a fixed constant - see `ps5-session-transport.md`.
- Resolve the `host-request-port` 997-vs-987 discrepancy against public documentation, and
  determine what protocol runs on that port (presumably remote wake, not exercised in any capture
  so far since the console was always already awake).
- ~~Decode the `RP-Data`/`RP-Tag` payload and the ECDH curve/key-derivation details - the blocker for
  ever resolving the controller-input byte mapping via decryption instead of traffic analysis.~~
  **Superseded.** The ECDH curve was settled as P-521 on 2026-07-13, and the controller-input mapping was
  resolved outright by decrypting `v1` traffic with our own session keys — see
  `ps5-controller-input-packet.md` and `ps5-remoteplay-v1-spec.md` §6.3. `RP-Data`/`RP-Tag` belong to the
  newer client's (`v2`) handshake and remain undecoded **[X]**, but nothing depends on them.
