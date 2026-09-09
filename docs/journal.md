# Ripcord engineering journal

**What happened, and when.** The dated working record of Ripcord's development, kept because a project
whose central claim is independent derivation is worth more with its trail intact than tidied away.
Newest first.

This is a *record*, not a reference. Where it states a protocol fact, [`docs/protocol/`](protocol/) is
authoritative and this file may be out of date. For what is still open see [`ROADMAP.md`](../ROADMAP.md);
for the clean-room provenance record — what external material was consulted, and what each item informed —
see [`protocol-research-log.md`](protocol-research-log.md), which is a different document with a
different purpose.

## Session log

> **↻ RESUME HERE (2026-09-05 — VIDEO OVER THE INTERNET. The account route works off-network.)**
>
> Client on a phone hotspot, console on a different network, both behind NAT, no port forwarding, direct
> peer to peer:
>
> _Public IP addresses throughout this file are redacted to RFC 5737 documentation ranges
> (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24). Private RFC 1918 addresses are as captured._
>
> ```
> our reflexive address is 198.51.100.55:3513   (control leg, local port 65004)
> reaching the console at 203.0.113.180:9303      (its STATIC candidate)
> media reflexive address is 198.51.100.55:3518 (A/V leg, local port 61613)
> media prelude established with 203.0.113.180:9297
> VIDEO CONFIRMED: 591 frames (60 fps steady), 944 KiB; audio 1003 frames
> ```
>
> Indistinguishable from the same-LAN numbers. This is the feature's actual premise finally met — "web/cloud
> play" means reaching the console from anywhere, and it now does.
>
> **What was missing.** A peer behind NAT cannot be reached at its own address, and we offered only our local
> one — so a distant console had nowhere to send. Our Init left every five seconds and nothing came back;
> neither side could open the path, not because either refused but because neither had been told where the
> other was. Found by capturing an off-network attempt (seven Inits out, zero packets in) and then finding
> the vendor's own answer in our `app-startup-checking-nat` capture: **classic STUN**, three Binding Requests
> from a fixed local port to two servers on 3478/3479 — the RFC 3489 NAT-classification pattern — with the
> reflexive address in `XOR-MAPPED-ADDRESS`.
>
> **Two things worth remembering about the fix.** The port matters more than the address: a NAT maps per
> source port, so a discovered mapping is only good for the port it was asked about, and that same port must
> then carry the traffic. And **both legs need it separately** — the control association and the A/V
> connection are different connections on different ports, and doing only the first left the control plane
> working off-network while the media prelude went unanswered.
>
> **Open (2026-09-06) — a typed address can never be followed.** `UseTypedAddress` sets no `HostId`, so a
> console paired by hand-typed address has no stable identity and cannot be relocated after a DHCP lease
> change; it is the one pairing that still breaks. The fix is available and small: a unicast SRCH probe to the
> typed address returns the same `host-id` a broadcast would, so a typed address could adopt the console's
> real identity at pairing time and become followable like any other. Needs a seam that probes one address and
> returns a `DiscoveredConsole` — today's reachability probe returns only `bool?`.
>
> **Still open, after a pass through the list (2026-09-05):**
>
> 1. **DONE — the app can now use this route.** `IStreamingSessionSource` chooses local or account by asking
>    the interface table what is reachable, and says which and why before connecting. `SessionController`'s
>    factory is async (a rendezvous is a cloud round trip, not a socket); `AccountRouteSession` makes the
>    session own the association and cloud session so teardown stays an ordinary dispose; and the streaming
>    page no longer builds the PlayStation session factory itself. Route choice is injectable and tested.
>    **Not yet exercised through the app's own UI** — only unit tests and the harness.
>
> 1b. **Superseded note — the app cannot use this route at all.** Everything above is reachable only from
>    `tools/Ripcord.ProtocolLab`; `HalyardAccountConsoleSession` has exactly one caller and it is the harness.
>    `SessionController` takes a **synchronous** `Func<IStreamingSession>`, while the account route is
>    inherently async (a rendezvous that takes 10–40 s) and hands back extra lifetime — the association and
>    the push channel — that must outlive the handshake and be disposed with it. Wiring it up means an async
>    session seam, somewhere in the UI to choose the route, and that lifetime plumbed through. This is now the
>    largest gap between what works and what a user can do.
> 2. **`0x000d`'s slot semantics — narrowed, not solved.** `+0x10` is **rtt**, derived and solid. The probe
>    reports exactly four quantities (`Senkusha Results - rtt:%d` and `… mtu:%d, bw:%d, loss:%f`), so the
>    other three slots are **mtu, bw and loss in an order that is still `[X]`**. Two constraints recorded in
>    the transport doc narrow it further. Settling it needs the event producers traced through the pump or
>    dynamic analysis — **not** a guess validated by watching the stream work, since the console demonstrably
>    does not check.
> 3. **CORRECTION (same day): a relay does exist, and I said it did not.** `docs/protocol/ps5-wan-relay.md`
>    documents one from a 2026-07-10 WAN capture — 134 MB of media through a single address, on the same
>    ports, with the same framing, `Host:` carrying the relay's address. What I actually established is
>    narrower and still true: **no relay *candidate type* appears in the twelve signalling captures**, only
>    `LOCAL`/`STATIC`/`STUN`. Those reconcile if the relay is handed over *as* the console's ordinary
>    candidate — which the relay doc's own finding supports, since it says the relay is a transparent IP-level
>    forwarder and "only the destination IP changes". If so we may already handle it and have simply never
>    been given one. **`[X]` how a relay address is delivered**, and untested either way.
>
>    Also worth recording so it is not re-derived: our own off-network session was **direct, not relayed**.
>    On the LAN our STUN reflexive came back as `203.0.113.180` — the same address the console advertises as
>    its `STATIC` candidate — so that is the home network's public IP and we hole-punched straight to it.
>
> 3b. **NAT classification — DONE. Candidate types — DONE.** Twelve rendezvous
>    captures contain no relay candidate of any kind; the only types that ever appear are `LOCAL`, `STATIC`
>    and `STUN`. The vendor's answer to a hostile NAT is *more candidates*, not a relay — and we were sending
>    two of the three with one mislabelled. Fixed: `STUN` is the NAT-assigned mapping, `STATIC` is the same
>    public address with our own port (a port-preserving guess), `LOCAL` is the local address; both legs offer
>    all three, deduplicated when a port-preserving NAT makes two of them identical. This does not make a
>    symmetric NAT work — nothing here can — but it removes the case where a second address was available and
>    we were not offering it. Original classification note follows. Two STUN servers on different operators, one source port, mappings
>    compared; a per-destination (symmetric) NAT is now detected and said out loud instead of appearing as
>    thirty seconds of silence. Deliberately detection only: it does not change the `natType` we send,
>    because that field's values are `[X]`. **Relay fallback is still not implemented** — a symmetric NAT is
>    now diagnosed rather than survived.
> 4. **The account route's login submit — DONE, and it found a bug in the waiting.** Watched against a
>    locked console on the LAN: prompt, submit, the console answers. The wait for session-ready was six
>    seconds (from a LAN capture where it took ~2.3 s); on this route it is longer, so a **correct** passcode
>    was reported as rejected — and the console unlocked anyway, which is how it was caught. Now 25 s: telling
>    a user their right passcode was wrong is the worst failure here, and a slow re-prompt on a genuinely
>    wrong one is much the cheaper error. Also retired a claim that was an artefact of not being able to
>    decrypt: the login-result byte was "opaque and different every time" only as *ciphertext*. It reads
>    `0x00` on both accepted logins seen, so whether it is a usable status is `[X]` and open.
> 5. **`0x0016`/`0x0017`/`0x0041` — shapes decoded, meanings still `[X]`.** The receive dispatcher is
>    `FUN_102089b0` (found via the frame receiver's caller, not by hunting case labels — that had returned
>    only noise). Each case states the length it demands and drops anything else: `0x0016` is exactly 2 bytes
>    read as one 16-bit value, `0x0017` is a count byte followed by `count` entries of two big-endian
>    `uint16`, `0x0041` is 8 bytes passed up uninterpreted. `0x0017`'s live payload decodes exactly — count 2,
>    pairs (4, 548) and (85, 4660). What they *mean* is still open, and nothing depends on them.
> 6. **Key frames under motion — DONE, and the answer is "nothing is wrong".** Measured against a game's
>    attract screen rather than a static menu: **1501 frames / 25 s, 60 fps steady, 2.8–7.8 Mbps tracking
>    scene complexity, 2 key frames** (session start and t+8 s). Infrequent IDRs are correct — they are
>    expensive — and the telling part is the *absence* of extra ones, since our demuxer requests a key frame
>    on detected loss. None fired, so the stream is clean rather than starved. The lab's `--watch` now
>    reports per-interval bitrate and key frames, which is what made this legible.
>
> **↻ Previous resume block (2026-09-05 — the account route delivers video on-LAN).**
>
> Watched back to back with `--watch`, counting frames off the neutral observables the app's decode
> pipeline consumes:
>
> | Route | Video | Rate | Bytes | Audio |
> |---|---|---|---|---|
> | LAN | 715 frames / 12s | 60 fps steady | 1259 KiB | 1209 frames |
> | **Account** | **897 frames / 15s** | **60 fps steady** | **1650 KiB** | **1514 frames** |
>
> Indistinguishable. A console reached entirely through the PSN rendezvous — no LAN pairing step — streaming
> H.264 and Opus. That is what the whole account-route effort was for.
>
> **What is left on this route** (none of it blocking):
> 1. **`0x000d`'s slot semantics are `[X]`.** Structure solved from our own binary; the console accepted
>    `[10000, 1454, 0, 0]` first time with two slots zero, which is also what a console that ignores the
>    contents would do. Do not promote to a derivation without evidence the values are read.
> 2. **Location independence is untested.** Everything so far is same-LAN. The route requires a reachable
>    console IP and keys credentials by it; the console's `STATIC` candidate is offered and ignored. See
>    the WAN item below — this is now the biggest remaining gap in the feature's *premise*.
> 3. `0x0016`/`0x0017`/`0x0041` payload meanings remain `[X]` (contents, not encoding).
> 4. Only one key frame arrived in 15s — expected for a static screen, but worth re-checking under motion.
>
> **↻ Previous resume block (2026-09-05 — the account route streams).**
>
> A live `accountconnect` now runs the whole thing: rendezvous, registration, `/sess/init`, `/sess/ctrl`,
> the A/V candidate exchange, the 9297 prelude, senkusha over that socket, protocol version 17, the probe
> report, and the console's stream-ready — then the stream loop. LAN re-verified unaffected.
>
> **The last bug was ours, in the chunk layer, and it was invisible from above.** `Send()` never advanced
> `_sequence`, so every chunk on a connection carried the sequence the console handed us in its accept. The
> console took the first payload on each connection and discarded every one after it as a duplicate —
> exactly as its own acknowledgements had been saying: they kept asking for the next sequence while we kept
> resending the previous. Request/response never noticed, because `rgst`, `init` and `ctrl` each open a
> fresh connection and send exactly one payload on it. What vanished was the persistent control channel that
> follows: the login passcode, every heartbeat reply, every attempt to answer the console.
>
> **How it was found, because the method is the point.** Three plausible theories (wrong counter, wrong key,
> route-specific crypto) all fitted the evidence and all were wrong. The question that broke it open was
> *"is our encryption wrong, or is the console not reading us at all?"* — answerable because `0x0910` is the
> one client frame with a guaranteed reply. The console echoed it on LAN and ignored it on the rendezvous
> route, which localised the fault to the transport rather than the cipher; the capture then showed every one
> of our data chunks carrying the same sequence number. **Two symptoms that looked route-specific were one
> bug, and neither was crypto:** the login submit LAN accepted and this route ignored, and the keyless
> `SESSION_REPLY` — the console will not arm its stream service until the client reports its probe results,
> and that report was among the dropped frames.
>
> **Still open, and now the interesting part:**
> 1. **`0x000d`'s slot semantics are `[X]`.** Structure solved from our own binary (four `uint32` BE, slot
>    order `+4,+8,+0xc,+0x10`, `+0x10` a millisecond time). The console accepted `[10000, 1454, 0, 0]` first
>    time with two slots zero — which is also what a console that ignores the contents would do. Do not
>    promote to a derivation without evidence the values are read.
> 2. **Watch actual video over this route.** "Stream loop running" is the handshake, not pixels.
> 3. `0x0016`/`0x0017`/`0x0041` payload meanings remain `[X]` (contents, not encoding — see the transport doc).
>
> **↻ Previous resume block (2026-09-04 — the A/V leg connects; one control frame is what's left).**
>
> A live `accountconnect` now negotiates the A/V connection, preludes it, probes it with senkusha,
> negotiates a protocol version on it and gets the console to answer **every** one. The Takion handshake
> completes on 9297. What it does not get is a session: the `SESSION_REPLY` comes back carrying no ECDH
> material at all, which surfaces as `server ecdhSignature verification failed`.
>
> **Four things were wrong and are fixed.** In the order they were found:
> 1. `SendOfferAsync` hard-coded `sid=1`. Harmless while a session offered only its control connection;
>    once the A/V leg was added both claimed stream 1 and the console's second connection collided with its
>    first. **The console takes our id for a leg from the OFFER, not the ACCEPT** — setting it in the ACCEPT
>    (which we already did) changed nothing. This was the one that got the console to answer Takion at all.
> 2. `RP-ConPath` was the constant `1`. The captured client sends **3** on this route, and the console runs a
>    different bring-up for each.
> 3. The senkusha probe opened a bare socket to :9297, which on this route answers nobody who has not
>    preluded there. It now runs as a first Takion association **on the negotiated A/V socket**, exactly as
>    the captured client does, and the console echoes every probe.
> 4. The stream association never sent a version request at all, so **no version was ever agreed** on it (the
>    console sent no ack, because nothing asked it to) and the `SESSION_REPLY` came back with no key. We now
>    offer `9,10,11,13,14,15,16,17` and are answered **17**. Note this was *not* the fix either — the reply is
>    still keyless — but the omission was real and the exchange now matches the capture exactly.
>
> **The remaining gap, and it is well defined.** After the probe the captured client sends control frame
> **`0x000d` (16 bytes, encrypted)**; the console answers `0x0010` (8 bytes) and then sends **`0x0034`**
> ("stream ready"), and only then does the client open the stream association. We send no `0x000d`, and
> `0x0034` never arrives — confirmed by waiting ten seconds for it. The LAN route needs none of this, which
> is why this never showed up before.
>
> So: **derive `0x000d`'s 16-byte plaintext.** [X] entirely. What is known: it is client→console, always 16
> bytes on both routes, sent 1–2 s after the probe finishes, and its first three bytes are echoed in the
> console's later `0x8910` reply (`<redacted>`/`<redacted>`/`<redacted>` in cap63/64/65). Its position suggests it
> reports what the probe measured. Do not guess it into the code — mark and derive.
>
> **↻ Previous resume block (2026-09-04, the account route's control plane completes).**
>
> A live `accountconnect` now runs the whole control plane over 9303 -- **`/sess/rgst`, `/sess/init`,
> `/sess/ctrl`, and the persistent control frames** -- and reaches the Takion stream handshake. The console
> shows its "remote play started" and disconnect notifications, which it never did before, and consecutive
> runs no longer strand it.
>
> **What `80108b13` was.** The console publishes a registration seed (`customData1`) on *every*
> account-route session, connects included, and both captures show `rgst`, `init` and `ctrl` over a single
> association. **Registration is part of each session on this route, not a one-time pairing** -- an
> association that has never registered is one `/sess/init` will not serve. `ConnectAsync` now registers over
> the very association the session then runs on.
>
> **Current failure, and it is the expected next milestone:**
> `Takion handshake: no INIT_ACK from the console`. The A/V leg is still pointed at the LAN default
> (console:9296). On this route it belongs on the **second negotiated connection to console port 9297** --
> the one the captures show being negotiated with its own `sid` right after the control one. Building that is
> the next task: run a second candidate exchange, open a second association to 9297, and hand it to the
> Takion stream instead of the UDP socket.
>
> **Two red herrings, recorded so they are not re-chased:** the `Host` padding / `Rp-Version` spelling made no
> difference (the console accepts either), and a duplicate `Content-Length` -- briefly introduced while
> chasing this -- did cause a 403 of its own. Deleting the cloud session is not permitted (PSN answers 405).
>
> **Live-testing note:** the "console never joined / Remote Play in use with no banner" state that cost many
> runs was a *half-open session* -- our attempt failed after the console joined, leaving it in a session
> nobody was in. It needed a reboot. Now that sessions complete, it clears itself.
>
> **↻ Previous resume block (2026-09-04, `80108b13` is route-specific).**
>
> Run back to back on a healthy console with the same stored pairing record:
> **LAN `/sess/init` accepted -- and the whole handshake then succeeded (`handshake accepted, stream loop
> running`)** -- while the **account route's `/sess/init` returned 403 / 80108b13**. So the refusal is
> route-specific, and the 9303 transport is not at fault: the connection opens, the request is delivered, the
> console reads it and rejects its content.
>
> Also worth noting: that is the **first full LAN session handshake using a pairing record obtained purely
> over the account route.** Previous runs stopped at the console's sign-in gate; an unlocked console does not
> raise it.
>
> **Landed but UNTESTED live:** the last two differences from the captured vendor `/sess/init` are gone --
> `Host` octets right-aligned in three columns, and `Rp-Version` (not `RP-Version`) on init only. The console
> stopped joining cloud sessions before these could be tried, so **do not treat them as a fix**; re-run
> `accountconnect` and see whether `80108b13` moves.
>
> **If it does not move**, the request is then byte-identical to a captured working one except for the
> registkey and address, and the cause is elsewhere -- most likely something about the cloud session's state
> that `/sess/init` validates on this route. `RP-ConPath: 3` (we send `1`) is still the untried candidate, but
> it lives on `/sess/ctrl`, which no run has reached on this route.
>
> **Two console-state traps that cost several runs:**
> 1. **A successful LAN `connect` blocks the account route afterwards** -- the harness leaves the stream loop
>    running and exits without a clean disconnect, so the console holds a Remote Play session and then refuses
>    to join a cloud one. Worth making `connect` end its session cleanly before the next live pass.
> 2. **Cloud wake is unreliable on this console** -- `cloudwake` accepted and `canWake=True`, yet it stayed
>    asleep across six polls and neither `accountpair` nor `accountconnect` could wake it, having done so
>    repeatedly earlier the same day. Rest-mode settings are unchanged and supported. Suspect power state
>    before protocol whenever a console will not join.
>
> **↻ Previous resume block (2026-09-04, a SESSION opens over 9303).**
>
> `accountconnect <ip> <duid>` runs it end to end. The chunk connection opens over 9303, `/sess/init` is
> delivered on it, and the console answers with HTTP. **The session transport is done.** The refusal code is
> now surfaced (it used to be discarded): `403 / RP-Application-Reason 80108b13`.
>
> **Start here, on a freshly power-cycled console, in this order:**
> 1. one LAN `connect <console-ip>` -- expect `/sess/init` accepted (it was, earlier, with this same record);
> 2. one `accountconnect <console-ip> <duid>`.
>
> If LAN passes and the account route still returns `80108b13`, the refusal is route-specific. **That A/B
> could not be completed today**: by the end the console was refusing TCP 9295 too while still reporting
> `awake=True`, so "the account route is refused" and "this console is refusing everything" are not yet
> distinguishable. The vendor client has no handler for `80108b13`, so the binary cannot name it either.
>
> **If it is route-specific**, the header diff against the captured vendor request is the place to look. The
> two candidates left, both recorded in the dirty-room log: the space-padded `Host` (`%3d` octets), and
> **`RP-ConPath: 3`** on `/sess/ctrl` where the LAN path sends `1` -- that reads as a direct-vs-rendezvous
> discriminator and is worth trying as soon as a run reaches `/sess/ctrl`. `Content-Length: 0` was missing and
> is now sent.
>
> **Live-testing note:** the console stopped joining cloud sessions after ~a dozen attempts in quick
> succession, and later refused TCP 9295 while reporting `awake=True`; it recovered on its own after a few
> minutes. Space the runs out, and suspect console state before protocol when refusal behaviour changes
> suddenly.
>
> **↻ Previous resume block (2026-09-04, the 9303 session transport WORKS live).**
>
> `accountconnect <ip> <duid>` runs the whole thing against hardware. Live result: the rendezvous completes,
> the prelude establishes, **the chunk connection opens over 9303, `/sess/init` is delivered on it, and the
> console answers with HTTP.** The transport is done. It answers **403**, which is now a request-content
> question rather than a transport one.
>
> **Next, and it is cheap:** re-run `accountconnect` and read the `RP-Application-Reason` -- the session path
> now surfaces it (it used to discard it, which cost a session of chasing the wrong cause on the registration
> path). The captured vendor `/sess/init` over 9303 is:
>
> ```
> GET /sie/ps5/rp/sess/init HTTP/1.1
> Host:  99.  9. 66.180:9303        <- space-padded octets, %3d style, and port 9303
> User-Agent: remoteplay Windows
> Connection: close
> Content-Length: 0                 <- we may omit this on a GET
> RP-Registkey: <hex>
> Rp-Version: 1.0                   <- note the lowercase 'p'
> ```
>
> Our header set already matches except possibly `Content-Length: 0` and the `Host` formatting; the LAN path
> is accepted with an unpadded Host, so padding is probably not it. Diff ours against this once the reason
> code is in hand.
>
> **Two lifetime bugs were found by running it**, neither of which unit tests would have caught, and both are
> fixed: the rendezvous used to leave the cloud session as soon as the association opened (right for pairing,
> fatal for a connect -- the console tears the association down with the session), and it unsubscribed its
> signaling handlers on the way out, so after a connect handed off nothing acknowledged the console and it
> TERMINATE-d. Both now live as long as the session.
>
> **Live-testing note:** the console stopped joining sessions after a dozen or so attempts in quick
> succession, while still reporting `awake=True`. It needs a rest (or a power cycle) rather than more
> hammering; that is not a protocol failure.
>
> **↻ Previous resume block (2026-09-04, the account route can now CONNECT).**
>
> `HalyardAccountPairing.ConnectAsync` runs the same rendezvous as pairing and hands back the **live
> association** instead of a pairing record. `PairAsync`'s body became a shared `RunAsync`; the two differ in
> three parameters (what builds the association, what to do once it is open, whether the seed is required).
> A connect does not wait for the seed -- it still sends `data1`/`data2` in the command, as the vendor's
> client does on both routes, but it holds a pairing record already.
>
> `HalyardDatagramSessionControlChannel` implements `IHalyardControlChannel` over that association, and
> `HalyardSessionFactory.Create` takes a control channel, so the session runs unchanged above it.
>
> **Verified after the refactor:** account pairing still completes end to end against hardware. That is the
> check that matters for a change to this flow -- the unit tests passing was necessary but not sufficient.
>
> **What remains for an account-route session:**
> 1. A composition that calls `ConnectAsync`, wraps the returned channel in
>    `HalyardDatagramSessionControlChannel`, and passes it to `HalyardSessionFactory.Create` along with the
>    stored pairing record. Roughly what `HalyardAccountConsolePairing` does for pairing, on the connect side.
> 2. A harness command (`accountconnect <ip> <duid>`) to drive it, so it can be run against hardware.
> 3. Then the live run -- and note `/sess/init` needs the console's `RP-Registkey`, which the credential store
>    already has after `accountpair`.
>
> After that, the second negotiated connection (console port 9297) is the A/V path.
>
> **↻ Previous resume block (2026-09-04, the 9303 SESSION transport is built).**
>
> `HalyardDatagramSessionControlChannel` implements `IHalyardControlChannel` over the 9303 association, so
> `/sess/init`, `/sess/ctrl` and the persistent control frames can all ride the account route.
> `HalyardSessionFactory.Create` now takes a control channel, so a session can be built over it. Unit-tested
> against a scripted console that closes each connection the way a real one does.
>
> **Two connection-lifecycle bugs came out of building it**, both of which would have bitten on the wire:
> opening a connection refused unless the association was `Established`, but the peer's teardown of the
> *previous* connection is usually still in flight -- so the next request went down a closing connection and
> read that teardown as its answer; and the wait for a connection to open tested for "no longer Established",
> which a stale teardown satisfies. Opening a connection now says nothing about the previous one, and the wait
> is for `Connected`.
>
> **What remains is composition, not protocol.** Nothing yet builds an account-route *connect* (as opposed to
> *pair*): that needs the cloud rendezvous run to the point where the association is established -- session,
> command, OFFER/ACCEPT, prelude, exactly as `HalyardAccountPairing` already does -- and then the established
> `HalyardDatagramControlChannel` handed to the session factory instead of being used for one registration
> exchange. The pieces all exist; what is missing is the flow that puts them together, plus a harness command
> to drive it.
>
> After that, the second negotiated connection (console port 9297) is the A/V path.
>
> **↻ Previous resume block (2026-09-04, ACCOUNT PAIRING WORKS, AND ITS RECORD IS CONFIRMED GOOD).**
>
> Account pairing completes, and the record it produces was then verified against the console over the LAN
> session path: `/sess/init` accepted the `RP-Registkey` and `/sess/ctrl` accepted the `RP-Auth` MAC derived
> from the record's companion. The run stops at the console's own sign-in gate (a locked user), which
> `connect --passcode=` supplies. That is the end-to-end proof the account route yields a genuine pairing,
> not just a 200 from `/sess/rgst`.
>
> The harness needed three fixes before that could even be measured, all now landed: `accountpair` persists
> the record through the app's own credential store, `connect` builds the session through
> `HalyardSessionFactory.CreateDefault` (real crypto + the same store) instead of a null store and the
> passthrough stub, and `connect` takes a login passcode.
>
> Live-testing note: the console drops back to rest soon after the pairing session ends, and a cold TCP 9295
> connect is then refused -- which looks exactly like a rejected pairing but is not. `cloudwake` plus a
> discovery poll brings it back.
>
> **Next:** `/sess/init` and `/sess/ctrl` over the **9303** transport, so the account route can run a session
> without needing LAN TCP 9295 at all. The seam is `IHalyardControlChannel` (ConnectAsync / SendRequestAsync /
> Send+ReadCtrlMessage), and the captured client opens two further chunk connections on the same association
> for exactly these two requests, then keeps the ctrl one for the persistent binary frames. After that, the
> second negotiated connection to console port 9297 is the A/V path.
>
> **↻ Previous resume block (2026-09-04, ACCOUNT PAIRING WORKS END TO END).**
>
> `accountpair` against PS5-<redacted> completes: seed delivered over the cloud, 9303 control transport, chunk
> handshake, `POST /sess/rgst`, a 276-byte response, a valid 34-byte pairing record stored as the console's
> credential. **The account ("web"/no-PIN) route is done.**
>
> The last blocker was sixteen bytes, found by static analysis of the client's request builder. It branches on
> whether the transport key slot is already populated -- the PIN route leaves it empty and derives a key from
> the passcode; the account route supplies `seed XOR registrationTable[selector]` up front. The two branches
> use the same selectors, the same context offsets and the same two tables, but **not the same transform**:
>
> ```
> PIN     : wrapped[i] = (material[i] ^ table[i]) - 0x2d + i
> account : wrapped[i] = ((material[i] - i) + 0x2b) ^ table[i]
> ```
>
> We used the PIN form on both, so the console recovered a material we never used, derived a different field
> IV, and read garbage where `Client-Type: ` sits -- refusing an otherwise byte-perfect request with
> `403 / 80108b09`. `WrapAccountMaterial`/`UnwrapAccountMaterial` now sit alongside the PIN pair, with a test
> asserting the two disagree so they cannot be quietly unified later.
>
> Also confirmed along the way: our four bundled field context keys are exactly the four the client selects
> between at runtime, and the registration and material-wrap tables lifted from the binary are byte-identical
> to the bundled ones.
>
> **Next:** the account route now yields a pairing record, so the natural follow-on is the session it unlocks
> -- `/sess/init` and `/sess/ctrl` over the same 9303 transport (the captured client opens two further chunk
> connections for exactly that), and then the second negotiated connection to console port 9297, which is the
> A/V path.
>
> **↻ Previous resume block (2026-09-04, transport solved; the 403 is SIXTEEN BYTES).**
>
> The account route's registration payload is almost entirely right. **Confirmed against three captured
> sessions, end to end from the cloud-delivered seed:** the transport key `key' = seed XOR
> registrationTable[context[0x18d] & 0x1f]` is correct, and so is the seed delivery — the captured request
> field decrypts to exactly `Client-Type: <ClientTypeHex>` from offset 16 onward. Previously this was only
> asserted; `LiveAccountKeyPathVectorTests` now pins it, along with `RecoverMaterial` against the PIN vectors
> that carry the material explicitly (never tested before).
>
> **The whole remaining bug is the field IV.** Exactly bytes 0..15 fail and nothing after them, which in
> AES-128-CFB128 means the key is right and the IV is wrong. The IV the sender used is now known *exactly*
> for three sessions, solved from `C0 = P0 XOR E_K(IV)` with `P0` known; the values are in
> `docs/protocol/captures/account_key_path_vectors.json` as `solvedFieldIv`. So we encrypt our first block
> under an IV the console does not expect, it reads garbage where `Client-Type: ` should be, and refuses with
> `403 / 80108b09`.
>
> **Do not redo the blind search.** Several hundred thousand candidates were checked against all three
> sessions and ruled out: HMAC-SHA256/1/512 under all four bundled context keys over {wrapped material, seed,
> data1, data2, derived key, raw table entry, concatenations} with counters to 4095 (100,000 for the
> material); every 16-byte window of the 480-byte context; the IV stored verbatim in the context; and all 32
> wrap-table entries under every bias/index-sign/half-order variant. The full list is in the dirty-room log.
> **Next step is static analysis of the client's account-request builder, with those three IVs as
> known-answer targets.**
>
> Also fixed: `nopin_registration_vectors.json`'s ps5 vector was misaligned by 10 bytes (it began at a 9303
> chunk prefix — cap63 splits the rgst request into a headers chunk and a body chunk). Re-extracted
> chunk-aligned. The tests over it still pass and were never invalidated, but one was vacuous.
>
> **↻ Previous resume block (2026-09-04, THE ACCOUNT ROUTE'S TRANSPORT IS SOLVED).**
>
> A live run against `PS5-<redacted>` now completes the entire 9303 exchange: prelude, hello, **the console's
> cookie**, echo, accept, `POST /sie/ps5/rp/sess/rgst` delivered over 9303, and a real HTTP response back.
> The console's `TERMINATE` is gone. This is the first time the console has ever answered us at the chunk
> layer.
>
> **Registration is still refused: HTTP 403, `RP-Application-Reason 80108b09`.** That is now a
> *registration-payload* problem behind a working transport, not a transport problem. The HTTP request is
> structurally identical to the vendor's — same headers in the same order, same 587-byte body length — so what
> is wrong is inside the encrypted body, i.e. the seed/key derivation. `80108b09` is not a generic refusal:
> `nopin_response_decomp.c` gives it a dedicated branch that re-attempts with a specific parameter, where
> `80108b03`-`08` all collapse to one error. The old wrong-transport code was `80108bff`. **Next thread: the
> registration payload crypto.**
>
> **The three bugs that were in the way**, all found by diffing against `ps-rendezvous/*.flows` (mitmproxy
> dumps that DO contain the client's own outbound signaling — an earlier resume block wrongly said no such
> capture existed):
> 1. We never acked the console's ACCEPT. Five vendor captures ack every inbound message; we acked only the
>    OFFER, so the console waited and gave up.
> 2. We could not parse the console's ACCEPT at all — it carries the vendor's malformed `"localPeerAddr":,`
>    and `JsonDocument` threw, so the frame was silently discarded. We *emit* that malformation ourselves.
> 3. The hello echo returned the whole cookie body instead of the body minus its 8-byte header, making a
>    64-byte echo where the vendor sends 56.
>
> **Searching lesson worth keeping:** the `.flows` bodies are JSON nested inside a JSON string, so every quote
> is backslash-escaped and a literal grep for `"action":"OFFER"` finds nothing. When a search comes back
> empty, doubt the search before the corpus.
>
> **↻ Previous resume block (2026-09-04, after the vendor-session diff).** The 2026-09-03 block below still stands
> except where noted here. The account route reaches the chunk handshake and stops: the console completes the
> 9303 prelude and then **ignores every chunk we send, having sent none of its own in any run**.
>
> **The big unlock: the dirty room already contains a complete successful account pairing** —
> `no_pin_push_frames.txt` (the push WebSocket) plus
> `app-startup-connect-and-pair-online-first-time-no-pin.pcapng`, against the same `PS5-<redacted>`. Its 9303 flow is
> the exact exchange we cannot get: hello → cookie in 3 ms → echo → accept → `POST /sess/rgst` → `200 OK` →
> close, then two more chunk connections for `init` and `ctrl`. Diff against it rather than guessing.
>
> **Confirmed and fixed (each was wrong in shipped code):**
> - **The chunk header is a 2-bit word count plus an 11-bit length**, not a marker plus 14 bits. The count
>   includes the header word, so the `0x244F` pair is what a count of 3 *means*; counts of 2 and 1 are legal
>   shapes we would have misparsed by two bytes. Also caps a chunk at 2047 bytes. Read from the vendor
>   library's own receive loop; live vectors still pass.
> - **The prelude echo's tail is the reflected peer endpoint** — `peerIPv4 XOR tagPair`, then
>   `peerPort XOR (tagPair >> 16)`, then two zero bytes. STUN's `XOR-MAPPED-ADDRESS` with the tag pair for a
>   magic cookie. Was carried as `[X]` and sent as zeroes. Confirmed on all six bytes against every echo in
>   every capture, both directions, and it decodes the console's echo to us live.
> - **`reqWord` is a counter, not a LAN/WAN discriminator.** Surveyed across every 9303 capture:
>   `0x00, 0x03, 0x17, 0x19, 0x1c, 0x1e, 0x145, 0x146`, rising within each series. **`0x40` — the value we
>   send — appears in no capture at all.**
>
> **Corrections to the block below:** its item 1 says an ACCEPT's candidate puts the console's address in
> `addr`/`port` and ours in `mappedAddr`/`mappedPort`. That reading is right, but note *why*: the sender always
> puts the **peer's** endpoint in `addr`/`port` and its **own** in `mappedAddr`/`mappedPort`. Also, PSN does
> **not** echo our own POSTs back over the push channel, so every signaling frame we receive is the console's —
> a dump of it contains no client-sent signaling to compare against.
>
> **Five hypotheses falsified live, so don't re-try them:** a word-count-1 hello; the Init before our OFFER;
> the Init after all signaling; the Init in the vendor's measured position (between our OFFER and ACCEPT);
> echoing the console's `skey`.
>
> **What is actually left.** The tag pair says who owns an association: a console that *accepts* an Init
> answers with the sender's own pair, halves exchanged. The vendor's console does that in 148 ms. Ours has
> invented its own pair in every run and every ordering — including when our Init arrived 51 ms after its own.
> Identity, address reflection, framing and the candidate exchange are all now confirmed to match, so the
> difference is in something the vendor's client *sends* that we have never observed. **The next step is a
> Fiddler/MITM capture of a real no-PIN pairing's outbound signaling** — the three POSTs whose TLS streams are
> visible but whose bodies are not. None of the five `.saz` archives in the dirty room predates the account
> route, so none contains it. Guessing at individual fields has now cost five live runs and produced two real
> fixes but no unblock.
>
> **↻ Previous resume block (2026-09-03, after the live pairing runs and the 9303 derivation).**
> - **The account route's cloud half is confirmed live** (reproduced twice against `PS5-<redacted>`): the console
>   joins our session ~0.75 s after the command, delivers the seed as `customData1`, our decrypt is correct,
>   and it then OFFERs its candidates. The `/sess/rgst` POST is then refused **HTTP 403 /
>   `RP-Application-Reason 80108bff`** because we send it over the PIN route's TCP 9295.
> - **The 9303 account-route control transport is now DERIVED and its codecs BUILT.** Derived from our own
>   `cap64` with a purpose-written pcapng reader, written up in `ps5-session-transport.md` ("The account-route
>   control transport"), and implemented: `HalyardControlChunkCodec` (the chunk layer),
>   `HalyardControlPrelude` (the 88-byte INIT/COOKIE prelude) and `HalyardDatagramControlChannel` (prelude →
>   handshake → request → response). 36 new tests, of which 6 are live vectors replaying the console's own
>   captured datagrams.
> - **The transport is INTEGRATED and the account route now gets as far as the chunk handshake** (four live
>   runs, 2026-09-03). Working live, in order: session → command → console joins → seed delivered and decrypted
>   → **full ICE negotiation** → **9303 prelude completes**. The console's final signaling message went from
>   `TERMINATE` to **`ACCEPT`** once the negotiation and prelude were right, which is the console saying it is
>   satisfied.
> - **Three protocol corrections came out of those runs, each from the wire:**
>   1. **The negotiation is a real offer/accept, not two independent advertisements.** This file previously
>      recorded "the exchange is symmetric; no ANSWER". `cap107` shows every message acked by a `RESULT`
>      carrying the *sender's* `reqId`, and the client answering the console's OFFER with an `ACCEPT` naming
>      the console's `sid` as `peerSid`. Stopping after our own OFFER left the console waiting and it never
>      opened its side. Also: an ACCEPT's candidate describes the **selected pair** — the console's address in
>      `addr`/`port` and ours in `mappedAddr`/`mappedPort`, the reverse of an OFFER's.
>   2. **A `sessionMessage` may only be sent to a member**, so our OFFER 404s until the console has joined.
>      It now goes out after the console's OFFER, which is what tells us it has.
>   3. **Either side may open the 9303 prelude, and on a LAN the console does.** `cap64` is a WAN pair where
>      the client initiates to punch out; on a shared LAN the console initiates as soon as signaling hands it
>      our candidate. The answering side must return the initiator's tag pair with its halves **exchanged** and
>      a zero request word (we sent a fresh pair and `0x19`), and **both sides send a CookieEcho** — answering
>      with only an Init leaves the peer re-sending its own forever, observed fourteen times over.
> - **THE BLOCKER, now pinned down precisely: the initiator role is unreachable, and the console never opens a
>   chunk connection to a responder.** The side that opens the association is the side that may open
>   connections on it. All four same-LAN vendor captures (`cap91`–`cap94`) plus the WAN ones show **the client
>   initiating every time**; the responder case is unrecorded anywhere in the dirty room.
>   - **Why we cannot take that role — measured, not reasoned.** Sending our `Init` *before* our OFFER was
>     tried live: the console ignored it and opened its own association, which its tag proves (it used its own
>     `096f0001`, not the swap of our `00014d0f`). So the console requires our OFFER before it will accept a
>     prelude from us — `SenderId` is checked, not merely claimed. And our OFFER is precisely what makes it
>     initiate, within ~200 ms. The loop is closed: OFFER first and it TERMINATEs; OFFER late and it wins the
>     race; `Init` before the OFFER and it is ignored.
>   - **The responder chunk path is already implemented** — the association answers a peer `Hello` with a
>     cookie and a `HelloEcho` with an accept. It never fires because the console sends no `Hello`; once the
>     prelude is up it simply keepalives every 10 s and waits.
>   - **What would actually resolve it**, in order of value: (a) a capture of the vendor client in the
>     *responder* role, which would say what a responder is supposed to do — we hold none, and it may not be
>     forceable; (b) understanding what makes the console open a chunk connection at all, which the current
>     captures do not isolate because in all of them the client asked first; (c) matching the vendor's whole
>     signaling shape so the console responds rather than initiates — blocked because our OFFER 404s until the
>     console joins and it offers immediately on joining.
>   - **Falsified along the way, so nobody re-runs them:** offering when the console joins (console sent no
>     9303 traffic at all and TERMINATE-d — the vendor can offer first because it *initiates the signaling
>     exchange*; on this route the console does); and `Init`-before-OFFER (ignored, per the tag evidence above).
> - **Prelude semantics corrected from the wire this session:**
>   - The token field is **a microsecond timestamp** (a console's rose ~500,190 between probes 0.5s apart), so
>     the exchange is a periodic RTT probe, not a one-shot handshake. **Every probe must be echoed** — echoing
>     once and going quiet gets you dropped after ~10s. Ripcord echoed once; a test asserted that as correct.
>   - An echo mirrors the peer's `(requestWord, token)` **as a pair**.
>   - `requestWord` is **not constant**: `0x19` from a WAN client, `0x40` from a same-LAN one, `0x00` from a
>     console. Still **[X]**.
>   - The tag pair tells you who opened: the answering side carries the halves exchanged, for the whole
>     association.
> - **A hello is retried.** The captured client's first goes unanswered; its second, ~1.1s later, is the one
>   the console replies to.
> - **Also unblocked, because this machine is a Windows box with VS 18** (full solution builds clean for x64):
>   the `GameInput` starvation bug (needs a two-pad hardware test) and the hardware pass over Stage C.
> - **⚠ SYNC WHEN SWITCHING MACHINES:** the whole `docs/` tree **and this `ROADMAP.md` are gitignored**. Copy
>   them out-of-band, including the dirty room's two new fixtures: the `SeedVectors` entry in
>   `registration_crypto_vectors.json` and the whole of `account_transport_vectors.json`.

Verified on `main`: **853 passed / 6 skipped** (2026-09-03, x64, with the dirty room) — **599/6** from
`Ripcord.Protocol.Halyard.Tests` and **254** from `Ripcord.Presentation.Tests`. The previous figures were
**852/6** (earlier the same day, before the live account-pairing arc's seed vector) and **835/6**
(2026-09-02, before the account-pairing UX wiring). (`feat/account-and-wan` was fast-forward-merged to `main`
on 2026-08-31, +76 commits, plus the account-route arc below.) The earlier **826/5** figure was
`feat/account-and-wan` at 2026-08-17, before the no-PIN solve's tests. There
are two suites, and the second needs neither hardware nor the dirty room, so a clean checkout sees its
full count. The previous figure, 653 on `feat/app-reimagining` (2026-08-06), predates the account/WAN work and
its ~170 new tests. The one before that, 448 on `main` (2026-08-05), predates both Stage A and the suite split.
The FEC/GMAC verification work and the ARM64/PMULL work were briefly split across two branches; both
are merged to `main` as of 2026-08-02 and the branches are deleted, so there is one number again. Two earlier
figures here were each correct when written and then outgrown by later commits — 424 (2026-08-02, outgrown by four
later commits that day) and 447 (2026-08-04, outgrown by the PS4 registration-bundle round-trip test). This is the sort of number that is stale the moment it is written, so treat a mismatch as
suite growth until a *failure* says otherwise.

Skips are host- and build-dependent, which is why the number moves without meaning anything. Five are the
hardware-dispatch cases of `GfMulHardwareTests`, which run whichever multiply paths the running machine can
actually execute — an x64 box skips the ARM cases instead of the x86 ones. The sixth is
`HalyardRegistrationCipherResolverTests.UnavailableCipher_CarriesNoContextKey`, which asserts what resolution
returns when it *fails*, so it correctly skips on any build that bundles the interop constants (i.e. the
default) and runs under `-p:BundleInteropConstants=false`.

Two further reasons the numbers move, both expected: the `Live*VectorTests` skip unless the gitignored
dirty-room fixtures are present, so **a clean checkout without `docs/protocol/captures/` sees more skips and
fewer passes** — that is the design (no secrets committed, CI green), not a regression. Counts above are from
a machine that *has* the dirty room.

> 🎯 **PS4 is closed as of 2026-08-05.** The previous top priority — a live PS4 **pairing-from-scratch** run
> — **succeeded from the Ripcord UI against a real PS4** (`PS4-<redacted>`), which was the one thing the byte-level
> verification could not establish: that the console *accepts* a request Ripcord originates, rather than that
> we reproduce a captured one. The PS4 registration crypto was solved, implemented into the seam, and
> byte-verified against four live captures earlier the same day (see Phase 2 below); this run confirms
> origination end-to-end. **No PS4 item is now open.**
>
> **The app re-imagining is landed** (branch `feat/app-reimagining`, plan in
> [`history/app-reimagining-plan.md`](history/app-reimagining-plan.md), gitignored like the rest of `docs/` so it
> travels by directory copy). All three stages are code-complete: **A** the app layer extracted out of the
> WinUI project into `Ripcord.Presentation` · **B** design system + information architecture · **C** input
> independence. What it still owes is **hands-on time, not code** — Stage C's six input commits and Stage A's
> steps 8–10 have never been driven by a human, and two Stage B items (high contrast, OS text scale at 150%)
> are at-the-machine checks by their nature. Those sections below are the list.
>
> **The account/WAN tier is committed** as of 2026-08-17, on branch `feat/account-and-wan` (four commits over
> `3ds`). PSN sign-in, the account's console list, cloud-assisted wake, STUN, the push channel, signaling and
> the rendezvous orchestrator — with the OAuth decision closed and the credential bundled under its own
> `NOTICE` section. Every cloud step is confirmed live; the flow still cannot complete, for the reason below.
>
> **Account-based (no-PIN) device registration — SOLVED & BUILT (2026-08-31).** This was the top priority and
> the single blocker on two fronts (SSO pairing and WAN play). The reverse engineering is done: the seed is not
> derived, the console *delivers* it — the client sends ephemeral `data1`/`data2` in the cloud `commands` call,
> the console field-encrypts the registration seed with them and returns it as `customData1` (double-base64) on
> the push channel, and the client decrypts it (our own field cipher + bundled `contextKey`) → `key' = seed XOR
> registrationTable[selector]`. Verified byte-exact against the vendor client; built and tested end to end on
> `main` (`HalyardAccountSeedDelivery` → `HalyardAccountPairing`). See Track B item 2 sub-item 1 for the full
> writeup. **Only the presentation/UX wiring remains** (exposing account pairing as an app action) — a focused,
> deliberate step, not reverse engineering. The falsified `data1/2`/device-id hypotheses were right as far as
> they went; the missing layer was `customData1`.
>
> **Behind that, in rough order of cheapness:** Track A's narrow question (do the senkusha probes *succeed*,
> or fall back silently?), which is half a diagnostics read; the `GameInput` starvation bug (an Xbox pad is
> dead while a DualSense is attached — a real defect in a path the project tests with daily); a hardware pass
> over Stage C; remaining Track D latency work; and the reverse rest-mode command.
>
> For context on what is *not* open: the 2026-08-03 LAN **wake** + console **login-passcode** arc is derived,
> implemented, and **verified live end-to-end** (an asleep, signed-out paired console now streams on Ripcord
> alone).
>
> **The protocol/crypto track is effectively closed.** FEC is fully settled (matrix `[C]`, coded length
> `[C][W]`, field `[V]`), and both the control *and* congestion GMAC AAD rules are `[V]` and pinned by tests.
> A 2026-08-02 audit found the previous top priority ("first live run of the senkusha probes") was stale —
> that bring-up runs on every connect and always has.

## Ports, and the streaming-quality work

## The 3DS port — a second client, and the spec's first real audit

**Added to this file 2026-08-17, having been missing from it entirely** while ~70 commits of work landed.
That is itself the lesson: this file is the source of truth only while someone writes in it, and a body of
work with its own README in its own directory is exactly the kind that quietly stops being tracked here.

`ports/ripcord-3ds` is a from-scratch C client for modded **New 3DS** hardware, on branch `3ds` (unmerged,
~70 commits ahead of `main`). ~18.7k lines. It is not part of `Ripcord.slnx` and never will be — sharing a
repository buys shared specs, constants and test vectors, not a build system with a cross-compiler for a
different CPU and OS. It keeps **no copy** of the interop constants: `tools/gen_constants.py` reads the one
committed JSON at build time and generates a C translation unit into the gitignored `build/`, so one bounded
exception does not quietly become two.

**Why it exists is the part that matters.** Ripcord's independence claim rests on `docs/protocol/` having
been derived from this project's own captures. A spec is only as good as its ability to produce a working
implementation, and until this it had produced exactly one — by the people who wrote it, in the language they
wrote it alongside. A second client in a different language, on a different OS, on a CPU with a different
word order, is the cheapest honest way to find out which parts of that document are load-bearing and which
only *look* complete because the author already knew the answer. **Every place the port had to guess is a
defect in the spec**, and that is the output worth having.

**What it does today.** The full session against real hardware: discovery, `/sess/init` → `/sess/ctrl`, the
binary control channel, senkusha's echo *and* both MTU legs (RTT 2 ms from 10/10 echoes; MTU 1454 confirmed
both directions — so the launch spec declares measured figures instead of `rtt: 0` and an assumption), the
Takion handshake on UDP 9296, the `SESSION_REQUEST`/`SESSION_REPLY` ECDH exchange, per-direction AES keys and
IVs, GMAC-sealed control traffic, H.264 decoded through the MVD block onto the top screen at a requested
960x540, Opus audio to NDSP, and controller input back up the stream socket.

**What it found, and fed back.** See the 2026-08-12 research-log row for the full list; the load-bearing ones:

- **Four control-channel message types neither implementation models** — `0x0005` (1 byte), `0x0017` (9),
  `0x0016` (2), `0x0003` (4, arriving after a REST_MODE ack). Meanings `[X]`. That the framing skipped all
  four cleanly is itself a `[V]` check on the 8-byte header against live traffic.
- **A defect in *our* code, not the spec.** Takion **continuation** fragments carry the channel id at value
  offset 4, exactly as first fragments do — only the reserved region shrinks. `ps5-session-transport.md`'s
  silence on this had been recorded in the port as "meaning unconfirmed", and zero-filling those bytes put
  every fragment after the first on channel 0 and got silence.
- **Three facts promoted to `[V]`** by a successful key negotiation: the streaminfo/launchSpec OFB counter is
  0 (never pinned by any captured vector on either side), omitting `adaptiveStreamMode:"resize"` is harmless,
  and the A/V Takion port is 9296 (senkusha 9297) for this firmware.

**Open on the port:** on-device PIN pairing (backlogged deliberately — it streams from an imported pairing
record today), and picture quality is now bounded by the screen rather than the pipeline. Its own README's
"still missing: audio, input, senkusha's RTT/MTU legs" line was **stale as of 2026-08-17** — all three have
landed since it was written.

## The portable core, and the Vita port — started 2026-08-17

> **Not in this repository.** Everything in this section lives on the unpublished `feat/vita-port` branch:
> `ports/common/` with the extracted protocol core, its three platform implementations, the mbedtls
> cross-build script, and the Vita port with its hardware checklist. Only `main` is published, so none of
> those paths resolve here, and `ports/` holds `ripcord-3ds` in its pre-extraction layout — the very layout
> these entries describe moving away from. Kept because the reasoning is worth having; flagged because a
> reader would otherwise go looking for files that are not there. Cross-platform work is deliberately
> parked until the Windows client is feature complete.

**`ports/common` now holds the protocol core**, on branch `feat/vita-port`. The 3DS port was written as
a single-platform tree; auditing it for a second target found that **71 of its 88 source files reference
no operating system at all**. The whole coupling was three libctru calls (`osGetTime`, `svcSleepThread`,
`svcGetSystemTick`) in three files, plus sockets — so the extraction was mostly `git mv`, and the 3,243
host assertions came through it unchanged.

`ports/common/platform/rc_platform.h` is the seam: a monotonic millisecond clock, a sleep, a
high-resolution tick, a CSPRNG. The recorded rule is that **nothing enters it that only one platform
needs** — a seam earns its place by having two real implementations, which is why decode/audio/present
are absent from it despite being the largest platform surface any port has. Three implementations exist
(libctru, vitasdk, and a host one for the tests); the host one is there because a header with one caller
and one implementation is indirection, not a seam.

**Pulling the core out paid for itself before any Vita code was written.** The host suite stopped at the
socket boundary, leaving `rc_tcp.c`, `halyard_control_session.c`, `takion_reliable_channel.c` and parts
of `util/` compiled by the cross-compiler and by nothing else. A new `make -C ports/common/tests compile`
target builds all 36 portable files on the host and immediately found two latent defects: **`inet_aton`
is a BSD extension rather than C99** (libctru declares it unconditionally, so the 3DS build never
noticed; vitasdk's documented helper is `sceNetInetPton`), and `clock_gettime` needs an explicit
`_POSIX_C_SOURCE` under strict `-std=c99`.

**The 3DS cross-build is verified against the refactor** (2026-08-17, devkitARM 16.1.0): all seven
`.3dsx` targets build from a clean tree, `crosscheck` passes, and the host suite is unchanged at 3,243.
The extraction did break one thing the path-resolution check could not see — every 3DS-side file
addressed core headers as a sibling or an uncle, and a quoted relative include resolves against the
including file before it consults the search path, so `-I` alone could not fix it. 46 includes across
12 files now address one core include root subdirectory-first. Fixed later on the same branch.

**The Vita port builds** (2026-08-17): the portable core compiled for Cortex-A9 first try under
`-Werror -Wconversion`, the platform seam and a Phase 1 crypto smoke test link, and `make` produces a
`.vpk` through vitasdk's elf→velf→eboot→vpk chain. Phase 0 is closed — sockets need no seam (vitasdk
ships POSIX names that link against `-lSceNet_stub`), the CSPRNG is `sceKernelGetRandomNumber`, and the
ECDH backend is cross-built rather than packaged.

**The ECDH backend was the last Phase 0 blocker and is now solved by building it.** vitasdk packages no
mbedtls, so `ports/common/tools/build-mbedtls.sh` cross-builds only its ECP/MPI layer — five translation
units, ~23 KB of ARM text, no TLS or X.509 — from a pinned, SHA-256-verified 2.28.8 release, matching
devkitPro's `3ds-mbedtls` so one `rc_ecdh.c` serves both ports. Nothing third-party is vendored.
**It also closed the last skipping host test**: `ecdh_test` needed `libmbedtls-dev` and skipped without
it, so the suite is now **3,287 assertions with nothing skipped** on a machine with only a C compiler.

**Paused 2026-08-17 pending hardware, with the resumption list written while it was fresh.**
`ports/ripcord-vita/HARDWARE-CHECKLIST.md` is the ordered list of what building-without-a-device leaves
unanswered: what to run, what failure looks like, and the environment both SDKs need but do not export.
The 3DS port's hardest bugs were never algorithm bugs — they were idioms correct on the reference
platform and invalid on the target, which no vector catches and only a device finds. Three are already
queued for the Vita: whether the loader honours `sceUserMainThreadStackSize` (default 4 KiB, against a
call chain that needed 128 KB on 3DS), whether `sceKernelGetRandomNumber` short-fills past 64 bytes
(P-521 keys are 66, and the ECDH round-trip test cannot catch it — both sides would be equally weak and
still agree), and whether `bind()` to port 0 is accepted (it is not on 3DS, and that cost a hardware run).

**`ports/ripcord-vita` is built, not run.** The seam implementation there has never been compiled
and says so in its own header comment; every hardware claim in its README carries `[C]` or `[X]`.

**Its reason for existing is deliberately not the 3DS port's.** That one is a completeness test for
`docs/protocol/`, and a port that reuses its code re-tests the seam rather than the spec — claiming
otherwise would be dishonest. The Vita is simply **the first target where the hardware is not the
limiting factor**: 960×544 native against a stream already requested at 960×540, hardware H.264 through
a conventional decoder API rather than the MVD block's war diary, two real analog sticks plus a rear
touchpad, and 256 MB where the 3DS had a 32 KB main stack that crashed twice.

**Open on the Vita port, in the order they block things:**
- **Sockets.** vitasdk's documented surface is `sceNet*`-prefixed; whether it also ships POSIX-named
  wrappers is `[X]`. If not, ~12 call names in the core need mapping and the seam grows a socket
  section. Nothing else starts until this is settled.
- **`rc_random_bytes`.** Likely `sceKernelGetRandomNumber` `[X]`. Deliberately unimplemented rather than
  stubbed — a port that builds and quietly generates predictable key material is worse than one that
  does not build.
- **The main-thread stack.** `sceUserMainThreadStackSize`, default reported as 4 KiB `[X]`. The 3DS
  needed 128 KB and crashed twice getting there, the second time from cumulative call depth down the
  audio loss-concealment chain. That chain exists here too; pre-empt it.
- **Binding port 0.** Rejected by the 3DS SOC service and it cost a hardware run. `sceNetBind`'s
  behaviour is `[X]` with no documentation found either way.
- **Bluetooth controller input.** Testing the full input path (analog L2/R2, L3/R3 — none of which a
  handheld Vita has natively) needs a taiHEN kernel plugin, not vitasdk. Whether such a plugin surfaces
  the full button set through `sceCtrl`'s extended reads is `[X]`, and it decides whether input can be
  tested completely on this hardware at all.

**Worth doing early, and it cuts the port roughly in half:** split the portable orchestration out of the
3DS's `connect/main.c`. It is 2,074 lines and most of it — `service_control`, `wait_for_session_ready`,
`check_sign_in_gate`, `await_control_message`, `seal_control_packet`, the stream callbacks — is flow
logic with no 3DS in it.

**Clean-room note specific to this port:** a third-party Remote Play client exists for this hardware and
is off-limits — source, constants, wire-format notes, all of it. `CLAUDE.md`'s same-project exemption
covers *Ripcord's* ports reading `src/`, not another project's. The temptation is sharper here than on
the 3DS precisely because a same-platform implementation exists to compare against.

## Streaming quality and resilience — landed, live-test status mixed

This was tracked here as an in-flight branch (`feat/streaming-quality-and-resilience`). **That branch no
longer exists: the work is committed on `main`.** The live-test status below was **substantially wrong and
was corrected on 2026-08-02**: the senkusha probes, the stall watchdog and link metrics all run on every
connect, so none of them is unexercised. What remains genuinely unknown is narrower — whether the probes
*succeed* or silently fall back to their estimates (Track A).

*Live-tested already:*
- **Composite controller input** (`Ripcord.Input/CompositeControllerSource.cs`) — runs DualSense-HID and
  GameInput simultaneously and merges frames (buttons OR-ed, axes take the value furthest from centre)
  instead of picking one engine at page load. Fixes mid-session pad swaps and Xbox-pad-while-DualSense-
  attached. This also closes the old "mixed-fleet hotplug" gap. Confirmed on the Ally X: swapping pads
  mid-session now works. (No automated test distinguishes this from the "never run" items below — this rests
  on hands-on testing, same as the touch-flyout line beneath it.)
- **Touch flyout + scrollable diagnostics** — on-screen PS/Create/touchpad buttons for pads that lack them,
  and the diagnostics panel scrolls on a small screen. Both confirmed on the Ally X.

*Runs on every connect — the "never run against a console" heading here was wrong (corrected 2026-08-02):*
- **Senkusha echo + MTU probes** (`SenkushaEchoProbe.cs`; `HalyardSenkusha.cs`) — see Track C, which is where
  the detail lives now. The receive loop routes base types 0x02/0x03 instead of discarding them.
- **Link metrics** (`Ripcord.Core.Net/Udp/LinkMetrics.cs`) — real measured MTU/RTT replacing the hardcoded
  `rtt: 0` / `mtu: 1454` in the launchSpec. Keeps 1454 as a ceiling on purpose: it's the value a real client
  is known to send, and console behaviour above it is untested. Note the probe result now feeds this, so a bad
  probe would show up as a bad launchSpec — worth watching on the first live run.
- **Stall watchdog** — uses console-activity timestamps to tell "video stalled because the scene is static"
  from "session is dead," fixing a false-reconnect bug.

**Next action:** not a live test of these — they already run. See Track A for the narrower question that
is actually open (do the probes succeed, or fall back silently?).

## Completed backlog items

### Phase 2 — PS4 support, closed 2026-08-05

The record of how PS4 support got closed. It lives here rather than in the roadmap because that is what
this file is for; the roadmap keeps a pointer and nothing else.

- **Phase 2 — PS4 support. CLOSED 2026-08-05** — the entry below is kept in full because it is the record of
  how it got there, and its opening clause ("not started") was outgrown by the work described further down it.
  The split is
  `.Common`/`.Takion`/`Halyard` (there is no `.Ps5` project), and groundwork already exists —
  `HalyardConsolePlatform.Ps4`, the `/sie/ps4/rp/sess/rgst` endpoint with `RP-Version 10.0`, SRC2/RES2
  discovery, and a PS4 option in the pairing dialog. **cap53 (2026-08-03) wire-confirms all of that
  groundwork `[W]`** against a real PS4 — endpoint path, `RP-Version 10.0`, `SRC2`/`RES2` arming probe — and
  adds the discovery/wake deltas the code did not yet carry: **discovery/wake on UDP 987** (not 9302) with
  **`device-discovery-protocol-version:00020020`** (not `00030010`). PS4's WAKEUP field set and
  `user-credential` derivation are identical to PS5's. **Two PS4 unknowns remain before the existing stack
  can be claimed to drive PS4:** (a) that 987 actually *wakes* a PS4 — cap53's wake got no reply, network-wake
  was disabled; and (b) ~~PS4 session crypto~~ — **DERIVED and validated `[V]` (2026-08-03).** PS4 session control-field
  crypto is the KDF dispatcher's **(mode 0, keytype 2) → `FUN_1fdd80`** variant (PS5 is mode 1 → `FUN_1fe340`),
  reversed from our own DLL and confirmed byte-for-byte against all three cap53 sessions' `RP-Auth` +
  `RP-OSType` → `Win11.0\0`. Same field-IV/CFB machinery and role convention; only the KDF arithmetic/tables
  and the context key (`B_eq_0`, which falls out of mode 0) differ. So the crypto seam is no longer a PS4
  blocker. Everything above it (discovery, wake format, `SRC2`/`RES2`, `/sie/ps4/…`, `RP-Version 10.0`, Takion
  handshake shape) is wire-confirmed. The **A/V stream key schedule needs NO PS4 variant**: cap53's Takion `SESSION_REPLY` parses as the identical
  PS5-v17 protobuf (`clientVersion 17`, 133-byte P-521 `ecdhPublicKey`, 32-byte `ecdhSignature`), so PS4 uses
  the modern P-521 handshake our `HalyardStreamKeySchedule` already implements — *not* a P-256 "older protocol"
  (early note corrected). `DeriveDirection` (SP800-108) takes no family/mode input; only the curve is
  version-dependent and `clientVersion 17` → P-521 via the existing `CurveForVersion`. So for the *streaming*
  path the only PS4-specific algorithm is the control KDF (`FUN_1fdd80`, `[V]`); stream handshake and key
  schedule are the PS5 machinery unchanged. **Registration, however, is NOT the PS5 machinery** — see the
  registration status below.
  The **stream key schedule is now `[V]`** too: `DeriveDirection` = `generateKeyIV`/`FUN_1012d9e0` in the clean
  v1 the vendor control DLL, disassembled and shown byte-identical to ours, **unconditional, with exactly two
  call sites (dir 2/3) and no family dispatcher** — so PS4 runs the same function validated against PS5 hardware.
  handshakeKey + ecdhSignature are `[V]` on PS4 too (cap54/cap57). And the **987 wake is now `[V]`** —
  cap54–cap57 show a resting PS4 (`620 Server Standby`) waking to `200 Ok` after a `WAKEUP` on 987 (cap53's
  no-reply was just network-wake disabled). **The PS4 streaming path is `[V]`/`[W]` — discovery, wake,
  control-field crypto, stream handshake + key schedule — and streaming from an imported pairing record works
  end-to-end** (H.264; PS4 is H.264-only, coerced). **Registration-from-scratch is now SOLVED, IMPLEMENTED,
  and byte-verified `[V]` (2026-08-05, cap61–cap64).** The registration *field cipher* was already `[V]`
  (AES-128-CFB, IV = `HMAC-SHA256(B_eq_0, material‖be64(ctr))[:16]` — context key `B_eq_0`, not PS5's
  `B_eq_1`). The transport-key derivation is **NOT a bespoke primitive** — the 2026-08-04 "bespoke
  a vendor net-auth symbol / `FUN_101f5700` / the vendor registration tag / 63-byte-secret, needs emulation" conclusion was
  analysing a **stubbed dead branch** (path A, `or eax,-1; ret 8`, verified byte-identical in the live
  process). PS4 registration is the **same table mechanism as PS5** (`FUN_101fcda0`, a mirror of PS5's
  `FUN_101fd830`): `K = table[context[397]&0x1f]` (transposed column, stride 0x20) with `be32(PIN)` folded
  into `K[12..16]`; the IV material is wrapped into the context at offsets `0x191`/`0xc7` (same as PS5) via
  `w[i]=((t[i]^m[i])+0x29+i)&0xff`. **Only four constants differ from PS5:** key table (`DAT_102f873c`), wrap
  table (`DAT_102f936d`), wrap bias (`+0x29` vs `-0x2d`), and context key (`B_eq_0` vs `B_eq_1`). All four
  cap61–cap64 pairings reproduce key **and** material exactly. Reversed by a live Frida hook on our own client
  + static analysis of our own DLL — **no emulation, no third-party implementation**. **All PS4 work is now
  landed:** (i) control-KDF; (ii) streaming path (imported record); (iii)
  **registration KDF + material wrap implemented into the seam** — PS4 tables added to
  `halyard-v1-constants.json`, `HalyardRegistrationKdf`/`Secrets`/`Cipher` + `HalyardInteropConstants` +
  `AppRegistrationCipher` family-keyed by console platform, `NOTICE`/`CLAUDE.md` amended, and a bundle
  round-trip test added (the C# seam reproduces cap64 byte-for-byte, verified locally against the dirty-room
  vector). **(iv) The live pairing-from-scratch run is DONE and succeeded `[V]` (2026-08-05)** — a PS4
  (`PS4-<redacted>`, discovered on 987) paired from scratch from the Ripcord UI, which confirms the piece byte-level
  verification cannot: that the console *accepts* a request Ripcord originates, not merely that we reproduce a
  captured one. **PS4 has no open items.** See the `2026-08-05` research-log rows and the
  dirty-room `pathB_groundtruth.md` / `ps4_regist_full_solution.json`, and
  `docs/protocol/ps5-local-discovery.md` PS4-family section.

### SOLVED — Settings page killed the app: a native probe on the UI thread (2026-08-06)
**Pre-existing, and it predates the Stage A work.** Opening Settings terminated the process every time on this
ARM64 host: no managed exception, nothing in `crash.log`, window simply gone. WER records a stowed exception,
`0xc000027b` with `E_UNEXPECTED` (`0x8000ffff`), faulting module `Microsoft.UI.Xaml.dll`.
**Cause.** `VideoCapabilities.IsCodecDecodeAvailable` — `MFStartup` + `MFTEnumEx` — cannot be called from the
WinUI UI thread. `SettingsPage` called it inline from `Page_Loaded`. `AboutPage` has always run the identical
query through `await Task.Run(...)` and has always worked. Two call sites, one hazard, and only one of them
knew about it.
**Found by running the app** and driving it through UI Automation, then bisecting:
- reproduces on the **pre-Stage-A-step-10** `SettingsPage` → not a regression from the extraction;
- reproduces with the **pre-H.264-fix** native build → the codec detector is not implicated;
- reproduces without the Stage B style split → not the resource dictionaries.
What the extraction *did* change is that the crash became findable: the probe is a seam now, so it could be
given a type that carries the constraint.
**Fix.** `IVideoCapabilitiesProbe` returns `Task` for all three queries, and `NativeVideoCapabilitiesProbe`
wraps each in `Task.Run`. Asynchrony here is not about throughput — it is what makes "must not run on the UI
thread" a property of the interface rather than folklore. `LoadAsync` applies stored settings synchronously
first so the page renders real values immediately, then awaits the probes; the GPU picker gets the same
treatment, since enumerating adapters builds a D3D12 device per adapter.
**The lesson worth keeping.** The `try`/`catch` around that call was never protecting anything — a stowed
exception is not catchable — so it read as safety while the failure it was written for took the process down
regardless. A guard that cannot catch what it names is worse than no guard, because it stops the next person
looking.

### SOLVED — PS5 "audio but no video" was an H.264 slice misread as an HEVC parameter set (2026-08-06)
**Root cause found and fixed** (`VideoRenderer.cpp`, `DetectAnnexBCodec`). The console was never at fault: it
sent exactly the H.264 that was requested, and the client put it through an HEVC decoder.
The detector tested the HEVC NAL type first and accepted it on the type alone:
```
hevcType = (b0 >> 1) & 0x3F;
if (hevcType == 32 || 33 || 34) -> HEVC       // VPS_NUT, SPS_NUT, PPS_NUT
```
An HEVC NAL header is **two** bytes; an H.264 one is a single byte of `forbidden_zero(1) | nal_ref_idc(2) |
nal_unit_type(5)`. An ordinary H.264 slice with `nal_ref_idc = 2` is `0x41` (non-IDR) or `0x45` (IDR), and
`(0x41 >> 1) & 0x3F` is **32 — VPS_NUT exactly**. Every H.264 stream is full of those bytes, so the test never
distinguished the codecs at all; it merely ran before the H.264 test. Whichever access unit arrived first
therefore decided the session: opening on a slice rather than a parameter set "succeeded" with the wrong
answer, latched `m_codecDetected`, rebuilt the decoder as HEVC, and fed it H.264 for the rest of the session.
**The capture that settled it** (2026-08-06, ARM64, Adreno X2-45) — the opportunistic F8 report this entry
previously asked for, and it split the hypotheses on the first try:
```
codec              H264   hdr=False
decoder            HEVCVideoExtension (DXVA) — stream is HEVC, overriding the H.264 request
decoder diagnostic samples=0 hr=0x00000000 out=NV12 coded=1280x720 geom=0 nal=41,00 head=00000001 tries=1
frames             0 decoded, 0 presented
audio frames       1547 decoded, 0 skipped
bitrate            2462 kbps
```
Every field is consistent and none of it needed guessing: video **is** arriving (bitrate, audio alive), the
decoder produced no samples with **no error** (`hr=0x00000000` — an HEVC decoder fed H.264 simply never emits),
geometry was never learned (`geom=0`), and `tries=1` shows the wrong verdict landed on the very first submit.
`nal=41,00` names the exact byte that caused it — and `00` is itself proof, since HEVC forbids
`nuh_temporal_id_plus1 = 0`.
**The fix.** Only a *parameter set* may decide the codec, and an HEVC one must satisfy the rest of its header:
`nuh_layer_id == 0` and `nuh_temporal_id_plus1 == 1` (parameter sets are base layer, TemporalId 0), plus the
forbidden-zero bit. `0x41,0x00` decodes as layer_id 32 and temporal_id_plus1 0 — two independent violations,
now two independent rejections. A slice can no longer decide anything, because its header carries nothing that
separates the codecs; an access unit without parameter sets is unclassifiable and the caller retries, which it
already did and had simply never needed to.
**Correcting this entry's own earlier review.** The 2026-08-05 renderer read recorded that "`DetectAnnexBCodec`
retries on every submit until a parameter set gives a verdict (not once)". The first half was right and the
parenthesis was the point — but "until a **parameter set** gives a verdict" is not what the code did. It
retried until *any* verdict, and a slice always produced one. That misreading is what let this section rule out
the actual cause and spend its effort on the stream instead. Worth keeping visible: the loop was read
correctly and the predicate inside it was not.
**Also disproven for this instance:** the leading hypothesis was a lost initial IDR with no recovery path. It
is not needed to explain any of the above, and `tries=1` rules it out directly — the fault was decided before
loss could matter. The underlying gap it identified is real and stays open on its own merits, below.
**Confirmed live (2026-08-06):** a PS5 H.264 session came up successfully on the fixed build.
One caveat worth keeping rather than dropping: the original fault was *intermittent* — it once worked on a
retry with no code change — so a single success cannot by itself distinguish "fixed" from "got lucky". The
strong evidence remains the root cause being read out of the code and matching every field of the capture. The
detail that would settle it conclusively is cheap: take an F8 report during a **working** H.264 session and
check the decoder row now names an H.264 decoder with **no** "overriding the H.264 request" suffix. If it
still says HEVC, the stream really is HEVC and something else is going on.

### Stage B — progress, and what is owed (2026-08-06)
**Landed.**
- **Design system split** into `Ripcord.Tokens/Text/Surfaces/Motion.xaml` behind the existing entry point. A
  4px spacing scale (`x:Double` + `Thickness` pairs), five icon-size tokens, radius semantics, motion durations
  and easings aliased onto WinUI's own.
- **Eleven of thirteen page-local styles merged** into the shared set. Six were exact duplicates; five were
  *not* duplicates but a collision — `RowLabelStyle`/`RowValueStyle` meant different things in AboutPage and
  SessionPage. Now `RipcordDetail*` (body, read-and-copy) and `RipcordInstrument*` (caption, scanned).
- **`AppEffects`** reads transparency and high contrast, which nothing read before. Vendor wash → 0 and family
  marks → system brush in high contrast; Mica suppressed when transparency is off, and dropped for the
  duration of a stream (opaque video over it; power win on a handheld).
- **One call site for the native capability queries**, enforced by `NativeCapabilityAccessTests`.
**Owed, in the order it should be picked up.**
- [x] **The no-page-local-styles test — landed.** `PageStyleTests` fails the build if a page declares a keyed
      `Style`. Two moved out to make it pass: About's section header (its 16px lead-in now sits at the two call
      sites, where a margin is visible rather than baked into a style) and `StepDashStyle`, which the test
      caught and which turned out to be a genuine shared primitive. Exemption is item templates and an items
      control's container style, one key listed.
- [x] **`SettingsControls` measured and adopted.** A trimmed Release publish produces the **identical 37 ILLink
      warnings** with and without the package, and the same published size to a tenth of a megabyte — it is
      trim-neutral. SettingsPage is now 203 lines of `SettingsCard` rows rather than 438 of hand-rolled
      grid-in-border, and HDR is a `SettingsExpander` because the readiness checklist is why anyone opens it.
  - **This also answers "whether WinUI 3 itself trims cleanly", which was open.** It does not, but it fails in
    a contained way: all 37 warnings are IL2075/IL2081 from CsWinRT's ABI layer (generic fallback initialisers
    for `IReadOnlyDictionary`, `IVectorView`, `IAsyncOperation` and friends). **None are from app code.** The
    remaining blockers to `PublishTrimmed=true` are unchanged — `Ripcord.Cloud.Halyard`'s 6 × IL2026 and one
    IL2075 in `Ripcord.Core`.
- [x] **IA: `NavigationView` retired.** Two-layer window: a chrome `Frame` plus the stage. Settings and About
      are title-bar commands that *navigate*, so back reaches them — under the pane there was no back at all.
      Initial focus now lands on a console (`IInitialFocusTarget`, seeded at low priority because a frame's
      `Loaded` fires before its content page has populated), which was the actual argument for removing the
      pane. Also deleted one of two "Add console" affordances.
- [x] **HomePage adapts to console count — landed.** Three layouts: empty state at zero, a hero at exactly one
      (the console is the page, a full-width accent Play button holds focus, "Add another console" is a quiet
      link), the card grid at two or more. Breakpoints in effective pixels are in, and page content is capped
      at 1400 and centred.
  - **Action labels took the player's verb at the same time**, because a hero saying "Play" beside a grid
    saying "Wake & connect" is incoherent: `Play` / `Wake & play`, and `Can't reach it` for offline.
    "Connect" stays in diagnostics, where it is the name of a mechanism rather than an invitation.
  - **Deviation from the plan, deliberate:** it calls for a separate `WelcomePage` at zero consoles. The
    existing empty state already renders that content in place, so a page plus navigation buys identical
    pixels. Worth building when first-run account setup lands and Welcome has something of its own to do.

### Fixed — the "slider focus trap" was a tooltip (2026-08-06)
- [x] **Root cause: `FocusPilot.DirectionalRoot` treated every open `Popup` as the focus search root.** A
      tooltip is a popup, and so is the dimmed smoke layer behind a `ContentDialog`. Neither holds anything
      focusable. Whenever one was the topmost popup, `FindNextElement` searched inside it, found no candidate,
      and directional navigation silently did nothing.
  - **It presented as four unrelated bugs**, which is why it took a hardware pass to see: "the pad stops
    responding sometimes", "focus dies after closing the Y menu", "Back removes the dimming but leaves the
    dialog", and "Up/Down will not leave the bitrate slider". The slider was simply where a user sits still
    long enough for a tooltip to appear.
  - **Two fixes were written against the wrong cause and neither worked** — `IsFocusEngagementEnabled="False"`
    and a tab-order fallback. In hindsight that was the evidence: a correct fix for the stated cause failing
    twice meant the cause was wrong. The engagement setter is kept on its own merits (the state really is
    unreachable in a desktop app) but is documented as never having been observed to help.
  - **Fixed** by requiring a popup to hold something focusable before it counts — the property
    actually being relied on, rather than a type check that would need a list of popup types kept current.
  - **The residual was a second, unrelated cause, and it is now measured rather than theorised.** With the
    popup bug fixed, focus left the sliders but SKIPPED a row. A geometry probe gave the answer outright:
    `BitrateSlider` occupies x 776-996 (its value readout sits to its right and pushes it left) while every
    `ToggleSwitch` occupies x 1008-1080. **Twelve pixels apart, zero overlap** — and XY focus's default rule
    only considers candidates that overlap on the perpendicular axis, so a slider and a toggle are invisible
    to each other. The ComboBoxes (x >= 863) do overlap, which is why focus stepped straight to them.
  - **Fixed with `XYFocusUp/DownNavigationStrategy="RectilinearDistance"` on the settings page.** Projection
    was tried first and could not have worked — it projects the focused element's bounds along the direction
    of travel, which still requires perpendicular overlap. RectilinearDistance ranks by distance and needs
    none.
  - **Three wrong guesses preceded one measurement.** Focus engagement, then an ancestor-walk heuristic, then
    Projection. The range-control special case in `FocusPilot.MoveFocus` written for those theories has been
    removed: instrumented against real markup, the primary search always returned a candidate, so it never
    fired once. Lesson recorded because it repeated: when a plausible fix fails twice, the premise is what to
    re-examine, not the implementation.


### Stage C — step 2 landed, step 3 is gated (2026-08-06)
**Standing note for this branch:** every UI change since 2026-08-06 has been verified by launching the app and
driving it through UI Automation, not by building it. That habit exists because building cleanly and passing
182 tests said nothing about a Settings page that crashed on open, and because the accessibility work above
crashed on launch the first time and was caught the same way.
- [x] **Step 1** (search-root fix, activation chain, `FocusState.Keyboard`) — landed earlier.
- [x] **Step 2** — `NavIntentReader` and `InputModeTracker` extracted into `Ripcord.Core.Input`, pure and
      clock-injected like `ExitGestureDetector`. 23 tests. `MainWindow` keeps only the half that needs a
      window (moving focus).
  - `InputModeTracker` **has no consumer yet** — `InputHintBar` (step 9) is the one that reads it. Tested
      groundwork, not live behaviour, and its feed points are deliberately unwired: a tracker that is fed but
      never read would look verified while nothing could show a wrong feed.
  - **Pad navigation is not verified end-to-end.** The extracted logic is unit-tested and the app launches,
      but driving the actual pad needs a physical controller. On the hardware list with everything else.
- [x] **Step 3 — `InputRouter` + `InputScope` landed 2026-08-06.** One reader of the pad for the whole app;
      `MainWindow._navControllerSource` and `SessionPage.IsCapturingInput` both deleted. `InputScopeStack` is
      portable and has 9 tests. The remap is applied once, in the router, and the session's
      `MergedInputSource` now takes an explicitly empty one. The two things this claims to fix are exactly the two that require
        hardware: **a DualSense working in the menus** (it previously worked only in a stream, because the
        chrome read GameInput only), and **the mid-session dialog** — open the disconnect prompt with a
        controller and check the pad reaches its buttons, that the combo that opened it does not stay held
        inside the game, and that "Stay connected" resumes forwarding.
  - Also worth a glance on that pass: menu navigation generally (auto-repeat, deadzone, B-to-go-back), since
    the whole path from frame to focus was rewired.
- [x] **Step 4 landed 2026-08-06** — `FocusPilot` (focus mechanics out of `MainWindow`, ~200 lines), right-stick
      scroll, and North → context menu.
  - Making North work required the cards to genuinely carry a `ContextFlyout`. They never had one: the XAML
    comment claimed the overflow flyout "is also wired as the card's ContextFlyout" and it was not — they
    handled `ContextRequested` and built a menu by hand. WinUI cannot raise `ContextRequested`
    programmatically, so rather than a pad-shaped second path the cards now carry a real flyout and
    right-click, the menu key and North open the same object.

### Stage A — what landed, and what still needs a console (2026-08-06)
All eleven steps of Stage A are in. Two new assemblies and one moved folder:
- **`Ripcord.Presentation`** (`net10.0`) — view-models, flow state machines, and everything deciding what a
  surface *says*. `PresentationPortabilityTests` reflects over its referenced assemblies and fails on anything
  matching `Microsoft.UI*` / `Microsoft.Windows*` / `WinRT*` / `Avalonia*` / `Gtk*`.
- **`Ripcord.Presentation.Halyard`** (`net10.0`) — the PlayStation implementations of its seams, plus
  `HalyardAppServices.Create`: the one place a front end names Halyard.
- **`Ripcord.Core/Consoles/`** — `PairedConsole`, `IPairedConsoleStore` and the credential store moved down
  from the WinUI project. `PairedConsole.ToPairingRecord` is now an extension on the Halyard side, so the
  record is a plain credential holder and the dependency arrow points the right way.
**181 tests**, none of which need a console, a GPU or a window. They cover things that previously could only
be observed by connecting to real hardware and watching: the sampling-interval floor that stops a delayed
timer tick reporting 1610 fps and permanently poisoning a peak that never decays; "resolution pending" vs
"resolution unknown"; that only `IsHdrOutput` lights the HDR pill; that switching off HEVC clears the HDR
request rather than leaving it set-but-disabled to reappear later.
**Six seams**, all following the same rule — a device gets an interface, plain data gets a parameter:
`IConsoleScanner`, `IConsoleRegistrar`, `IConsoleReachabilityProbe`, `IConsoleWakeCoordinator`,
`IVideoPipelineStats`, `IVideoCapabilitiesProbe`, plus `IShellNavigator` for window-level operations.
Two flags deleted rather than moved: `SettingsPage._loading` (fourteen handlers checked it) and the
`AddConsolePage` scan-generation guard that was being evaluated at the wrong time.
- [x] **Live pass done (2026-08-06).** All three verified against real hardware:
  - **Step 8** — an existing pairing still loads and streams, so moving credential-store construction into the
    composition root did not disturb the credential path.
  - **Step 9** — the diagnostics overlay reads correctly through a live session.
  - **Step 10** — settings save and reopen correctly, so the rewritten save path and the code-attached
    handlers behave as the unit tests claimed.

### Fixed en route — a reachable crash in the add-console flow (2026-08-05)
Worth recording because of *how* it was found rather than what it was. `AddConsolePage.StartScan`'s `finally`
disposed its `CancellationTokenSource` but left `_scanCts` referencing it, so once a scan had run to completion
the next `CancelScan()` called `Cancel()` on a disposed source — which throws `ObjectDisposedException`. The path
was ordinary, not exotic: let the 4-second scan finish, then click a discovered console, and
`OnDiscoveredConsoleClick` → `GoToLinkStep` → `CancelScan` faulted out of a synchronous click handler. It
survived because the *other* ordering — clicking while the scan is still running — cancels a live source and is
fine, and because nothing could exercise the flow without a console on the LAN.
Fixed in `AddConsoleFlow` (release ownership before disposing) and caught by a unit test the moment the state
machine became testable, which is the argument for Stage A in one bug.

### Console-card layout — fixed cell height is a standing constraint (noted 2026-08-05)
The clipped "Played 31 min ago" line is fixed (the container's 12px gutter margin was being subtracted from
`ItemsWrapGrid.ItemHeight`, so the card was 164 tall while its rows were sized for 176), and the slack now
lives in an empty row so a shortfall closes up whitespace instead of chopping text. Two limits remain, both
for Stage B's card redesign rather than a patch:

### Track A — Live-test the streaming-quality work
> **Plan written 2026-08-02:** `captures/console_session_plan.md` (dirty room) batches every remaining
> hardware-gated item across this track and Tracks B/C into one trip, in a fixed order — Phase 1 needs the
> console *asleep*, a state you get once per session, so the ordering is load-bearing rather than advisory.
- [x] ~~Senkusha RTT + MTU probes~~ — implemented and byte-verified against two captures (Track C).

### Track B — Phase 1 production-path gaps
The stack connects and streams; these are the bits that still lean on dev-machine scaffolding.
*(Already done, previously listed here: session factory (`HalyardSessionFactory`), DPAPI-backed credential
store (`PairedConsoleStore`, `dpapi:` prefix), pairing UX (`PairConsoleDialog` → live registration), first
live end-to-end connect.)*
- [x] ~~**Production secrets path — the one real shippability blocker.**~~ — **RESOLVED 2026-07-30.** The
      generic protocol constants (~2 KB: both KDF tables, the four field context keys, the registration key
      table, the material wrap table, the selector offset) are now bundled with the build as an embedded JSON
      resource, read by `HalyardInteropConstants` as **step 4** of the existing resolution chain. A plain clone
      can pair and stream.
  - **Precedence is unchanged for developers:** env override → platform config dir → dev-tree fixture →
    bundle. Local material still wins, so you cannot silently end up testing against the bundle. Every loader
    reports which path won via its `source` string, and that surfaces in the diagnostics overlay — read it
    rather than assuming.
  - **Only generic values ship.** Nothing per-console or per-account: no registration key, pairing record,
    session key, device id, or account id. `BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial`
    enforces this by scanning the embedded JSON for those field names, so a future regeneration that sweeps in
    the vector half fails the build rather than shipping.
  - **An inert build is one flag, not a refactor:** `-p:BundleInteropConstants=false` omits the resource;
    `IsBundled` goes false, both accessors return null, and the session degrades to the passthrough stub with a
    clean message. Verified by effect rather than absolute count, since totals drift with suite growth and host: the flag turns exactly the **4 `BundledInteropConstantsTests` inert** and the suite stays green (measured 2026-08-02 on `main`: 424/5 default → 420/9 with the flag; the earlier "413 pass / 4 skip" was a correct snapshot of a smaller suite, not an error). Keep this working — it is the whole reason the
    constants are data behind a provider rather than inlined literals.
  - Note the flip side of shipping them: they are now in git history permanently, so the decision is not
    reversible in the "make them disappear" sense. Forward reversibility (produce a clean build) is what the
    flag buys.
  - The first-run **extractor** idea is therefore dropped rather than deferred — it existed to avoid shipping
    these values. If the Linux/Steam Deck direction ever needs a per-user extraction path instead, the seam is
    still there and the bundle is just one more provider behind it.
- [x] ~~**Console wake.**~~ — **LAN wake SETTLED, IMPLEMENTED, AND VERIFIED END-TO-END 2026-08-03 [V].** A real console in rest mode woke from our own client and reached the connect flow (the stream then blocks on the separate sign-in gap below, not on wake). The two fixes that made it work — broadcast SRCH probe, source port 9303 — were found by diffing our packets against cap49; that the console accepted the wake also confirms the credential derived from the stored registkey is correct. It is a purely local
      exchange; the cloud is not involved. Proven by `cap49`, captured with the console's internet blocked at
      the router, which is what makes the "no cloud" claim evidence rather than assertion.
  - **The protocol** (all UDP 9302): `SRCH` → `620 Server Standby` → a plaintext `WAKEUP * HTTP/1.1`
    datagram → console wakes → `SRCH` → `200 Ok`. The one per-console value, `user-credential`, is the
    registration key as a base-16 integer in decimal — derivable from the `HalyardPairingRecord` we already
    persist, so no new pairing state. `HalyardWakeClient` implements it; `WakeClientTests` pins the payload
    to the captured bytes.
  - **This corrects the entry's own three-part claim from 2026-08-02, all of which turned out wrong.** LAN
    wake was said not to exist in the tree (it does now, and it was always in the protocol); it was said to
    ride the advertised `host-request-port` (it goes to 9302, so the 987-vs-997 question is moot); and the
    whole item was filed behind the OAuth/cloud decision (it needs neither). The lesson is the recurring one
    this file keeps re-learning: a "does not exist / needs X first" note is only as good as the capture
    behind it, and here there was none.
  - **`620` came good.** The standby status code, removed on 2026-08-02 as untraceable, is in `cap49`
    verbatim. Right all along, now sourced — `HalyardSearchClient` records the round trip.
  - **Still open — the wiring, and the reverse.** (a) The connect path should offer to wake a `620`/standby
    console and then poll `SRCH` until `200` before proceeding; `IsAwake` is detected today but surfaced only
    in `PairConsoleDialog`, not acted on at connect. (b) The **awake → rest mode** command is not yet
    captured — in `cap49` it went over the encrypted Takion channel after the plaintext TCP control channel
    closed. That is the remaining piece, and it needs a capture with that session's stream keys, not a
    desk-derivable one.

- [x] ~~**Console user sign-in passcode.**~~ — **DERIVED, IMPLEMENTED, AND VERIFIED LIVE 2026-08-03.** A locked console now prompts for the passcode and signs in; wrong-passcode retry (cap51) and the per-attempt counter both confirmed working against real hardware.
      Distinct from the pairing PIN: this is the PS5 *user account* login passcode (4 digits), demanded when a
      session starts against a signed-out / locked user profile. The vendor client prompts for it; we have no
      flow, so an awake-but-locked console pairs and wakes but never streams.
  - **Mechanism located [V] from cap50 (2026-08-03); only the PIN encoding is still [X].** The sign-in is a
    **binary control-channel exchange** on the TCP 9295 connection that stays open after `/sess/ctrl`, not
    HTTP and not Takion. For a locked user the console pushes a **`0x0004` sign-in prompt**; the vendor
    replies with a **`0x8004`** frame carrying the encrypted PIN (4-byte payload); the console then sends a
    **`0x0033` session-ready** frame and only *then* accepts the Takion `INIT`. Frame format is
    `[u32 payload_len][u16 type][u16 flags][payload]`.
  - **Ripcord's two defects, confirmed on the wire.** (a) It has **no handler for `0x0004`** — it answers the
    `0x00fe` heartbeats and nothing else, so it never submits a PIN. (b) It fires the Takion `INIT` ~0.08 s
    after `/sess/ctrl`, *before* the sign-in handshake; the vendor waited ~6.5 s. The console sends Ripcord
    the identical prompt and Ripcord ignores it, so it never unlocks and every `INIT` is dropped.
  - **To build it:** handle the `0x0004` prompt (surface a PIN entry in the app), submit `0x8004` with the
    encoded PIN, and gate the Takion bring-up on the `0x0033` session-ready rather than firing immediately.
  - **PIN encoding DERIVED [V] 2026-08-03.** the `0x8004` payload decrypts to the four ASCII digits that were typed, so the
    transform is the **same §2.1 control-field cipher as RP-Auth**: plaintext = the 4 ASCII digits,
    ciphertext = `AES-128-CFB(control_key, field_iv)`, context key = selectorOne, **counter = 5**. The counter
    is not new state — it is the existing per-connection field counter continuing past the five `/sess/ctrl`
    fields (RP-Auth=0 … RP-StreamingType=4) onto the same kept-open TCP connection. So the crypto is entirely
    reuse of the working control-field path; only the framing (`0x8004`) and the trigger (`0x0004`) are new.
  - **Nothing left to capture — this is now purely an implementation task**, spanning three layers: the
    binary control-channel codec (parse `0x0004`, emit `0x8004`, recognise `0x0033`), a PIN-entry UI in the
    app, and gating the Takion bring-up on `0x0033` instead of firing immediately.
  - **Superseded, recorded honestly:** an earlier pass this day concluded "the passcode is not in cap50." That
    was wrong — it examined HTTP and Takion but not the binary control channel, which carries it. Lesson:
    enumerate every transport channel before declaring a negative.
- [x] ~~**The OAuth decision — split into three, because it was never one decision.**~~ — **THE DECISION IS
      CLOSED (2026-08-07);** see the status blocks inside. Items (2) WAN play and (3) cloud wake are tracked by
      their own notes below — (3) is done, (2) is blocked on the push channel and STUN rather than on policy,
      and (1) account-SSO pairing remains genuinely unbuilt. Kept open-form below because the sub-items still
      carry live status. It originally sat unresolved as
      a single "decide scope" item, which is probably why it never got decided: LAN-only play needs none of
      it, so the item reads as skippable, while three quite different capabilities hide behind it. They are
      independently choosable:
  1. **Account-SSO ("online") pairing** — pair without walking to the console for a PIN. This is a *pure
     PSN-cloud flow* (tier 1), confirmed in `ps5-network-architecture.md` across a reconnect and a
     brand-new device's first connection. We implement only the PIN "Link Device" path today; that same
     doc calls the split "the real remaining gap, not 'first-time pairing' in general."

     > **✅ SOLVED & BUILT (2026-08-31). Remaining: the presentation/UX wiring only.** The blocker below —
     > "the transport key derives from an input we did not capture" — is resolved: **the seed is not derived,
     > the console DELIVERS it.** The client sends ephemeral `data1`/`data2` (field-cipher key/material) in the
     > cloud `commands` call; the console field-encrypts the registration seed with them and returns it as
     > `customData1` (double-base64) on the push channel; the client decrypts it (`HalyardControlFieldCrypto` +
     > bundled `contextKey`) → seed → `key' = seed XOR registrationTable[selector]`. Verified byte-exact against
     > the vendor client's own `session+0x10` on multiple fresh pairings; see the dirty-room
     > `nopin_response_key_callchain.md` (UPDATE 27–38) for the full trail and `ps5-cloud-session-api.md` /
     > `ps5-session-establishment.md` for the spec. This retro-corrects the falsifications below: they were
     > right that `data1/2` don't *directly* key the response — the missing layer was `customData1`.
     >
     > **Built on `main`** (all tested without hardware): `HalyardAccountSeedDelivery`
     > (the seed crypto), the cipher's account path (`BuildAccountRequest`), `AccountSeed` threaded through the
     > registration client, the push channel surfacing `customData1`, and `HalyardAccountPairing` — the coordinator
     > that runs the whole flow (session → command → `customData1` → seed → register → pairing record) with an
     > end-to-end round-trip test.
     >
     > **✅ PRESENTATION/UX WIRING LANDED 2026-09-03. Remaining: a live run.** Account pairing is now an app
     > action, from the graph to the button:
     > - **`IAccountConsolePairing`** (`Ripcord.Presentation/Pairing/`) — the portable seam, deliberately
     >   *alongside* `IConsoleRegistrar` rather than folded into it: the code route is a purely local exchange
     >   that has to keep working in a build with no account credential, and one interface would have given it a
     >   cloud dependency it does not have. `CheckAvailability` answers about the **build**, not the current user
     >   (whether someone is signed in and whether the account lists this console both change while a flow is
     >   open, and the flow already knows them), so it can be asked once, synchronously, before the affordance is
     >   offered. `UnavailableAccountPairing` is the null object, mirroring `UnavailableAccountSession`.
     > - **`HalyardAccountConsolePairing`** (`Ripcord.Presentation.Halyard/Pairing/`) — resolves the crypto,
     >   gathers the token + push-server + socket, and hands `HalyardAccountPairing` a fresh `HalyardPushChannel`
     >   per attempt (the socket is single-use; pairing is a thing users retry). Cloud refusals come back as a
     >   result with a reason, not an exception.
     > - **The context key travels with the cipher.** `IHalyardRegistrationCipherResolver.Resolve` now returns a
     >   `HalyardRegistrationCipherResolution(Cipher, Source, ContextKey)` instead of an `out string`. The account
     >   route needs the field context key to decrypt the delivered seed, and resolving it through a second path
     >   would let a developer's fixture serve the registration while the bundle served the seed decrypt — which
     >   surfaces only as a seed that decrypts to noise, deep inside a pairing. Pinned by
     >   `ContextKey_ComesFromWhicheverSourceWon`.
     > - **One gateway, built lazily, feeds both account seams.** `HalyardAppServices` used to build the gateway
     >   inside `BuildAccountSession`; it now memoises `TryBuildGateway` so signing in and account pairing share
     >   one, and a caller substituting both still pays for neither (`HalyardClientDeviceId` throws on a host that
     >   cannot supply a device id, and a build that will never sign in must not fail to start over it).
     > - **`AddConsoleFlow` has two routes over one runner.** `PairAsync` (code) and `PairWithAccountAsync`
     >   (account) share `RunPairingAsync` — supersede, own the timeout, check on return that this attempt is
     >   still current — because every part of that dance was a bug once and a second copy is a second place to
     >   regress. New state: `AccountPairingOffered`, `CanPairWithAccount`, `AccountPairingNote`, `PairingHint`,
     >   and an `AccountPairingTimeout` (75 s: WebSocket upgrade → session → command → the console generating and
     >   publishing the seed → the same registration POST).
     > - **The WinUI link step** shows the note above the console instructions and puts "Pair with my account" in
     >   the footer's secondary slot — offered, not urged, and one accent button per step. Focus seeds the account
     >   button instead of the code box when that route is the usable one.
     > - **`ProtocolLab accountpair <ip> <duid> [ps4|ps5]`** drives it through the very object the app composes,
     >   not a harness-local rebuild of the same wiring, so a live run verifies the shipping path.
     >
     > **FIRST LIVE RUNS 2026-09-03 — the cloud half works; the registration transport does not.** Driven with
     > `ProtocolLab accountpair <console-ip> <duid>` against `PS5-<redacted>`, signed in as a real account. Reproduced
     > twice, identically:
     >
     > ```
     > · push channel connected
     > « rps:members:created      [REMOTE_PLAY]   ← us
     > · session created
     > · connect command sent (data1/data2)
     > « rps:members:created      [PROSPERO]      ← THE CONSOLE JOINS (~0.75 s)
     > « rps:customData1:updated  [PROSPERO]      ← the encrypted seed arrives
     > · registration seed recovered from customData1
     > « sessionMessage [PROSPERO] action=OFFER reqId=3 error=0   ← it offers candidates
     > · registration failed: HTTP 403, RP-Application-Reason 80108bff
     > ```
     >
     > So **everything the 2026-08-31 solve claimed is now confirmed live**, not merely against the vendor's
     > captured values: the command reaches the console, the console joins, generates the seed, field-encrypts
     > it with our `data1`/`data2`, and we recover it.
     >
     > **The blocker is the transport, and it is the one piece never built.** `80108bff` is the console's
     > generic refusal. The account route's control plane is on **9303 over reliable UDP**; we POST over **TCP
     > 9295**. There is no 9303 control transport in the tree. See the RESUME block.
     >
     > **A verification gap found and closed — the seed tests proved nothing.** Every seed-delivery test was a
     > round trip through our own code (`SealSeed` → `RecoverSeed`), sharing the key, material, counter *and*
     > context key — so it would have passed with any of the four wrong, and the "verified byte-exact" claim
     > rested entirely on ad-hoc dirty-room analysis with nothing in the suite holding it. The cap107 tuple in
     > `nopin_response_key_callchain.md` is a complete ground truth, so it is now a fixture (`SeedVectors` in
     > `registration_crypto_vectors.json`) and a `SkippableFact`
     > (`LiveRegistrationVectorTests.AccountSeedDelivery_ReproducesTheConsolesDeliveredSeed`): our `RecoverSeed`
     > reproduces the vendor's own seed byte-exactly from the console's captured `customData1`. It also pins a
     > detail worth knowing — **the ciphertext is 17 bytes for a 16-byte seed** in all 16 captured values, so
     > "take the first 16" is tested rather than assumed.
     >
     > **Fixed en route, each from capture evidence:**
     > - **The coordinator never left its cloud session.** The vendor's last push frame is a `members:deleted`
     >   for itself; we created a session per attempt and walked away. `HalyardAccountPairing` now leaves on
     >   every path, before the push channel goes down, and the frame log confirms it.
     > - **`accountId` was a JSON string; every capture shows a bare number.** The command's `initialParams` is
     >   now composed to match the captures exactly.
     > - **`User-Agent: RpNetHttpUtilImpl`** was missing; the vendor sends it on every cloud REST call.
     > - **`RP-Application-Reason` was discarded**, so every refusal read alike. `TrySplitResponse` now reports
     >   it — that is what turned a bare "403" into "403, generic refusal".
     >
     > **Falsified, so nobody re-runs them:**
     > - *"Leaked sessions are blocking it"* — no. PSN had already expired the sessions from earlier attempts
     >   (readback: `(gone)`). The leave fix is correct hygiene, not the cause.
     > - *"The `commands` field set is wrong / needs `data3`, `supportCmd`, `protocolVer`"* — no. cap96, cap97
     >   and cap107 all show exactly the six fields we send. The earlier trim was right, and `data3` does not
     >   appear in this call at all.
     >
     > **[X] Open, behavioural — a console paired seconds after waking refuses.** The two runs that paired
     > immediately after a cloud wake got `action=TERMINATE, error=0` and **no join**; the two that let it
     > settle ~45 s both joined and delivered the seed. Four data points: a strong correlation, not proof. If it
     > holds, the app's account-pairing path needs a readiness wait rather than firing as soon as `SRCH` says
     > awake — and note `TERMINATE` carried `reqId=4`, i.e. the console's counter was mid-conversation, which
     > is unexplained.
     >
     > **[X] Dead code found — `HalyardCloudClient.GetSessionsAsync` can never work.** A bare
     > `GET /remotePlaySessions` is a 400: the readback requires `X-PSN-SESSION-MANAGER-SESSION-IDS`, so there
     > is no enumeration endpoint and a session whose id is lost cannot be found or left. `ProtocolLab sessions`
     > is therefore id-based. Either give `GetSessionsAsync` the header and ids, or delete it.
     >
     > **Fixed en route — the cloud console list was cancelled by the user's own click.** The account list lookup
     > rode the *scan's* cancellation token, and picking a console cancels the scan, so a cloud that had not
     > answered by the moment the user clicked never answered at all. Invisible while the id only bought a remote
     > wake (it was filed as optional enrichment); not invisible now, because whether the account knows this
     > console is what decides whether the route is offered. The lookup now owns its own `_cloudCts`, superseded
     > by the next lookup and cancelled only by the flow's disposal, and its result is applied **through**
     > `Mutate` so a list landing while the user is on the link step recomposes the state instead of sitting in a
     > field nobody re-reads. Both halves are pinned
     > (`AccountPairing_CloudListArrivingAfterTheUserHasMovedOn_StillEnablesIt`).
     >
     > **What is left is a live run**, and it is the genuine unknown: the seed delivery was verified byte-exact
     > against the *vendor* client's own value, never by Ripcord completing a pair. Nothing below this banner
     > still blocks — it is preserved as the historical trail of how the key source was run down.
     - **Scoped 2026-08-07 from cap4; see spec §2.0.1 for the derivation.** It is a smaller job than it
       looked, and blocked on one specific unknown rather than on breadth:
       - **Body layout needs no work at all.** The account-route request body is 587 bytes — the *same*
         480-byte context plus the *same* 107-byte `Client-Type`/`Np-AccountId` field our PIN-route builder
         already produces. Verified by recomputation, not assumed from the spec's prose.
       - **Transport is derivable and mostly derived**: HTTP/1.1 over a reliable-UDP framing on 9303
         (2-byte big-endian header, top two bits set, low 14 bits = chunk length; two 16-bit connection tags;
         chunks concatenate within a datagram; 5-packet INIT/COOKIE prelude). Note the whole control plane
         moves to 9303 on this route — `init` and `ctrl` too, not just `rgst` — so this is a second transport
         behind the existing seam, not a one-off for pairing.
       - **THE blocker: the response key schedule [X].** Three hypotheses now falsified, each with a test:
         - *"passcode 0 / any pinless table entry"* — no. IV-free CFB block oracle over all 32 entries, both
           families, zero hits (`NoPinRegistrationVectorTests`, from cap63).
         - *"`RP-Hmac` authorises it"* — no. The older client (cap63) omits the header entirely and still
           pairs; it was a newer-client addition in cap4.
         - *"the cloud `commands` `data1/2` key it"* — **no, falsified 2026-08-07 from cap64/cap65**, the
           correlation capture that was finally taken (Fiddler saz + raw pcapng together, so both halves are
           readable for one event — it turned out to be practical after all). 24 derivations of `data1/2`
           (direct, reversed, table-fold, SHA-256, HMAC-SHA256) fail to decrypt the PS5 response to the
           captured registkey (`NoPinKeyCorrelationTests`, oracle self-validated on a known PIN key); and the
           PS4 (cap65) paired over the same 9303 route with **no `commands` call at all**, so it never had
           `data1/2` in the first place. The "data1/2/3" values are now falsified in *both* roles they were
           ever proposed for (session crypto, and now registration auth).
       - **Where it goes next.** Because the PS4 had no cloud trigger, the key source must be common to both
         families' flows — so look **in-band** (the 9303 INIT `06` packets carry two ~20-byte high-entropy
         blobs in every account-route handshake) or at **account/session material** (OAuth token,
         session-manager rendezvous), not the commands payload. Correlate those against the transport key; and
         on the binary side, finish the request-encrypt `Key1` localisation the RE log already started.
       - **Companion sweep run 2026-08-07 — NEGATIVE (`NoPinCompanionKeySweepTests`).** The natural remaining
         hypothesis, `K = f(companion, …)`, was swept broadly: the companion (and a second companion value) as
         key or message, combined with the context, recovered material, registkey, account id, and the selected
         table entry, through HMAC-SHA1/256, SHA-256, AES-ECB, AES-CMAC, direct/reversed, table-XOR and
         passcode-style fold. No candidate decrypts cap64's response (oracle self-validated on a known key, so
         the negative is real). **So all three input candidates we hold — table entries, cloud `data1/2`, and
         the companion — are ruled out.** The no-PIN transport key derives from an input we did not capture. The
         verification half is fully built (oracle + fixture + sweep harness); what is missing is the *input*.
         Getting it needs a fresh fully-instrumented first-time no-PIN pairing capture (TLS on every cloud call
         *and* 9303 packets, correlated) or binary RE of the client's key derivation — not more function-search
         over what we have. The
         transport and request codec are done — only the key schedule blocks a working no-PIN pair.
  2. **Remote/WAN play** — tiers 1+2. Worth knowing before scoping: the WAN relay is a *real transparent
     media relay*, not just signaling — when the LAN path is unavailable the client re-addresses its
     ordinary tier-3 protocol at the relay, same ports, same framing (`ps5-wan-relay.md`). So this is
     closer to reachable than "implement a whole new transport" suggests.
     - **Push channel DECODED 2026-08-07 (cap66/cap67); one transport blocker left, and it is buildable, not
       RE.** The reachable half was already runnable via `ProtocolLab signaling <duid>` (session → command →
       OFFER). The inbound half is now solved:
       - **The console's candidates arrive over the push WebSocket** — captured by forcing the vendor client
         through mitmproxy with Proxifier (the push connection bypasses the system proxy and the client
         ignores `SSLKEYLOGFILE`, so both easier routes were dead), and excluding the cert-pinned
         `auth.np.ac.playstation.net` with `--ignore-hosts`. Parsed by `HalyardSignalingMessage.TryParse`,
         validated against the real frames by `LiveSignalingVectorTests`. The exchange is symmetric (each side
         OFFERs its own candidates; no ANSWER); the console offers `STATIC`/`LOCAL` (PS5) or
         `STUN`/`STATIC`/`LOCAL` (PS4) on port 9303, with populated `skey`/`localHashedId`.
       - ~~**Remaining transport blocker: a STUN client**~~ **BUILT 2026-08-07** — `Ripcord.Core.Net.Stun`
         (`StunMessage`/`StunClient`), validated against the RFC 5769 vector and a loopback fake server.
         Gathers the reflexive endpoint on a caller-supplied socket (the binding is port-specific). Note the
         vendor's own STUN is authenticated (classic STUN + `USERNAME`/`MESSAGE-INTEGRITY`, its relay tier) —
         we use public servers for reflexive gathering instead, which is server-agnostic.
       - ~~**push-WebSocket client**~~ **BUILT 2026-08-07** — `Ripcord.Core.Net.WebSockets`
         (`IWebSocketChannel`/`ClientWebSocketChannel`, generic) + `Ripcord.Cloud.Halyard.HalyardPushChannel`
         (connects `wss://<fqdn>/np/pushNotification`, pumps frames through `HalyardSignalingMessage.TryParse`,
         surfaces the console's OFFER). Plus `GetPushServerAsync` (the serveraddr lookup) and the
         `HalyardPushServerInfo` model. Dispatch loop validated against the **real captured frames** replayed
         through the channel (`HalyardPushChannelTests` / `LiveSignalingVectorTests`). **[X]** the exact upgrade
         auth header is unconfirmed (bearer token is the scope-implied default; captures show the connect but
         not its headers) — first thing to check if the upgrade is rejected live.
       - ~~**Now purely orchestration**~~ **ORCHESTRATOR BUILT 2026-08-07** — `HalyardWanRendezvous`
         (`Ripcord.Cloud.Halyard.Rendezvous`) sequences the whole dance: create session → start push channel →
         wake command → gather STUN on the media socket → OFFER (reflexive + LAN, re-sent on an interval) →
         return the console's candidates once its OFFER arrives over push. Collaborators are behind seams
         (`IHalyardSignalingClient`, `IReflexiveGatherer`) so the sequencing is unit-tested against fakes
         (`HalyardWanRendezvousTests`): listens-before-triggering, re-offer-while-waiting, timeout, teardown,
         cancellation. `HalyardWanConnection` owns the session + push channel and leaves the session on dispose.
         Runnable against hardware via `ProtocolLab wanconnect <duid>`.
       - **Live-run findings (2026-08-07):** two bugs found and fixed against real hardware. (1) We created the
         session before the push WebSocket connected; PSN binds the session's message channel to the live push
         connection at create time, so `sessionMessage` 404'd — fixed by awaiting `HalyardPushChannel.Connected`
         before create. (2) The push upgrade 400'd because it lacked the required `Sec-WebSocket-Protocol:
         np-pushpacket` subprotocol and the `X-PSN-*` header set — captured from a vendor handshake (cap68) and
         now sent verbatim (`HalyardPushHeaders`). Bearer auth on the upgrade is **[C] confirmed**.
       - **Live bring-up completed to the console-join boundary (2026-08-07).** Every cloud step is now
         confirmed working against real hardware: push upgrade (needed `Sec-WebSocket-Protocol: np-pushpacket`
         + the `X-PSN-*` header set, and bearer auth — all from cap68), session create (needs the push
         connected first), the wake command (the console powers on; command trimmed to the vendor's exact 6
         `initialParams` fields), STUN gather, and the OFFER POST. The session-manager readback needs
         `X-PSN-SESSION-MANAGER-SESSION-IDS`.
       - **THE remaining blocker [X] — the console will not JOIN the session our client creates.** Confirmed
         over a 120 s / 77-offer run: the session exists (readback 200) but stays at 1 member (us); the console
         wakes but never becomes a member, so every `sessionMessage` POST 404s (you cannot message a non-member).
         Not a timing issue — it never starts joining. This is an **authorization gate**: the console joins for a
         registered/authorized remote-play client (the official app), and our harness is not one. Prime
         suspects, both tying into the unsolved account-registration work: the command's `data1`/`data2` (we
         send random bytes; they may be the join-authorization token) or a required remote-play device
         registration our client lacks.
       - **Diagnosed 2026-08-07 to the root, via live tests.** The official app connects off-LAN fine from the
         same network, so it is our client, not config. Then two hypotheses were tested and **both falsified**:
         - `data1`/`data2` are **ephemeral nonces** — they differ every connect (cap64 vs session6, same
           console) and are not derivable from the companion/registkey (broad HMAC/SHA sweep, validated across
           two captures). Not the join gate.
         - **Device id is not the gate either.** The official app's `duid` device-id tail is NOT
           MachineGuid-derived (confirmed same-machine: official `<duid>` vs ours `<duid>`), but
           signing in with the official app's exact device id (via a `RIPCORD_DEVICE_ID` override on the
           harness) still produced `console joined: False`.
       - **CONCLUSION: WAN play is blocked on the account-based (no-PIN) device REGISTRATION — the same
         unsolved `[X]` as the cap63/64/65 key-schedule work, now confirmed from the connect side.** The
         console authorizes the join only for a device holding a real remote-play registration (mirrored into
         the account's cloud device registry at pairing); our client has none, and neither the OAuth device id
         nor `data1`/`data2` substitute for it. Everything else in the WAN pipeline is built and proven live
         end-to-end (push upgrade, session create, wake, STUN, OFFER) — the registration is the one remaining
         brick, and it is genuine reverse-engineering, not a code fix.
       - **Harness aids left in place:** `wanconnect <duid> [timeoutSeconds]` with live `· ` diagnostics and
         readback (session present / console joined), and `RIPCORD_DEVICE_ID` to present a chosen device id.
       - **Send-side gap [X]:** `SendOfferAsync` sends `skey` as zeros and omits `localHashedId`; the console
         populates both. Untested whether the console requires non-zero client values. `localHashedId`'s
         derivation is still unknown — do not fabricate it.
       - ~~**Bonus for the no-PIN thread:** consoles also push a `customData1:updated` with a 16-byte value —
         the right shape for the missing registration key material; capture it during a first-time no-PIN pair.~~
         **CAPTURED AND FALSIFIED 2026-08-20** (cap71/cap72, own VM + own PS5, push channel decoded). Two
         corrections and one live lead:
         - **It is 24 bytes, not 16.** The field is 32 *characters* of **base64**; the "16-byte" reading was
           32 characters taken for hex. Worth noting because the survey test was written with a 16-byte filter
           straight from this note and duly reported nothing while the candidate sat in the frame — a filter
           derived from a hypothesis will confirm it by finding nothing.
         - **It is EPHEMERAL, so it is not registration material.** Two consecutive connects to the same
           already-paired console produced **different** `customData1` values (cap71 vs cap72), with all other
           pairing-scoped fields identical. Same falsification as `data1`/`data2`, by the same method, and
           cheap: it needs two ordinary connects, not a fresh device.
         - **`skey` IS pairing-scoped, and is now the live candidate `[X]`.** The 16-byte non-zero `skey` in
           the console's OFFER is **identical across both sessions** (reqId 1/3 then 5/7), **differs between
           two client devices paired to the same PS5** (VM vs. host machine), and differs again for the PS4.
           So it is stable per (client device, console) pair, which is the shape registration material has —
           and the name "session key" has been quietly implying otherwise. Nothing in `docs/protocol/` claims
           it is per-session, but nothing said it was not.
         - **Not yet evidence either way:** `skey` and derivations of it (reversed, SHA-256, MD5, HMAC over the
           context both ways) were run through `NoPinCompanionKeySweepTests`' block oracle against the cap63
           no-PIN response — **0 hits from 36 candidates**. That negative is *uninformative*, and must not be
           recorded as a falsification: cap63 is a different client device (off-network, older vendor client),
           so its registkey belongs to a different pairing than any `skey` we hold.
         - **cap73 (2026-08-20) took that capture, and it REFRAMES THE WHOLE QUESTION.** Two WAN connects from
           the VM with the push channel decoded *and* a pcapng of the console leg (console reached at a public
           address on 9303/9297 — incidentally the first first-party capture of the vendor client's WAN path
           end to end, ~20 MB streamed).
           - **`RP-Registkey` is NOT device-scoped.** The VM presents the *byte-identical* registkey recorded
             in `nopin_correlation_ps5.json` from **cap64 (2026-08-05, a different client device, fifteen days
             earlier)**. So one registkey serves an (account, console) pair across that account's client
             devices `[X]`.
           - **Which makes it a DISTRIBUTION problem, not a derivation problem.** Every previous attempt —
             table entries, `data1`/`data2`, the companion sweep — searched for a function that *computes* the
             registkey on the client. If the same value is simply issued to each newly-authorised device, there
             is no function to find, which is a coherent explanation for why four independent searches all came
             back empty. It also fits the 2026-07-10 finding directly: the client presents a valid registkey on
             its first packet because it was *given* one, not because it derived one.
           - **`skey` is NOT the companion, and this negative IS conclusive** — unlike the cap63 attempt. The
             companion on file belongs to the same registkey the VM uses, so the material is matched, and no
             `skey` we hold (host or VM, direct/reversed/MD5/SHA-256) equals it. `skey` is a *third*
             pairing-scoped value: device-scoped where the registkey is not.
           - **`customData1` falsified twice more** — a distinct value in each of cap73's two attempts, four
             samples now.
           - **The registkey appears NOWHERE in a normal connect's cloud traffic** (searched `cap73.flows` and
             the push frames for the wire form, the ASCII form, upper case, and base64 of both). So delivery
             happens at a distinct earlier event, not on every connect.
           - **Also settled, from the same capture:** cancelling out of the auto-selected console and
             re-selecting it explicitly produces a **structurally identical** exchange to just accepting the
             auto-selection — same frame sequence, same `skey`, only `sessionId`/`customData1` differ. The UI
             path is not a different protocol flow, so it is not worth varying in future captures.
         - **cap74 (2026-08-20) rules out the cheap version of that capture.** A full **sign-out and fresh
           sign-in** to the vendor app on the already-authorised VM does **not** re-deliver the registkey: no
           new endpoint is hit (the endpoint set is identical to cap73's but for the push host's address and a
           `referenceData/ageGroups` call), the registkey appears nowhere in the cloud traffic, and the client
           presents the same one to the console from local storage. `skey` is unchanged again — **five sessions
           across four capture runs, one value.** So delivery is **once per device**, at first authorisation,
           and the VM has already spent its event. Re-authenticating is not the trigger.
         - **FOUND, and the delivery event is now REPEATABLE without spending the snapshot (2026-08-20).** The
           vendor client's registration store is two files, `setting.cache` + `data.bin` (cap74 folder);
           **deleting them makes the app re-register with PSN on next launch/connect.** So the fresh-device
           capture is no longer a one-shot — delete, capture, repeat.
           - `setting.cache`: 67 bytes, a UTF-8-BOM + base64 line decoding to **48 bytes** of high-entropy
             data. 48 = a 16-byte IV/salt + 32-byte key or GCM tag, or three AES blocks. A wrapping key, most
             likely.
           - `data.bin`: 611 bytes, entropy **7.64 bits/byte** (encrypted, not a plist), **not** a DPAPI blob
             (no `01000000d08c9ddf…` magic) — so it is app-encrypted, keyed by `setting.cache` rather than by
             Windows. `611 − 3` is 16-aligned. The registkey is **not** plaintext in either file, which is
             consistent with everything else: the client stores it wrapped.
           - This means the client-side store is itself an encryption seam worth a note, but it is **not on the
             critical path** — the point of the delete is to re-trigger delivery over the wire, where the
             capture reads it, not to decrypt the store.
         - **cap75 (2026-08-20) forced re-acquisition and is INCONCLUSIVE on the mechanism — for two concrete,
           fixable reasons, both recorded so the next run does not repeat them.** Store deleted, client
           relaunched under the Frida hook (`frida_nopin_registkey.js`). It worked as an instrument: both store
           writes were caught with paths + timestamps, and the store genuinely changed (563/611 bytes of
           `data.bin`, plus the `setting.cache` wrapping key), so deletion does force a real re-acquisition.
           But the delivery was not observed, because:
           1. **The run never connected to the console** — no 9303/9297 leg, no `RP-Registkey` on the wire — so
              there is no ground-truth *new* registkey to search for, and the cloud capture was near-empty
              (mitmproxy was not routing this run).
           2. **The hook's live recogniser was pinned to the OLD registkey**, so a fresh registration — a value
              we do not yet hold — would pass through the TLS/recv/crypto surfaces unflagged. Same class of
              mistake as the 16-byte `customData1` filter: a recogniser keyed to a known value cannot see a new
              one. Fixed 2026-08-20 — the hook now (a) flags on value-independent labels (`registkey`/`rp-key`/
              `regist`/`nickname`/`rp-nonce`) and (b) keeps a size-banded LEDGER of every inbound TLS/recv
              buffer, so a fresh key is recoverable OFFLINE by reading the new `RP-Registkey` from the connect's
              pcapng and grepping the ledger for it.
         - **cap76 (2026-08-20) ANSWERED the distribution-vs-derivation question: the registkey is
           DISTRIBUTED.** Attach-mode Frida (app launched normally so Proxifier routed it), device
           deregistered server-side, then a live re-registration + connect. `decrypt@20d390`'s output buffer is
           the **fully decrypted registration response**, an HTTP-style field block in the clear:
           `AP-Bssid` / `AP-Name: PS5` / `PS5-Mac` / `PS5-RegistKey: <registkey-wire>` / `PS5-Nickname` /
           `RP-KeyType: 2` / `RP-Key: <redacted>…` / `RP-SupportCmd: 136000`. So the console/PSN sends the
           registkey **and** the companion down encrypted, and the client decrypts them here — it does not
           compute them.
           - **This retro-explains every failed derivation search.** `RP-Key` (the companion) is *delivered by*
             this response, so `NoPinCompanionKeySweepTests` was feeding a response OUTPUT back in as a key
             INPUT — it could never have worked. The registkey is not a function of anything the client holds
             beforehand; it is issued.
           - **Re-registration is idempotent:** the console returned the byte-identical `<registkey-wire>` registkey, so
             it is stable per (account, console) across registration epochs, not minted per registration.
           - **What is STILL missing, and it is now the only thing: the response cipher KEY.** For Ripcord to
             perform no-PIN registration itself it must decrypt this response for an *arbitrary* console, which
             needs the key `decrypt@20d390` used. cap76 did not capture it — the hook dumped the call's args
             only when they contained the plaintext registkey, which is true only AFTER decryption, so the
             key/IV/ciphertext on entry went unrecorded. (Third run in a row a value-pinned recogniser hid the
             thing we wanted; the pattern is now explicitly the lesson.) The `NoPinResponse_IsNotDecryptable…`
             test already rules out every raw bundled-table entry, so the key is a no-PIN-specific derivation
             we have not reproduced — and it cannot involve the companion (delivered in-band).
         - **cap77 (2026-08-20) re-confirmed distribution and located where the key is NOT.** The decrypt
           entry args were dumped unconditionally this time; `decrypt@20d390` is `(ctx, out, cap=0xc00, ctx2,
           …)` with `out` filling to the plaintext field block on return. **The 16-byte response key is not in
           any of the shallow arg buffers** — `nopin_find_key.py` slid a 16-byte window over all 5,090 dumped
           bytes and tested each as the AES-128-CFB key against the known `responseBody` (IV-free CFB-tail
           oracle, the `NoPinCompanionKeySweepTests` one): **0 hits.** The apparent key-shaped block at `arg4`
           was pointers (`0x77c3673f…`, into code), not material. So the key lives deeper in the crypt-context
           object graph — as the raw key or the first 16 bytes of its AES round-key schedule.
         - **cap78 (2026-08-20) dumped the stack-arg object graph (220 nodes / ~60KB) — still 0 hits, which
           localises the gap to the calling convention.** `nopin_find_key.py` (the one-command brute-force,
           dirty room) confirmed the key/schedule is not anywhere reachable from the *stack* args. The oracle
           itself is not in doubt: it is byte-identical to the C# `TryRecoverRegistkey`, which
           `Oracle_RecoversAKnownRegistkey_FromThePinFixture` pins as correct. `FUN_20d390` is a C++ member, so
           its crypt object — where an AES key lives — is passed in **ECX/EDX** (`__thiscall`/`__fastcall`),
           which the graph roots never included. That is the one surface left, and it is a specific fix, not a
           guess.
         - **cap79 (2026-08-20) failed to PAIR — and the failure was the instrumentation, not the console.**
           Cloud all 2xx, but the console link died at "Linking your PS5", and the registration decrypt never
           produced the registkey block (so no key to find; the finder correctly reported nothing on 515KB).
           With Frida AND mitmproxy off, pairing succeeds. Two mechanisms, now both mitigated:
           1. **Proxifier routing the console's UDP through an HTTP-only proxy.** The console link is UDP
              9303/9296/9297; mitmproxy cannot carry UDP, so a Proxifier rule that catches all of
              the vendor client breaks the link while the cloud (TCP/443) still works. **Fix: the key is caught
              in-process by Frida — the wire capture is not needed for it (cap76 already settled
              distribution). Run the key capture with mitmproxy/Proxifier OFF entirely.**
           2. **The graph dump ran synchronously inside `decrypt@20d390` and got too heavy** (raised to 500
              nodes/depth 5 between cap78 and cap79), stalling the handshake. **Fix: the hook is now minimal —
              only `decrypt@20d390` (parser/http dropped), the heavy graph dump fires only for the first
              `MAX_DECRYPT_DUMPS`=4 calls then goes inert so streaming decrypts run at full speed, and the
              per-network-read `recv`/`DecryptMessage` hooks are disabled.**
         - **cap80 (2026-08-20) is the FAILING run's diagnostic, and it settles the cause: the Frida hook's
           IPC volume, not the proxy and not device/account state.** pcap shows the console **replying**
           bidirectionally on WAN 9303 (~10 frames each way over 4.3s) then the link times out — a stalled
           handshake, not blocked UDP. The Frida log shows why: each `decrypt@20d390` call ran a 500-node graph
           dump = ~9,000 `console.log` lines (1,000 node-dumps across two link attempts), and every
           `console.log` is a synchronous IPC round-trip to the Frida host — so the decrypt blocked for
           *seconds* mid-handshake and the console gave up (`80001fff`). This is why linking succeeds with the
           instrumentation off. **Fix (done): the hook collects the graph SILENTLY (memory reads only, ~1ms),
           and emits it in a SINGLE batched `console.log` only for the one decrypt whose output is the
           registkey; failed and streaming decrypts emit nothing.** The earlier per-line emit — even in the
           "lightened" version — would itself have stalled the handshake; that flaw is now removed.
         - **cap81 (2026-08-20) CONFIRMED the fix: linked AND streamed with Frida attached** (1,675 A/V frames
           to the console on 9297), and the registration decrypt fired and produced the registkey in its
           output. So the stall was the whole pairing problem and it is solved. One bug left the graph empty —
           the collect filter tested `!p.isNull` (a method reference, always false) and dropped every root, so
           it emitted "0 lines". Fixed to `!p.isNull()`; walk trimmed to 300 nodes / depth 4 (cap81 walked
           zero, so this is its first live exercise). Everything is now in place: the connect completes, the
           registration decrypt is caught, and the crypt-object graph (ECX-rooted) will actually be emitted.
         - **cap82 (2026-08-20) RECOVERED THE RESPONSE KEY, LIVE — and the crux that blocked cap75–81 is now
           understood.** With the stall fixed, the connect completed and the registration decrypt emitted its
           ECX-rooted crypt object. The key is at node `0x6db8fac` +0x1c, with a 16-byte IV-candidate at +0x0c
           (`{iv, key}` layout). **The response key is PER-REGISTRATION:** cap82's 195-byte ciphertext shares
           ZERO bytes with cap64's, so every earlier finder run was testing a capture's memory against a
           *different session's* ciphertext and could never match. Against cap82's OWN response ciphertext
           (extracted from the `POST /sie/ps5/rp/sess/rgst` reply on 9303, frame 6551), `nopin_find_key.py`
           found `<redacted: per-pairing key, dirty room>`, and a full **AES-128-CFB** decrypt with it reproduces the
           exact field block — `PS5-RegistKey: <registkey-wire>`, `RP-Key: <redacted>…` (companion), nickname, MAC. Cipher
           and key confirmed. Saved to the dirty-room fixture `nopin_response_key_cap82.json`.
         - **THE LAST STEP: the key DERIVATION — narrowed, and the offline hypotheses are FALSIFIED.** The
           derivation harness recovers cap82's material via the validated
           `RecoverMaterial` = `<redacted>`, then sweeps). It pins the confirmed fact (the
           recovered key CFB-decrypts the response to the registkey) and records the search. **Ruled out
           (2026-08-20):** the key is **not** `HMAC-SHA256(contextKey, material‖be64/le(ctr))[:16]` for any
           bundled context key or counter<64; **not** a single or 32-byte HMAC of material / context /
           registkey / companion in either key/message order; **not** a SHA-256/512 of those; the `{iv,key}`
           32-byte pair is **not** one HMAC output; there is **no RP-Nonce** in the request or response to key
           off; and the response key is **not** the request-field key (it does not decrypt the request field).
           So the derivation is not a one-liner over the material we hold. **Next, and it is a LIVE trace, not
           more offline guessing:** `frida_nopin_kdf.js` hooks the KDF the project already reversed —
           `FUN_101ede80` (dispatch; arg0 = `[variant, flag, nonce(16), companion(16)]`), its mode-1/mode-0
           variants `FUN_101fe340`/`FUN_101fdd80`, the field-encrypt `FUN_101f8cc0` (KEY/material explicit
           args), and `FUN_20d390` (decrypt marker). For each call it logs the `(nonce, companion)` inputs and
           dumps the output region, IPC-light. Run it on one re-registration (proxy off, Wireshark on), then
           `nopin_find_key.py <log> <responseBodyHex> PS5-RegistKey` finds which KDF output is the response
           key — its `(nonce, companion)` sit in the `[KDF ...]` header right beside it. Because
           `HalyardControlKdf.Derive(nonce, companion, versionSelector)` already implements this KDF,
           confirming the derivation is then `Derive(nonce, companion) == <redacted>`.
         - **cap83 (2026-08-20) RULED THE KDF DISPATCH OUT as the response-key source, and offline mining is
           now exhausted.** The KDF dispatch (`FUN_101ede80`) fired ~1.7s AFTER the response decrypt and was
           keyed by our just-registered companion — i.e. it is the STREAMING session KDF, not the registration
           response. The request field key (`<redacted>`, material all-zero) does not decrypt the response either.
           And an exhaustive offline sweep — **41,138 distinct 16-byte windows from cap82's entire crypt-object
           graph**, each tested as HMAC-SHA256 input (both orientations, counters 0–3) under all five bundled
           context keys against both the key and IV — found **nothing**. So the response key is not any HMAC
           over material we hold, under any bundled secret; it cannot be recovered by data-mining.
         - **THE remaining step is now a STATIC read, not another capture.** `frida_nopin_kdf.js` now takes a
           `Thread.backtrace` at the response decrypt and prints the call chain as `<vendor control DLL>+0x…`
           RVAs. One run yields the chain that set up the response key; those RVAs are then read in Ghidra
           (we have the DLL) to identify the key-setup function and its inputs. That function — not another
           sweep — is the derivation.
         - **cap84 (2026-08-20) took the backtrace and mapped the setup chain statically.** Chain at the
           response decrypt: `FUN_20d390` (decrypt) ← `FUN_20f410` (the WRAPPER that builds the crypt object,
           calls decrypt at 0x20f558) ← `FUN_1f6716` ← `FUN_1fc3c9`. Inside the wrapper, the crypt object is
           built by **`FUN_20d8c0`** (call at 0x20f528, immediately before the decrypt), which loads the
           cipher state at `[cryptobj+0x180]`/`+0x1b0` via the EVP-like primitives `FUN_101de970`/`FUN_101de980`.
           The raw key is not a visible 16-byte copy there — it is threaded through those calls' args from the
           wrapper's inputs (the `edi` session-struct fields). Full call-chain + offsets saved to the
           dirty-room note `nopin_response_key_callchain.md`.
         - **Ghidra decompile (2026-08-20, headless 12.1.3) mapped the whole response path and LOCATED the
           key's home.** Decompiles saved to the dirty room (`nopin_response_decomp.c`, plus
           `nopin_response_key_callchain.md`). Findings: `FUN_20f410` is the response PARSER (decrypt →
           strncmp `AP-Bssid:`/`PS5-RegistKey:`/`RP-Key:`/… → scatter into the session struct), confirming the
           full field layout. `FUN_20d390` is receive-and-decrypt; its field-decrypt call
           `FUN_101f8c20(out, in, len, conn+0x190, conn+0x180, conn+0x1a8)` reads the **KEY / material /
           context-key from the connection object at offsets `+0x180`/`+0x190`/`+0x1a8`** (conn =
           `*(sess+0x1c0)+0x16c`). So the response key `<redacted>` lives at `conn+0x180`-ish, set upstream during
           connection setup. (One correction logged: `FUN_101ee740`→`FUN_101fe680` is the Winsock endpoint
           builder, not crypto — a reminder to verify each layer.)
         - **SOLVED to the cipher model (2026-08-21, Ghidra): the no-PIN response cipher IS the field cipher
           Ripcord already implements.** Decompiling the field-decrypt `FUN_101f8c20` and its callees shows
           `FUN_101f8d40` = `HMAC-SHA256(contextKey, material‖be64(counter))[:16]` (byte-identical to
           `HalyardFieldIv.Derive`) and `FUN_10210280` = `AES_set_key(key,128)` + CFB. The connection object
           holds **`conn+0x190` = AES key (field-cipher out1, = cap82's `<redacted>`)**, **`conn+0x180` = material
           (out2)**, **`conn+0x1a8` = counter (u64)**, context key = a bundled constant. So the response is
           `HalyardControlFieldCrypto` verbatim. This finally explains every failed offline sweep: the key is
           out1 (a KDF *output* stored at `conn+0x190`), not a `Derive()` result, and the IV uses out2
           (`conn+0x180`), not the request-context material — we were testing the wrong construction with the
           wrong material.
         - **VALIDATED END TO END (2026-08-21, cap85, Frida + wire).** `frida_fielddecrypt.js` captured the
           live cipher inputs for the registration response: KEY(conn+0x190)=`<redacted>`, material(conn+0x180)=
           `<redacted>`, counter=1. Decrypting cap85's **wire** response (195 B off the 9303 200-OK) with
           `AES-128-CFB(key, HalyardFieldIv.Derive(contextKey, material, counter))` yields the exact field
           block — registkey + companion + nickname + MAC. **So the no-PIN response cipher is
           `HalyardControlFieldCrypto` verbatim, and Ripcord reproduces it with existing code.** (Blocks 1+ =
           the whole payload are contextKey-independent under CFB and decrypt cleanly; only block 0's
           `AP-Bssid:` needs the exact bundled contextKey — cosmetic.)
         - **cap86 (2026-08-21, merged hook `frida_nopin_solve.js`): the STREAMING KDF is validated against
           Ripcord's own code, and the registration-response key is shown to be a DISTINCT, earlier
           derivation.** `HalyardControlKdf.Derive(nonce, companion, 1)` reproduces the streaming
           field-cipher key/material byte-for-byte (throwaway C# check, deleted; no live values committed) —
           so the KDF model and Ripcord's implementation are confirmed. **But the registration RESPONSE key is
           NOT that derivation:** the `FUN_101ede80` KDF calls fire ~1.7s AFTER the response and carry the
           registered companion (they are the streaming session); the response key is set *before* the
           response arrives, so it cannot be companion-derived — the companion is delivered *inside* that
           response (chicken/egg).
         - **The last question, now with a real fork.** The no-PIN response key is derived from pre-response
           material only (request nonce + bundled constants + possibly an account/device credential).
           **(a) clonable** if it is request-nonce + bundled constants (+ the account id we already send);
           **(b) NOT fully clonable** if it is a server-issued / account-bound secret obtained out-of-band.
           Deciding instrument below.
         - **STATIC (2026-08-21, Ghidra) reframed the key: it is a STORED 16-byte SEED, not a KDF output.**
           `FUN_20d0a0(conn, seedPtr)` → `FUN_101ebdb0` simply `memcpy`s a 16-byte seed into the cipher key
           `conn+0x190` (material `conn+0x180` and `conn+0x1b0` are zeroed). The seed is `FUN_20f410`'s
           `param_2` = session field `[edi+0x10]`. So there is no derivation function to reverse — the whole
           question collapses to **where that 16-byte seed comes from**. (One caveat kept: `FUN_20d0a0` sets
           material=0 but the response decrypt used a non-zero material, so material is re-filled between init
           and decrypt; the key — the seed — is the dominant question.) Decompiles in
           `nopin_response_decomp.c`; string-xref found the request-line builder `FUN_101ee360` (not the
           seed generator — that is a hop further up).
         - **THE fork, resolved by one Frida run.** `frida_seed_trace.js` hooks `FUN_20d0a0` (dumps the 16-byte
           seed + a **backtrace of who supplied it**) and `FUN_20d390` (dumps `conn+0x190`/`+0x180` at
           decrypt). **(a) CLONABLE** if the seed == the decrypt key and its provenance is an RNG / a value
           derived from bundled constants + the request (Ripcord can reproduce it); **(b) NOT CLONABLE** if the
           backtrace shows the seed came from a socket read (server-issued). **State: response cipher `[C]`,
           validated live; streaming KDF `[C]`; key shown to be a stored seed; seed origin `[X]` — one capture
           decides (a)/(b).**
         - **RESOLVED 2026-08-21 (cap88) — fork (b): the response key is EXCHANGE-ESTABLISHED, not
           client-derivable; no-PIN registration is CLOSED as not-cleanly-clonable.** A bracket over the same
           conn showed `conn+0x190` (key) and `conn+0x180` (material) are both set during `FUN_20d8c0` — but
           that function only reads conn, serializes and SENDS, and none of its callees take conn. So they are
           written by the RECEIVE side of the round-trip it drives — established from material the console
           contributes mid-exchange. Cross-checked: not `HalyardControlKdf.Derive`/HMAC of any client-side
           shared known (seed, account id ×3, console MAC, registkey, client/console IPs, zeros — 300+ combos);
           no nonce/device-id in the rgst request. So Ripcord cannot compute the response key from bundled
           constants + values it already holds; closing it would mean reversing the receive-side re-key
           handler and the console's on-wire contribution — the same class as the device-registration
           authorization that blocks WAN play. Not strictly proven impossible, but high (b)-risk / diminishing
           returns, so **banked**.
         - **CORRECTION 2026-08-21: (b) was over-stated — this is a KEY EXCHANGE and is very likely
           CLONABLE.** "Established from console-contributed material mid-exchange" is the shape of a key
           exchange (ECDH / client-key-under-shared-secret / key = f(received nonce, shared secret)), all
           reproducible — not a dead end. Reversing it from our own captures + our own DLL is the path, same
           method as the rest of the project. **Clean-room, re-affirmed: other apps having solved this does NOT
           change our method — we do not read their source/constants/layouts, and training-data recall of them
           is the same laundered-provenance violation; their success only confirms solvability.** Concrete
           next step: page-guard at `FUN_20d8c0` entry to catch the RECEIVE-side re-key writer + backtrace,
           decompile that handler to read the exchange, capture the wire request/response, reproduce in
           `HalyardRegistrationCipher`.
         - **SOLVED IN PRINCIPLE 2026-08-21 (cap89) — CLONABLE; the re-key is the table-KDF family we already
           implement.** Inner-bracketing localized the re-key to `FUN_101de970`(=`FUN_101ebe70`): it reads the
           seed (client-chosen, from `session+0x10`) + zero material, runs `FUN_101ed040`, writes the results
           to `conn+0x190`/`+0x180`, then field-encrypts the request with them — **all inputs local, no network
           data.** `FUN_101ed040` is a KDF dispatcher over the SAME table functions already reversed
           (`FUN_101fcda0`/`FUN_101fd830` = `HalyardRegistrationKdf`; the two-selector two-pass =
           `HalyardControlKdf`). So the response key is a deterministic local derivation of the seed via
           primitives Ripcord already ships → no-PIN registration is clonable, and the earlier "(b) not
           clonable" is retracted. Two known-answer I/O pairs captured (dirty-room note) to validate a
           reproduction. **Remaining (bounded):** decompile `FUN_101ed040` + the selected variant to read the
           tables / selector offsets / seed threading, reproduce, validate against the two pairs, and confirm
           the seed's origin (`session+0x10` — likely a client RNG echoed in the request). Then account-SSO
           ("web") pairing lands in `HalyardRegistrationCipher` alongside PIN.
           **Clean-room note:** other apps having solved web-pairing is NOT an input — this is derived from our
           own DLL + our own captures; do not consult or recall theirs.
         - **KEY DERIVATION FULLY SOLVED & VERIFIED 2026-08-24 (cap92/93).** The re-key variant is
           `FUN_101fd2a0` (mode1/keytype2), and: **`key' = seed XOR RegistrationTable.row[ ctx[0x18d] & 0x1f ]`**
           — verified in C# against cap92 (selector read from the WIRE request context = 4;
           `seed XOR registrationTable.row[4] == key'`). It is the no-PIN analog of PIN registration (PIN folds
           into `table[sel]`; here the client seed XORs the whole entry), and `registrationTable` + the selector
           are ALREADY in `HalyardRegistrationKdf`. Material transform likewise mapped
           (`((in_mat − i + 0x2b) ^ table2[sel2])`). **The ONE remaining thread: the seed's origin.** The seed
           (`session+0x10`) is not the wrapped material and is not anywhere in the 480-byte request context, so
           it is established in an earlier step (an earlier `/sess` message or the PSN account-SSO exchange).
           Ripcord as the client generates it, so it can compute `key'`; interop needs how it is agreed with
           the console. **Next:** trace the write to `session+0x10` (backtrace) or find it in the earlier wire
           exchange — `frida_seed_origin.js`. **Status: no-PIN response cipher + key derivation `[C]` (verified
           vs wire); seed origin `[X]` (one trace). Clonable; the crypto is done and reuses our KDF.**
         - **cap94 (2026-08-24) — the seed is per-REGISTRATION, i.e. the no-PIN `RP-RegistKey` analog.**
           `frida_seed_origin.js` hooked the CSPRNGs + the seed read: the seed
           (`<redacted>`) is **not** among the RNG outputs (caveat: the RNG log capped at 400 before the seed
           read, after the sign-in TLS burned ~394 draws — so a late draw is unlogged) and is **not** in
           `cap94.pcapng` (raw or byte-reversed). The tell is in the recovered seeds themselves —
           cap87/89/91/92/94 are all different, and **every one of those captures followed the "delete the
           store, then sign in and connect" recipe, i.e. was a FRESH REGISTRATION.** A value that changes
           per-registration (but is hypothesised stable across connects of one registration) is not a session
           nonce — it is the per-registration secret: the account-route registration key, established during
           the account-SSO **pairing** handshake (cloud/TLS, during sign-in), stored, and reused at connect.
           That explains all three facts (secret, absent from the LAN wire, not a fresh local RNG draw).
           **Clean-room consequence:** the seed is PERSONAL material (per-console/per-account), never a bundle
           candidate — it lives in the credential store beside `RP-RegistKey`; the GENERIC algorithm
           (`key' = seed XOR registrationTable[…]`, cipher, material transform) is the solved, bundle-relevant
           part and already uses our tables. **Decisive test (`frida_seed_stable.js`): connect TWICE without
           deleting the store, compare seeds** — EQUAL ⇒ stored per-registration key (obtain it at account-SSO
           pairing, store it, use at connect; parallel to `RP-RegistKey`); DIFFER ⇒ per-session, delivered in
           the encrypted account-SSO exchange (capture it with mitmproxy TCP-only, which leaves the UDP console
           link intact). Either branch is clonable and points at the account-SSO exchange, not more DLL work.
         - **cap95 + the seed's SOURCE identified 2026-08-24 — `connRequest.skey` (PSN-issued, per-registration).**
           cap95 (connect twice, no store delete) printed only `[SEED #1]`; the second connect reached a stream
           but did **not** re-fire `FUN_20d0a0` — so the seed is read **only at registration**, and a paired
           reconnect runs on stored creds via `HalyardControlKdf`. A hard constraint pins the seed's origin: the
           rgst *request* field is encrypted with `key' = seed XOR table[sel]`, so the console must already hold
           the seed to decrypt the request — it cannot learn it *from* the request → the seed is PSN-cloud-issued.
           Our own cloud flows (cap73/cap76, `web.np.playstation.com` sessionMessage / `connRequest` OFFER) carry
           exactly such a value: **`connRequest.skey`** — the client's opening request sends `skey` all-zeros and
           the console's OFFER returns a 16-byte per-registration secret (cap73 `<redacted>`, cap76 `<redacted>`).
           `skey` was flagged as the pairing candidate months ago but only ever tested *directly* as the decrypt
           key (0 hits); it was **never tested as the seed**, because the `key' = seed XOR registrationTable[sel]`
           formula did not exist yet. No single capture yet holds both `skey` (cloud) and `seed` (Frida) for one
           registration, so the confirmation is **cap96**: one fresh registration with PSN-scoped mitmproxy
           (console left Direct, so UDP never hits the proxy → no `8801330d`) + the Frida seed hook, then test
           `seed == skey` / `seed == skey XOR const` / `key' == skey XOR registrationTable[sel]`. Any hit closes
           no-PIN registration; the account route becomes: run the sessionMessage exchange, take `skey`, derive
           `key'`, decrypt the response, store `RP-RegistKey` + companion. Plan: `cap96-seed-vs-skey-plan.md`.
         - **cap96 (2026-08-24) — key formula RE-VERIFIED on fresh data via a full live-request decrypt; seed ≠
           skey; seed is a jointly-derived account secret.** cap96 captured both legs of one fresh registration
           (seed `<redacted>` via Frida; `connRequest.skey` `<redacted>` via PSN-scoped mitmproxy). Findings:
           **(1)** `seed ≠ skey`, and `skey` is identical for the control (9303) and senkusha (9297)
           `connRequest`s — i.e. it is the *streaming-session* key present on every connect, not the
           registration seed. **(2)** Using only our own bundle + the seed, the live `/sess/rgst` **request**
           (frame 6555) decrypts offline: `sel = ctx[0x18d]&0x1f = 11`, `key' = seed XOR
           registrationTable.row[11]`, and AES-128-CFB(`key'`) cleanly decrypts the 107-byte field (CFB blocks
           1..n are IV-independent, so they validate `key'` outright) — **re-confirming `key' = seed XOR
           registrationTable[sel]` on a second independent capture.** The field carries a 30-byte value +
           `Np-AccountId` (the account id, little-endian, base64). **(3)** Because the request decrypts with
           `key'`, the console must also derive `key'` → it must hold the seed → the seed is a value *both*
           sides compute, not one side's private RNG. **(4)** The seed is transmitted nowhere observable: not
           in the LAN pcap, not in the decrypted request field, not in the decrypted PSN cloud flows
           (`cap96.flows`), not `skey`, not a hash/HMAC of skey+account+device, not any xor/wrap/unwrap of the
           480-byte context. **Conclusion:** the seed is a per-registration account-authorization secret jointly
           derived from PSN's account-SSO authorization (out-of-band of the session-manager flows), on the same
           shelf as `RP-RegistKey`. **Definitive next step (autonomous, no capture): static-trace the *writer* of
           `session+0x10` in our own DLL (Ghidra)** — `FUN_20d0a0` only *reads* it; naming its writer settles
           the origin instead of guessing. no-PIN registration is clonable modulo obtaining/deriving that seed.
         - **Ghidra trace DONE 2026-08-24 — the seed is an APP-LAYER input via `CSharpInterface`, not
           DLL-derived.** Static call chain in our own control DLL: `FUN_101f61f0` (session thread)
           dequeues a **command** message → type 2 → `FUN_101f6c00` → `FUN_1020f410(conn, seedPtr=*(msg+0x10),
           …)` → `FUN_1020d0a0` copies the 16-byte seed and runs `FUN_101de940(seed, conn+0x190 key,
           conn+0x180 material, conn+0x1b0)` (the seed→key/material expansion), then sends (`FUN_1020d8c0`) and
           decrypts the response (`FUN_1020d390`). The type-1/2/5 messages are **commands the app enqueues**
           (read from an internal queue with a 1000 ms timeout), not network packets; the DLL exports a
           **`CSharpInterface`** consumed by the vendor app's managed layer. So the seed is **supplied by the C#
           app as a registration-command input — generated/derived nowhere in the native DLL**, which rules out
           every DLL-internal derivation. Where the app gets it is a **capture gap**: cap96's allow-list was
           `web.np.playstation.com` + `np.communication.playstation.net` only, **excluding the account-SSO/OAuth
           hosts** (`auth.np.ac.playstation.net`, `account.sonyentertainmentnetwork.com`) — so the exchange that
           hands the seed to the app was never proxied, which is exactly why the seed is absent from every
           captured artifact. **Net:** no-PIN crypto is fully solved and twice-verified; the seed is a
           per-registration account-SSO secret from an as-yet-uncaptured PSN auth host. To name it: cap97 with
           the auth/account hosts added to the allow-list (console still Direct), then find the seed in those
           flows — or RE the vendor's own managed assemblies (our-own-client, clean-room-OK). Native-DLL trace complete.
         - **Managed RE DONE 2026-08-24 (the vendor client via ilspycmd) — the app passes only the OAuth ACCESS
           TOKEN; the seed is native-side.** Decompiled the vendor's own obfuscated managed app (renamed
           symbols, encrypted strings, dynamic dispatch). It reaches the vendor control DLL by `LoadLibraryEx` +
           `GetProcAddress` on the ordinals, bound as named delegates in `RemotePlayCtlMethods`. The
           registration/connect entry signatures are decisive: `startpin(LPWStr pinCode)` (PIN),
           **`startinet(LPWStr AccessToken)`** (WAN/account — passes the OAuth access token),
           `starthome()` (LAN — **no args**), `constart(LPWStr ip)` (connect), `removeregkey()` (deregister);
           callers pass `AccessToken.ToString()` / the PIN / nothing respectively. **So the managed app never
           supplies or fetches a 16-byte seed — for the account path it hands the native DLL only the OAuth
           access token.** The seed is produced entirely inside the native DLL, which imports `CryptGenRandom`
           (advapi32) feeding an OpenSSL-style RAND pool (`FUN_102103e0`/`FUN_10164fb9`/`FUN_10105e88`) — a path
           my earlier Frida RNG hooks (BCryptGenRandom/ProcessPrng) never covered, explaining the earlier
           non-match. The one managed KDF (`OAuth2Base` `Rfc2898DeriveBytes`) is local data-protection (AES key
           from MachineGuid+UserName to encrypt stored creds), not the seed. **Conclusion:** the no-PIN seed is
           a client-side per-registration secret produced natively (RNG), consistent with its absence from
           every captured artifact; the console gets its copy via the access-token-authenticated account-SSO
           registration with PSN (native HTTPS to the auth host cap96 excluded). **Remaining tiebreaker (both
           clonable):** is the seed *generated* client-side or *fetched* from the auth host? Distinguish with one
           capture — widen the allow-list to `auth.np.ac.playstation.net` + `account.sonyentertainmentnetwork.com`
           (console still Direct) and check whether the seed appears there; absent ⇒ client-generated (Ripcord
           makes 16 random bytes). **Account route for Ripcord:** OAuth access token → account-SSO registration;
           per-registration 16-byte seed (generate or read); `key' = seed XOR registrationTable[sel]`; `/sess/rgst`;
           store RegistKey+companion. All crypto ours, twice-verified.
         - **✅ SOLVED end-to-end 2026-08-31 (cap105–cap107) — the seed is DELIVERED by the console, not derived.**
           A runtime memory dump caught the seed being produced by the field cipher (`FUN_101f8c20` =
           `HalyardControlFieldCrypto`), and the signaling pinned the wire source exactly. **The client generates
           two ephemeral 16-byte values `data1` (key) and `data2` (material), sends them in the
           cloudAssistedNavigation command; the console generates the seed, field-encrypts it with
           `(data1, data2, counter=0)`, and returns it as `customData1` (double-base64) on the push channel.** The
           client decrypts it to the seed. Verified byte-exact against `session+0x10` (cap107):
           `seed = HalyardControlFieldCrypto.Decrypt(key=data1, material=data2, ctr=0, ct=customData1)`, then
           `key' = seed XOR registrationTable[ctx[0x18d]&0x1f]` (already twice-verified). **Every primitive is ours**
           (field cipher + bundled `contextKey`) — no exotic RE. This is why every derivation hunt failed and the
           seed was in no plaintext channel: it rode the wire as `customData1` ciphertext. Retro-corrects the
           2026-08-20 notes that dismissed `data1`/`data2`/`customData1` as ephemeral non-material — they were the
           ephemeral key/material and the encrypted seed. **Ripcord account route (fully specified):** generate
           `data1`/`data2` → send in the cloud command → read `customData1` from the push channel → field-decrypt →
           seed → `key'` → decrypt the `/sess/rgst` response → store `RP-Registkey` + companion. **no-PIN / account
           ("web") registration is SOLVED**, and WAN play unblocks. (Historical dead-ends — RNG, ECDH, npticket,
           local KDFs — recorded in `nopin_response_key_callchain.md` UPDATE 27–38.)
         - **Solid, banked results:** response cipher = field cipher AES-128-CFB
           (validated live vs the wire); streaming KDF = `HalyardControlKdf` (reproduced by Ripcord); full
           decrypt call-chain + object layout (`conn+0x180` material / `+0x190` key / `+0x1a8` counter) mapped;
           The derivation harness pins the recovered cipher. Resume point (if ever): page-guard at
           `FUN_20d8c0` entry to catch the receive-side writer + backtrace.
         - Superseded note (kept for the record): earlier entries treated "find the key in the dump" as the
           goal and kept widening the graph. The dump was fine from cap78 on; the error was the *reference
           ciphertext* (per-console assumption vs the per-registration reality), which is why the fixed-hook
           cap82 succeeded only once tested against its own wire response.
         - **THE capture that now matters: none — the remaining work is offline derivation.** (Prior text kept
           below for the hook/procedure details.) The registration decrypt fires early (during linking), so the first-4-calls gate still catches
           it while leaving the stream path untouched. `dumpGraph` roots at ECX/EDX first, so the crypt object
           holding the key (or its 176-byte schedule) is captured. Then
           `python3 nopin_find_key.py ps-rendezvous/cap80/cap_frida.txt`: a hit prints the key + offset, which
           with the known ciphertext + plaintext fully specifies the account-route response cipher and
           `HalyardRegistrationCipher` gains its no-PIN variant. Fallback if the ECX graph is still empty:
           hook the vendor's AES primitive directly, which necessarily sees the round keys.
         - Instrument: `HalyardPushMaterialSurvey` plus a push-material fixture,
           `docs/protocol/captures/no_pin_push_frames.txt`.
  3. **Cloud wake** — ~~`HalyardCloudClient.SendConnectCommandAsync`. Currently the **only** wake mechanism
     that exists anywhere in the tree, implemented but unreachable.~~ **REACHED 2026-08-07** — this line said
     "still gated on (a)" when written; **(a) closed later the same day** (see the status block below), so
     cloud wake is now live and shipping in the app. `HalyardSessionCoordinator.WakeAsync` creates the session and sends
     the command without going on to negotiate, which is the half that works: the command travels through
     PSN's own server-side fan-out, so it does **not** depend on the push channel. Wired into the app as
     `CloudFallbackWakeCoordinator`, a decorator that tries the local wake first and falls back only when the
     local broadcast could not reach the console at all. The result is reported as a new
     `ConsoleWakeOutcome.AskedRemotely` rather than `Woken`, because acceptance genuinely does not tell us the
     console came up. Targeting needs the console's `duid`, now stored as `PairedConsole.CloudDeviceId` and
     learned at pairing time by matching the account's console list against the local scan by name (an
     ambiguous match stores nothing rather than guessing — a wrong id would wake someone else's console).
  - **Precisely what is missing, since "resolve the OAuth decision" invites two wrong readings.** The
    *mechanics* are written — `HalyardAuthClient` (authorize URL, code exchange, refresh) and
    `HalyardTokenProvider` (auto-refresh). What blocks a live token is (a) **no client credential**:
    `HalyardClientConfig` requires one and the project has *deliberately* declined to embed the official
    app's ("clean-room + it isn't ours to embed"), which is the actual unresolved policy decision; (b)
    `ExchangeCodeAsync` has **no caller anywhere** — only the refresh leg is exercised, and only from an
    out-of-band `RIPCORD_REFRESH_TOKEN`; (c) no sign-in UI or token store in the app. So it is genuinely
    blocked on a decision, not merely unwired — a 2026-08-02 review that claimed otherwise was wrong.
  - Note (3), cloud wake, was previously framed as the fallback if no LAN wake existed. LAN wake does exist
    (settled 2026-08-03), so cloud wake is now only about waking a console you are *not* on the same LAN as —
    a WAN-play concern, not a prerequisite for local play.

  > **Status update 2026-08-07 — (b) and (c) are DONE; (a) is unchanged and is now the only blocker.**
  >
  > Built this pass: `IAccountTokenStore`/`AccountTokenStore` (refresh token DPAPI-encrypted at rest, its own
  > `account.json` so sign-out cannot disturb pairings); `HalyardAccountGateway`, which is the caller
  > `ExchangeCodeAsync` never had, plus restore-from-stored-token and sign-out; the portable `IAccountSession`
  > seam with `UnavailableAccountSession` as the no-credential null object; `AccountViewModel`; a WebView2
  > `AccountSignInDialog` and an Account section on the settings page; and `signin`/`cloud`/`cloudwake`/
  > `signaling` in ProtocolLab. **Item 1 above (account-SSO pairing) is not addressed** — this delivers the
  > account *identity*, not the no-PIN pairing route.
  >
  > **Two capture-derived facts settled while doing it:**
  > - The authorize call must carry `device_type=PC_APP` and a `duid`; ours previously sent neither. The
  >   `duid` is 48 hex = an 8-byte constant prefix (`0000000700410080`, meaning **[X]**) + the 16-byte machine
  >   id `IDeviceIdentity` already supplies. See `HalyardClientDeviceId`.
  > - The token endpoint takes `Authorization: Basic` with a **non-empty 16-char secret** against a 36-char
  >   UUID client id. It is *not* an empty-secret public client, so there is no PKCE route that would let us
  >   sidestep (a). That possibility is now closed rather than untested.
  >
  > **(a) is now DECIDED — 2026-08-07, by the project owner, after verifying the credential is already
  > published.** The vendor client's `client_id`/`client_secret` are bundled as data in
  > `src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json`. **This closes the whole item**; all three
  > capabilities above are now gated only on their own remaining work rather than on a decision.
  >
  > What the decision rested on, recorded so it is not re-argued from memory: the value came from **our own
  > capture** (it was already in hand before any comparison), and was *then* confirmed to be in wide public
  > circulation — which retired the "publishing it invites rotation" objection, since one more copy of an
  > already-public value moves that risk very little. The remaining consideration is positioning rather than
  > secrecy: Ripcord authenticates as the vendor's application because PSN offers no third-party client
  > registration, and `NOTICE` now says exactly that in its own section.
  >
  > **The two exceptions are kept separate on purpose.** `CLAUDE.md` now lists this as bounded exception 2,
  > argued on different grounds from the v1 constants: those are interface facts the console computes against,
  > this is an access credential a client provably does not need to speak the protocol. Do not merge the
  > arguments, and do not cite this exception as precedent for a third.
  >
  > Mechanics: `-p:BundleOAuthClient=false` omits it; `RIPCORD_CLIENT_ID`/`RIPCORD_CLIENT_SECRET` and a
  > `client.json` both override it; `tools/extract-oauth-client.py` repopulates it from the capture on a
  > machine that has the dirty room. A checkout without the dirty room keeps an inert placeholder and behaves
  > exactly as the pre-decision build did. `HalyardAccountAuthTests` guards the bundle against being
  > half-populated and against carrying any user-scoped material.
  - Deliberately **not** on this list: PS Plus cloud streaming (a hosted instance rather than your own
    console). No capture, doc, or note in this project touches it, and there is no basis for assuming it
    shares this wire protocol — the session manager targets a console by `duid` from the account's device
    list. Out of scope, and recorded here only so its absence is a decision rather than an oversight.
- [x] ~~**Handheld deployability.**~~ — **CLOSED 2026-08-02.** It read "mostly fixed, one gap left", but the
      one gap named was the secrets path, which is `[x]` RESOLVED four items above; nothing else in the item
      names an unfixed defect. Retained below for the CRT note and the status-string lesson, both still
      worth having. Getting Ripcord onto the ROG Ally X (no dev
      tools) surfaced a chain of four separate failures, all since fixed: missing
      constants, the trimming issue, a `Page_Loaded` path with no try/catch (native GameInput construction
      threw before any status was shown, so the UI just said "Starting…"), and a **static XAML default** of
      `Text="Connecting…"` that made a dead session look like a hanging one. Lesson worth keeping: on a
      machine with no debugger, any status string that can be *wrong by default* costs hours. Also: **Debug**
      native DLLs need the non-redistributable VS debug CRT — ship Release for handhelds.

- [x] ~~**ARM64 build support.**~~ — **DONE 2026-07-31.** The whole stack now builds and runs natively on
      ARM64 Windows; the dev machine moved to Snapdragon. Debug and Release verified for both ARM64 and x64
      (either host cross-builds the other), managed suite green natively (411 passed / 6 skipped), and the
      D3D12 interop was verified activating on an **Adreno X2-45**: hardware H.264 decode supported at 1080p,
      synchronous decoder MFTs present for both H.264 and HEVC. What was actually wrong:
  - Both `.vcxproj` files declared only `Debug|x64` / `Release|x64`, and the solution offered no ARM64
    platform; `Ripcord.App` was additionally pinned to x64 by the `.slnx` mapping regardless of the solution
    platform.
  - The architecture was **hard-coded as the literal string `x64`** in six paths across
    `Ripcord.App`/`Media`/`Input`. It now resolves via `$(RipcordNativePlatform)` from the new root
    `Directory.Build.props` (`$(Platform)` → RID → host arch, overridable with
    `-p:RipcordNativePlatform=`). Do not reintroduce a literal architecture in a path.
  - Those paths were also built on `$(SolutionDir)`, which is undefined outside a solution build — so the
    documented `dotnet build src/Ripcord.App/...` workflow was silently broken on **every** architecture, not
    just ARM64. `$(RipcordRepoRoot)` replaces it, and the vcxproj `ProjectReference`s are now skipped under
    the dotnet CLI (it cannot execute C++ build tasks at all) with a readable error if the native DLLs are
    genuinely absent.
  - `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` on `Ripcord.App`, so switching platform no
    longer fails with NETSDK1047 until a manual restore.
  - Was left open by this work: the bit-serial GHASH fallback on ARM64 (Track D) — correctness fine,
    throughput not. **Closed 2026-08-01**, and it was exactly the predicted problem: the first end-to-end
    stream on this hardware collapsed to 74.7% reported loss with the A/V queue pegged at capacity. NEON
    PMULL took per-packet A/V crypto from ~200 µs to ~12-15 µs, and the same hardware now streams
    **1080p60 at 0.4% loss**; see Track D.


#### Open — the Takion protocol version: we now negotiate 17, and almost nothing downstream knows it (2026-09-04)
The stream association used to send no version request at all, so **no version was ever agreed on it** and
the console fell back to its own default. It now offers `9,10,11,13,14,15,16,17` and is answered **17** —
the same list and the same answer as all five captures we hold, across both console families and both
routes. Senkusha keeps offering only `[9]`, which is also what the captures show: its `SESSION_REQUEST` is
keyless, so it needs no key agreement.
The version is load-bearing, not a label: **13–17 select P-521 and anything below selects P-256**
(`HalyardStreamKeySchedule.CurveForVersion`), so it decides the shape of the key on the wire. Corroborated
independently — the captured v17 `SESSION_REPLY` carries a 133-byte key field (`0x04 || X(66) || Y(66)`,
P-521 uncompressed) where P-256 would be 65; the signature stays 32 bytes either way.
**The follow-up is everything downstream of that.** Reaching 17 is done; *exploiting* it is not, and the
rest of the stack was derived while no version was being negotiated at all:
- **What 17 actually changes past the curve is `[X]`.** The launchSpec template, packet layout, codec and
  HDR declarations were all settled against captures without knowing which version was in force. If v17
  unlocks capabilities we do not declare, we are currently asking for a v9-era session over a v17 handshake
  and would not know.
- **10, 11 and 13–16 have never been seen chosen.** The negotiator speaks whatever the console picks rather
  than assuming it got its maximum — deliberately — but a console that answered 14 would take a code path
  no capture and no test has ever exercised. `CurveForVersion` is the only place that branches on it today,
  which is itself suspicious for a range that wide.
- **Why 12 is skipped is `[X]`.** The gap is real and reproduces in every capture; nothing explains it.
- The only end-to-end confirmation that v17 works is a LAN `connect` reaching `handshake accepted`. That is
  the handshake, not the stream — video over a negotiated v17 session has not been watched.

#### Settled on hardware — reference, don't re-litigate
Cheap to re-derive wrongly, so written down:
- The console **honours** `videoCodec` (H.264/HEVC) — both codecs tested on hardware — **and
  `dynamicRange`**, and **ignores** `yuvCoefficient`. The `dynamicRange` half is `[V]` as of **2026-08-02**,
  and the story is worth keeping: it had been sitting in this "don't re-litigate" section as settled on **no
  evidence at all** — only `"SDR"` had ever been seen on the wire and `"HDR"` was an inference from the field
  existing with a value. It was demoted to `[X]` that morning, then measured the same day: requesting
  `dynamicRange: "HDR"` makes the console send **HEVC Main10 / P010 signalling PQ transfer and BT.2020
  primaries**, read off the decoder output type rather than assumed. Right answer, but it was right by luck
  for weeks — an unevidenced claim under a heading that tells readers not to check is the most expensive
  kind. The rest of this bullet stands:
  `yuvCoefficient` — it signalled BT.709 in the stream regardless of what we asked for. Read the matrix from
  `MF_MT_YUV_MATRIX` on the decoder output; don't try to dictate it. (Two wrong conclusions were published
  here before the hardware settled it.)
- **`network.bwKbpsSent` is the resolution lever.** The console picks resolution from the ladder at launch
  based on it: at a 5 Mbps cap it chose 960×540, at 30–40 Mbps it chose 1920×1080.
- **HEVC does not buy resolution at a constrained cap** — 5 Mbps gave 960×540 on *both* codecs. HEVC's value
  is HDR and quality-per-bit, not a bigger picture.
- **No 1440p.** The vendor's resolution ladder is 640 / 960 / 1280 / 1920 (scores 1–4), so 1440 isn't an
  option the protocol offers; asking for it is not a thing we can do.
- **`CONNECTION_QUALITY` carries no resolution field.** The HUD must never claim a resolution change was
  "sent to the console" — resolution is fixed at launch.
**Senkusha echo + MTU probes — DONE, don't redo.** Implemented from our own capture analysis and verified
byte-identical against two captures. Recorded because the details are easy to get subtly wrong:
  - `mtuReq` denotes the **whole IP datagram**, not the payload: 28 bytes of IPv4+UDP header. Confirmed twice
    at two different sizes (1454→1426 payload, 1254→1226), which is why it's stated and not assumed.
  - The RTT ping is 548 bytes of payload — 556 as a UDP datagram, **576 as an IP datagram, which is the classic IPv4 minimum-MTU-safe size** (RFC 791's minimum reassembly buffer; it denotes the whole IP datagram, so the label belongs to 576, not the 556 an earlier revision attached it to).
  - **The two probe packets are padded differently, and this matters.** The RTT ping's tail is all zero; the
    MTU probe is filled with **`0x47` from offset 27**. Identical in both captures. Our first implementation
    zero-filled both — caught only by dumping the full tail of `cap47`. The asymmetry looks deliberate: a
    zero-filled payload is trivially compressible, so a link doing compression could carry it in fewer bytes
    than requested, and an MTU test measured that way passes at a size the path cannot actually deliver. If a
    future probe needs a new size, pad it the same way.
  - The echoed timestamp field can only be the client's *own* send time (the console returns the packet
    unchanged), so it is not a checksum and we are free to write our own value. RTT is still measured with a
    monotonic stopwatch rather than by reading the field back, because the wall clock can step.
**Congestion + control GMAC offsets — DONE, don't redo.** Derived from `cap47` and worth recording because
the method generalises: you don't need key material to locate a GMAC and a key position in an unknown packet.
A **GMAC** field is high-entropy — near-1:1 distinct values across the sample. A **key position** advances in
whole cipher blocks, so **every value is a multiple of 16**. At n>400 the two can't be confused. That settled
control tag@5/key_pos@9 (2528 packets) and congestion tag@7/key_pos@11 (474 packets), plus the ~200 ms
congestion cadence (min 201 ms / median 216 / p90 219). Zeros in the tag column mark the unauthenticated
handshake packets, which is a useful cross-check that you have the right column.

#### Correctness risks — need verification
Both need a console or a capture to settle, hence here rather than in Track D.

#### Provisional values — adopted but not confirmed against the console
Each of these is currently *assumed* rather than derived from our own evidence. They are tagged `[X]` in the
spec, which means provisional, not settled. Same priority tier as the correctness risks above.
- [x] ~~**FEC per-unit stride padding (round up to a multiple of 0x10).**~~ — **RESOLVED 2026-08-01, and it was
      not a bug.** The question was malformed: it treated one name for two different values.
  - **`_unitStride` (the 16-rounding) is not a protocol value and never was.** The console lays units out at
    `base + coded_len * unit_index` — its stride *is* the coded length, no other alignment anywhere. Ours is
    purely local slot spacing, and any value >= the coded length is equally correct: all coding reads and
    writes `[slot, slot + _unitPaddedSize)`, the buffer is zero-cleared per frame, and the tail is never read.
    So 16 and 4 are both correct here and the "discrepancy" was between a wire fact and a layout choice.
  - **`_unitPaddedSize` (the coded length) is the wire-bearing value, and the real alignment is 4** — `[C][W]`.
    `FUN_101035e0` rejects any fragment length with `(len & 3) != 0`, and on the wire in `cap47` (5,961 frames)
    parity length is constant within every frame, never exceeded by any source unit, with
    `parity_len − max(source_len)` only ever 0..3. So: **coded length = longest source unit rounded up to a
    multiple of 4.** We already compute exactly this, from either arrival order.
  - So the earlier analysis suggesting **4** was right, about the value it was actually describing. Neither
    number was wrong; they were answers to different questions filed under one heading.
  - Method note: parity units carry no size-extension, so a parity unit's payload length **is** the coded
    length — readable straight off the wire with no keys. That is why this was settleable from `cap47` when
    the matrix form was not.
  - Fixed in passing: `HalyardStreamHeader.TypeFec = 0x12` was dead and actively dangerous — `Type` is masked
    to the low nibble, so `Type == TypeFec` is unconditionally false. `0x12` is video (low nibble 2) with the
    extended-header bit set. Replaced with `IsParityUnit` (`UnitIndex >= SourceUnits`), which is what actually
    identifies a parity unit.
- [x] ~~**FEC generator matrix form (Cauchy, not Vandermonde).**~~ — **SETTLED 2026-08-01 [C].** Cauchy, and
      with the exact indexing we already had. `FUN_101035e0` builds each element as
      `table[((m + j) XOR i) | 0x100]`; that table is the field inverse (it is what the Gauss-Jordan pivot in
      `FUN_1015f2a0` divides by, whereas GF multiply uses a separate 64 KB two-byte-indexed table), so
      `matrix[i][j] = inverse(i XOR (m + j))` — what `CauchyReedSolomon.BuildCodingMatrix` computes. Vandermonde
      is ruled out: a power sequence needs index products through the multiply table, and the construction never
      touches it. Spec §6.2 `[X]`→`[C]`.
  - Method note, because it is reusable: this was settled by **static analysis of our own copy of the vendor
    binary**, not by a capture. The capture route is *blocked and should not be re-attempted* — verified
    first-hand this session: `cap3` is the only session with correlated stream keys and it never reached
    streaming (794 packets, all control, zero A/V), while `cap47` has 3,327 FEC packets and no keys dated
    anywhere near it. FEC is over plaintext, and AES-CTR keystream does not cancel out of the parity equation,
    so ciphertext-only units cannot settle the matrix either.
  - **What this did *not* settle at the time:** the GF primitive polynomial (below), since a correct matrix
    over the wrong field still reconstructs garbage — the pair only becomes safe together. That half closed
    the next day; see the entry below.
- [x] ~~**FEC GF primitive polynomial `0x11d`.**~~ — **SETTLED 2026-08-02 [V].** Confirmed by dynamic capture
      of the vendor client's own runtime field table. The dumped 0x200-byte block's **upper half** — the
      `table[value | 0x100]` inverse table the matrix construction indexes — reproduces the 0x11d inverse
      table on all 255 defined entries, with `a · inv[a] = 1` throughout; the other 15 primitive degree-8
      polynomials match at most one entry each, so there is no ambiguity to resolve. `GaloisField256` already
      assumed 0x11d, so nothing changed behaviourally — but it was an `[X]` propping up the whole FEC path,
      and it is now `[V]`. **FEC is fully settled: matrix form `[C]`, coded length `[C][W]`, field `[V]`.**
  - Method note: the breakpoint that worked was **not** the one previously planned. The old recipe targeted
    the Gauss-Jordan solve `FUN_1015f2a0`, which only runs on real erasure recovery and so was hard to hit.
    The matrix *builder* `FUN_101035e0` runs whenever a frame carries a parity unit — i.e. every frame — so an
    ordinary lossless local stream was enough, no induced loss required. Breaking on the inverse-table load
    inside it (`0x101036db`) puts the table base in a register with nothing left to dereference.
  - **Correction — the earlier "static search is exhausted, don't retry it" note was wrong, and the reason is
    worth internalising.** The polynomial *was* statically findable the whole time: the builder
    `FUN_1015eed0` carries it as a literal immediate at `0x1015ef5d`. The earlier sweep missed it because it
    searched for `0x11d`/`0x11b`/`0x187`/`0x163`/`0x12b` — the **full 9-bit polynomials** — whereas GF table
    builders conventionally fold the high bit into the shift and XOR only the **low byte**: `x <<= 1;
    if (x & 0x100) x ^= 0x1d`. Searching for `0x1d` as an `xor r32, imm8` finds exactly one site in the whole
    `.text`, and it is the right one. **Generalise: when scanning for a magic constant, scan for every form
    the arithmetic could store it in, not just the canonical one** — a negative result over one encoding is
    not a negative result.
  - This also **settles the "is it dynamic?" question**, which the dump alone could not: `FUN_1015eed0` is a
    `thiscall` with no arguments, no global reads, and a build-once guard, so the field is a fixed
    compile-time constant — not negotiated, not per-session, not per-console. Our hardcoded `0x11d` is
    therefore correct by construction rather than by luck.
  - **Corrected structural reading:** the table at field-object `+0x8` is not a "0x200 inverse table" — it is
    a 64 KB **divide** table `div[a<<8|b]`, alongside the 64 KB **multiply** table at `+0xc`, `log` at `+0`
    and a triplicated `exp` at `+4` whose pointer is biased `+0xFF` so division's negative indices work. The
    `table[value | 0x100]` the matrix builder uses is just row `a = 1`, i.e. `1 / value`. The 0x200-byte dump
    is `div[0]` and `div[1]`, and reproduces byte-for-byte from that layout. Spec §6.2 has the table.
  - The `0xff` the console stores for divide-by-zero is a sentinel where we throw; unreachable either way
    (`i < m ≤ m + j`, so `i XOR (m + j)` is never zero). `FecTests` pins the row (head and tail) so a future
    change of field fails loudly.
- [x] ~~**Senkusha probe sequence ordering**~~ — **SETTLED 2026-08-02 [W].** Verified rather than assumed:
      **handshake → RTT echo ×10 → MTU-in → MTU-out**, observed identically in two independent captures of
      our own. `session8` frames 142–154 (control), 155–175 (ten 548 B type-3 pings, each echoed), 180
      (type-2 1426 B downstream, console-originated), 187–188 (type-3 1226 B upstream); `cap47` frames
      955–977, 982, 990–991 with the same sizes on a different date and client port. Spec §6.4 has the table.
  - The **echo is a byte-for-byte reflection** — 10/10 payload-identical in `session8` — so RTT is all the
    exchange measures and a client need not parse the body.
  - **`cap47` justifies the "majority of 10" rule empirically**: its first ping went unanswered ~200 ms, was
    retried, and 11 were sent for 10 echoes. A probe demanding all ten would have failed a healthy session.
  - Worth keeping straight: **MTU-in is type `0x02` and console-originated; MTU-out is type `0x03`** reusing
    the echo mechanism. The legs are asymmetric in both base type and origin.
  - Scope: this is the order our sessions *do* follow, not proof it is mandatory. Treating probe failure as
    non-fatal remains our design choice, not a protocol requirement.
- [x] ~~**The control GMAC AAD rule.**~~ — **SETTLED 2026-08-02 [V].** Control zeroes the 4-byte key_pos field
      *in addition to* the tag; our `zeroKeyPos: true` was right. Recomputed offline over `cap3` with that
      session's dumped keys: **727/727 authenticated type-0 packets** (364 c2s + 363 s2c) reproduce the
      on-wire tag when tag+key_pos are zeroed. Zeroing the tag alone matches only the 2 packets whose
      `key_pos` is 0 — where the two rules are byte-identical and prove nothing. A wrong AAD cannot match a
      32-bit tag 727 times. Script: `captures/gmac_aad_rule.py`.
  - **Now an executable regression test, not just prose:** `LiveStreamPacketVectorTests` pins the rule
    against real captured packets (fixture `captures/stream_packet_vectors.json`, generated by
    `make_stream_packet_vectors.py`, gitignored — it holds real keys and packet bytes; the tests skip when it
    is absent). It asserts **both** directions: that tag+key_pos reproduces the wire tags, *and* that the
    tag-only A/V rule does **not**. The negative assertion is the one that pins the rule rather than merely
    "some AAD works" — the generator excludes `key_pos == 0` packets, where the two rules are byte-identical
    and discriminate nothing. Together with `LiveStreamKeyVectorTests` (the key schedule) the chain from ECDH
    shared secret to an authenticated packet is now covered end-to-end by tests.
  - **Why this was doable at a desk after all**, having been filed as needing hardware: `cap3` was written off
    during the input work *because* it carries no feedback traffic — every packet is base type 0. That is
    exactly what makes it right for a control-plane question. **When a capture is dismissed, the reason it was
    dismissed is scoped to that question, not to the capture.**
  - **Follow-up, same day: the RE-session-log "confirmed negative" on these key dumps is now RETRACTED — it was
    simply wrong.** `sendcrypt.bin`/`recvcrypt.bin` *do* reproduce through the KDF. Correct pairing:
    **`secret66.bin` (66-byte P-521 X, used whole) + `hkstruct.bin`**, reproducing both directions' key and
    IV byte-for-byte. Two independent faults caused the false negative, either fatal alone: (1)
    `stream_crypto_reimpl.py`'s `derive_channel()` carried a **stale SP800-108 length field** (`\x00\x01`
    instead of `\x01\x00`) — the C# was fixed 2026-07-22 but the Python reference never was, so the tool used
    to *test* the dumps disagreed with the implementation already known to be right; (2) the handshakeKey
    tried was `hkey.bin` = a struct header from a bad read, not
    `hkstruct.bin`. Both now fixed, and `validate_stream_keys.py` sweeps every (secret × non-zero
    handshakeKey) pairing on mismatch so it cannot recur.
  - **`cap3` is now the best-validated session we hold**: a complete verified chain from the ECDH X through
    the KDF to per-direction keys that then authenticate 727 real control packets. Useful as a regression
    fixture for the whole stream-crypto stack.
  - **Scope this correctly — the pairing was not a new discovery.** `LiveStreamKeyVectorTests.
    DeriveDirection_ReproducesDumpedConsoleKeys` has been loading `secret66.bin` + `hkstruct.bin` and
    passing all along. So the C# side already had it right and was verifying it on every run; what was
    wrong was the **Python reference and the RE-session-log entry**, which disagreed with a test that was green
    the whole time. The damage was real anyway — the prose is what people read when deciding whether a
    capture is usable, and it said cap3 was a dead end.
  - **Lesson worth carrying beyond this item:** a negative result from a reimplementation tests the
    reimplementation as much as the data. Before recording "the data is bad", re-confirm the tool still
    reproduces a known-good vector — and never conclude a pairing is absent from having tried only the
    *named* files. Corollary, and the sharper one here: **when a prose note and a passing test disagree,
    the test is the one telling the truth** — cross-check findings against the suite before writing them down.
  - Bonus, all confirmed in passing by the same run: the GMAC key fold (`sha256(k‖iv)` XOR-folded), the GMAC
    nonce (`baseIv + key_pos>>4`, little-endian), control tag@5 / key_pos@9, the Crypt-object layout
    (key@+0x30, IV@+0x40), and the direction mapping (send = client→server). This is the first end-to-end
    validation of control-plane packet crypto against real traffic with real keys.
- [x] ~~**The congestion GMAC AAD rule.**~~ — **SETTLED 2026-08-02 [V].** Same rule as control: zero the tag
      *and* the key_pos. Measured over `cap48`, **300/300** congestion packets reproduce with tag+key_pos
      zeroed and the tag-only A/V rule matches none. So control and congestion share a rule and **feedback is
      the odd one out**. Pinned by `LiveStreamPacketVectorTests.CongestionGmac_...`.
  - **This was never actually blocked**, and the false blocker cost a planned phase of a console trip. The
    entry said it needed "a capture that reaches streaming with correlated keys" — `cap48` had been exactly
    that for days, with its keys dumped in the same run and already validated (its recv key decrypts that
    capture's video to a real HEVC IDR). The claim descended from a capture inventory written *before* cap48
    existed and never revisited. **Check the dirty room against a capture-blocked claim before believing it.**


### Track D — Quality / latency leftovers
- [x] ~~**True HDR output.**~~ — **BUILT AND VERIFIED ON HARDWARE 2026-08-02.** End to end: the console
      honours `dynamicRange: "HDR"` and sends HEVC Main10 signalling PQ BT.2020; the renderer reads that from
      the decoder output type; `IDXGIOutput6` detection finds the panel; the swap chain is `R10G10B10A2` with
      `SetColorSpace1(G2084_P2020)`; and the video processor outputs PQ instead of tone-mapping. Confirmed on
      a Surface HDR panel — `10-bit P010 · PQ BT.2020 · HDR10 output (777 nits)`, with the previous
      `tone-mapped to SDR` gone and the washed-out look with it.
  - **The format choice was measured, not recalled.** A standalone DXGI probe showed that of the three
      candidate back-buffer formats only `R10G10B10A2` supports PQ on a **composition** swap chain —
      `R16G16B16A16_FLOAT` offers scRGB only, which would have forced a PQ→linear conversion for no gain.
      HDR10 also needs no transfer conversion (the source is already PQ) and is half FP16's bandwidth.
  - **No overlay work was needed**, contrary to the expectation that PQ would make the HUD blinding: the
      native renderer draws only video and the HUD is XAML composited over the `SwapChainPanel`.
  - **The stream carries no HDR static metadata, and that is correct** — established 2026-08-02 by asking
      twice, since either answer alone is ambiguous: the decoder's output media type carries no ST 2086 /
      MaxCLL attributes, *and* a direct scan of the HEVC SEI NAL units in the decrypted bitstream finds none
      either. So it is the console's encoder, not a Media Foundation decoder dropping it. Measured with PS5
      HDR set to Always On running an HDR title, which rules out the content simply being SDR. This is
      unremarkable: static metadata describes a *mastering display*, which real-time rendered content does
      not have. **PQ + BT.2020 signalling is the whole of the HDR delivery here**, PQ being absolute so a
      panel can map it without metadata. Consequences worth not re-deriving: there is no MaxCLL to drive any
      future tone-mapping decision, and we deliberately do **not** call `SetHDRMetaData` — inventing values
      would be worse than letting the display use its own defaults.
  - **Remaining, all minor:** capability is probed once at device creation, so toggling Windows' "Use HDR"
      or dragging to another monitor mid-session is not noticed (`MaxLuminance` also drifts with power state
      — 686 vs 777 nits across two runs, so a re-probe would want to refresh it too). The Ally X is still not
      a valid test target: it reports HDR *video playback* only, not apps/games.
- [x] ~~**Source-generated JSON contexts** (task #33)~~ — **DONE 2026-08-02 for the LAN path.** Converted the
      interop-constants bundle, control secrets fixture, pairing credential store, settings store, paired
      console list and registration fixture. `Ripcord.Cloud.Halyard` is deliberately left (see Track B).
  - **The rationale in this entry was understated, and the correction is the interesting part.** It read as a
    size/latency item and "removes reflection from the launchSpec path" — which a 2026-08-02 audit first
    flagged as *false*, on the grounds that `BuildLaunchSpecJson` is hand-built string concatenation with no
    serializer in it. A refutation pass then showed the original entry was right and the audit wrong: the
    launchSpec *path* reaches reflection twice, and the second one matters — `CryptStreaminfo` needs
    `HalyardControlSecrets`, which is deserialized. Under trimming those return null, `IsControlEstablished`
    goes false, and **the launchSpec goes on the wire unencrypted**. This was never a size item.
  - **Cost a real bug on the way, worth not re-learning.** For a record whose properties are all `init`, the
    source generator assigns *every* property in the contract via an object-initialiser, so a property absent
    from the JSON is written as `default(T)` rather than keeping its initialiser value — reflection keeps it.
    A settings file from an older build would therefore have silently reset every setting it did not mention,
    on upgrade. Measured: `init` → 0, `set` → 60, positional record with defaults → 60, `init` +
    `[JsonConstructor]` → 0. `RipcordSettings` now uses `set` and
    `SettingsStoreTests.PartiallyUnknownFile_KeepsWhatItCanAndDefaultsTheRest` pins it.
- [x] ~~**ARM64 has no carry-less GHASH path**~~ — **DONE 2026-08-01.** `GfMulCarrylessArm` implements the
      multiply with NEON PMULL/PMULL2 (`Arm.Aes.PolynomialMultiplyWideningLower/Upper`), dispatched from
      `GfMul` after the x86 check. Found by running the app on ARM64 for the first time: the stream collapsed
      to 74.7% reported packet loss with the A/V receive queue pegged at its 512 capacity, and the console was
      asked to step down to 540p30 @ 2 Mbps — all of it self-inflicted, on an idle 4.7 ms LAN.
  - Measured on the Snapdragon X2 dev box, A/V per-packet crypto (verify + CTR-decrypt, 1426 B packet):
    **~200 µs → ~12-15 µs (13-17x)**, i.e. a single-core ceiling of ~57 Mbps → 750-1000 Mbps (the spread is
    run-to-run variance; treat the order of magnitude as the result). GHASH was **87%** of
    the per-packet cost (~1.90 µs per multiply × ~91 blocks). At the observed 25.9 Mbps the headroom went
    from 2.2x to 28.5x — and 2.2x on a microbenchmark is under 1x in the real loop, which is what the pegged
    queue was telling us.
  - The x64 PCLMULQDQ path is deliberately left byte-for-byte untouched: the two are near-duplicates rather
    than one generic body, because this host cannot execute the x64 tests and that path is the shipping one.
    See the note above `GfMulCarrylessArm`.
  - `GfMulHardwareTests` is now a theory over `HardwarePath` (`X86Pclmulqdq` / `ArmPmull`), each case skipping
    on hosts that lack it, so neither architecture can report green for a path it never ran. ARM64 suite:
    417 passed / 5 skipped (the 5 skips are the x86 multiply cases).
  - **Verified live against the console on the same hardware**, and it clears the pre-fix baseline rather than
    merely matching it: **1920x1080p60**, 23.2 Mbps, handshake 2.1 ms, RTT 6.5 ms, **loss 0.4%**, 18 ms
    demux→present, decode queue 0, receive queue 0-18, zero-copy HEVC on the Adreno X2-45. Per-packet crypto
    is now ~2.7% of one core at that bitrate (was ~45%), and the loss figure is finally measuring the network
    instead of measuring us. The pre-fix run could not hold 720p.
- [x] ~~Frame pacing~~ — deprioritised. The present-fps peak of 83 that justified it was a shed-then-burst
      symptom of the crypto bottleneck; post-fix it's 61 (peak 62), locked to target on x64. ARM64 at 1080p60
      peaks at 63, i.e. mildly burstier — same signature as the 0-18 receive-queue oscillation above, and it
      points at the allocation churn item, not at pacing. Don't reopen this one on the strength of a 63.


### Track F — UX
- [x] ~~**Console list + onboarding redesign.**~~ — **DONE 2026-08-04.** The two surfaces a user meets first
      were still scaffolding: `ConsolesPage` was a bare `ListView` giving every console the same generic
      glyph and the literal name `"PlayStation 5"` (two PS5s were distinguishable only by IP), and adding one
      was a flat six-field `ContentDialog`. Now a card grid — vendor mark, the console's own name, family +
      address, status, and a primary action that says "Connect" or "Wake & connect" — with the whole card as
      one focus stop and secondary actions in an overflow reachable by right-click, menu key or pad. Adding a
      console is a five-step page: family → find → link → pair → name. Consoles can be renamed, and the store
      grew nickname / reported-name / host-id / system-version / last-played fields (all nullable, so an
      existing `consoles.json` loads untouched). Three bugs fixed on the way: **the LAN scan was PS5-only**
      (it built a `HalyardSearchClient` with no profile, which silently defaults to PS5, so choosing PS4 in
      the dropdown could never find anything), results did not stream (a blind 4 s wait), and `HostId` /
      `SystemVersion` were parsed off the wire and then discarded. Also the app's first shared resource
      dictionary (`src/Ripcord.App/Styles/Ripcord.xaml`) and its first motion, gated on
      `UISettings.AnimationsEnabled`.
- [x] ~~Consider hiding the always-visible exit/diagnostics buttons behind the flyout.~~ — **DONE.** The
      diagnostics, save-diagnostics and exit buttons now live inside the auto-hiding touch bar (collapsed in
      markup, revealed on pointer/touch, hidden again after ~4 s idle); no exit or diagnostics affordance is
      permanently on screen. One deliberate deviation from "unobstructed until touched": the bar is shown once
      on stream entry for discoverability, so it is visible for the first few seconds.
- [x] ~~Keyboard support with configurable mapping~~ — done (task #29), committed. Bindings cover both key map
      and gamepad remap; Escape/F3/F11 are reserved and can't be bound over.
- [x] ~~Diagnostics overlay rewrite~~ — done (commit `8732901`): sparklines, capability pills (HDR/HEVC/
      zero-copy), headroom bar, scrolls on small screens, F8 export to file for machines with no debugger.
- [x] ~~About page rebuild + a real logo~~ — done. The About page now reports Windows edition/build/UBR, the
      GPU a stream would actually use, hardware-decode and HEVC as pills, and copies the lot to the clipboard
      for bug reports. The WinUI placeholder icons are gone: every shipped asset is generated from
      `brand/*.svg` by `brand/generate-assets.ps1` (headless Edge + a hand-rolled `.ico` writer), including a
      separate pixel-aligned small cut for ≤32 px. See `brand/README.md` for the palette and the trademark
      constraint on the three dash colours.

### Track G — Deferred phases
- [x] ~~Phase 2 — PS4 support~~ — **closed 2026-08-05.** Discovery, wake, control-field crypto, stream
      handshake, key schedule and registration-from-scratch are all `[V]`/`[W]`, and a live pairing run from
      the Ripcord UI succeeded against a real PS4. No PS4 item is open; see the Phase 2 entry above.
