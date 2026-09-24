# PS5 Remote Play — PSN cloud API (the decrypted tier-1 surface)

> **Scope note.** The API shape recorded here — endpoints, methods, ordering, field names — is accurate and
> current; `Ripcord.Cloud.Halyard` is built from it. Two framings in the original draft were wrong and are
> corrected inline below: (1) this tier is **not** a `v2` feature. Cloud/PSN authentication is the *account*
> path and is orthogonal to which control plane the console speaks — a `v1` LAN session needs none of it.
> (2) The `data1/2/3` seeds were guessed to bootstrap the *direct* (`v1` LAN) session's crypto; they do
> **not** — a LAN session establishes fine against a console with no internet. But `data1`/`data2` were not
> idle either: **they are the account/no-PIN registration seed-delivery key/material, now solved `[C]`** —
> see "the no-PIN registration seed" below and `ps5-session-establishment.md`. Cloud *play* — streaming from
> a datacentre rather than your own console — would be a genuinely different protocol, but we have **no data
> on it** and nothing here describes it.

Status: draft, from three TLS-intercepted capture sessions (see provenance log). Every prior capture
saw the PSN cloud tier only as opaque TLS (see `ps5-network-architecture.md` tier 1). These sessions
were captured through a local TLS-terminating debugging proxy on the author's own machine and
account, which makes the cloud HTTPS request/response bodies readable. The third session was a full
cold sign-in from a signed-out state (passkey via QR code, consent choice, console-list, connect),
which adds the initial-auth, cloud-discovery, and candidate-signaling pieces below. This document
records the **API shape** (endpoints, methods, request/response structure, ordering) - not any
values.

Source: own captures of the author's own PSN account traffic from the author's own machine, via a
locally-trusted debugging proxy. Captures and this document written 2026-07-12/07-13. No source code
from any existing Remote Play client project was consulted - see `docs/protocol-research-log.md`.

**Redaction note - this document is the most sensitive in the set and the rules are strict.** The
raw captures contain *live* credentials and personal data: OAuth access/refresh tokens **and
authorization codes**, **WebAuthn/passkey challenge material**, account identifiers (numeric account id,
hashed account id, online ID), date of birth, **console and client device unique IDs (`duid`)**, **console
names**, **candidate IP addresses (LAN and reflexive/relay)**, session UUIDs, and push-context UUIDs.
**None of these are reproduced here or anywhere in the repo**, and the raw `.saz`/pcap files are gitignored
and never committed. Only endpoint paths, method names, field *names*, and structural shape are documented.
Placeholders like `<token>`, `<accountId>`, `<duid>`, `<uuid>`, `<ip>` stand in for real values throughout.

**One value from these captures is deliberately committed, and it is not in the list above:** the vendor
desktop client's own OAuth `client_id`/`client_secret`, which ships populated in
`src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json`. It authenticates an *application*, not a person
— identical for every user, tied to no account and no console — which is why it is not personal data and why
it is treated separately here. That is a deliberate, argued exception, not an oversight: see
[`NOTICE`](../../NOTICE), which makes the case on its own grounds, and
[`CLAUDE.md`](../../CLAUDE.md)'s "Bounded exception 2". Nothing else from these captures is committed, and no
*user* credential is: the signed-in account's refresh token lives encrypted in the user's own
`account.json`.

## Why this matters

The direct-console protocol (the UDP/RUDP or TCP session in the other specs) is only half the story.
Before any of that happens, the client runs a sequence of PSN cloud calls that authenticate the
user, look up account/region context, and - critically - **coordinate the remote-play session
through a cloud "session manager" that notifies the console out of band**. Understanding this tier
is what turns "we can talk to a console we already have the address and key for" into "we can do
what the official app does from a cold start."

## Initial sign-in: OAuth2 authorization-code flow (with passkey)

Observed on a cold sign-in from a signed-out state. The app delegates sign-in to Sony's account web
flow, obtains an authorization code, and exchanges it for tokens:

1. `GET ca.account.sony.com/api/v1/oauth/authorize?...` → `302` to the sign-in web UI on
   `my.account.sony.com`. (Interspersed POSTs to obfuscated `my.account.sony.com/...` paths are
   Akamai bot-detection sensor traffic, not part of the auth protocol.)
2. **Passkey / WebAuthn sign-in** (the QR-code-on-phone method): `POST
   ca.account.sony.com/api/authn/v3/sso/passkeyChallenge` → `201` returning a WebAuthn
   `credential_options` blob (`challenge` bytes, `rpId: my.account.sony.com`,
   `userVerification: required`, ~120 s timeout) plus an `authentication_ticket`. The QR code is
   cross-device (hybrid) WebAuthn - the phone holds the passkey and signs the challenge. The
   assertion is verified via `POST web.np.playstation.com/api/accountManagement/v3/users/passkey`
   and `POST ca.account.sony.com/api/authn/v3/sso`. This is standard FIDO2/WebAuthn; nothing
   Sony-specific to reimplement beyond driving the endpoints.
3. `POST ca.account.sony.com/api/v1/oauth/authorizeCheck` → `204`, then
   `GET ca.account.sony.com/api/v1/oauth/authorize?...` → `302` carrying the authorization code.
4. **Redirect catch**: `GET https://remoteplay.dl.playstation.net/remoteplay/redirect?code=<code>&cid=<uuid>`
   - the app's OAuth `redirect_uri` is that `remoteplay/redirect` URL, and this is how the native app
   receives the code from the web flow.
5. **Code exchange** (same token endpoint the refresh grant uses):

   ```
   POST https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token
   grant_type=authorization_code&code=<code>&redirect_uri=https://remoteplay.dl.playstation.net/remoteplay/redirect&device_type=PC_APP&duid=<clientDuid>
   ```

   → the initial `{ access_token, refresh_token, ... }`. The client passes its own device unique id
   (`duid`) and `device_type=PC_APP`. Thereafter the app uses the refresh-token grant below.

(There is a *separate* set of `ca.account.sony.com/api/authz/v3/oauth/token` calls during this flow -
those are the account web app's own internal tokens, not the Remote Play client's. The client's
token is the `auth.api.sonyentertainmentnetwork.com/2.0/oauth/token` one.)

A **consent / data-reporting** step also runs here (the app prompts for a data-sharing level):
`GET privacytemplate.api.playstation.com/.../data_profiling_onboarding` and
`GET privacysettings.api.playstation.com/v2/users/me/settings?...` fetch the template and current
settings. No obvious cloud *write* of the chosen level ("limited") was observed - it appears to be
applied client-side as a telemetry flag (governing the `sbahn:pc.telemetry.publish` scope). Not
protocol-relevant to remote play.

## Auth: OAuth2 refresh-token grant (subsequent runs)

```
POST https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token
Authorization: Basic <app client_id:client_secret, base64>   ← the app's own fixed credential, not the user's
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token&refresh_token=<token>&scope=<space-separated scopes>
```

Response: `200 OK`, JSON `{ access_token, token_type: "bearer", refresh_token, expires_in, scope }`.
`expires_in` was ~3600 s (one hour). The returned `refresh_token` is long-lived and is what the app
persists between runs; the sign-in "SSO" the user experiences is really "the app still holds a valid
refresh token."

The **scopes** requested are the most useful part - they enumerate the cloud services the client
uses, and map directly onto the other calls below:

- `psn:clientapp`
- `referenceDataService:countryConfig.read`
- `pushNotification:webSocket.desktop.connect` — the push channel (see below)
- `sessionManager:remotePlaySession.system.update` — the remote-play session manager (see below)
- `sbahn:pc.telemetry.publish` — a telemetry sink

Every subsequent call carries `Authorization: Bearer <access_token>`.

**We do not reproduce the app's Basic client credential.** It is a fixed secret embedded in the
official client; a clean-room implementation must obtain/register its own client credential rather
than reuse Sony's (both for cleanliness and because it isn't ours to embed).

## Account/identity and profile lookups

Run once per sign-in, in this order, before any console connection:

| Purpose | Endpoint | Notes |
|---|---|---|
| Account info | `GET vl.api.np.km.playstation.net/vl/api/v1/s2s/users/me/info` | Returns account id, a hashed account id, **region**, language, online ID, and age/parental-control fields. Responded `201`. **This is an identity/account-context endpoint, not a key-management one** - see the correction note below. |
| Profile base URL | `GET asm.np.community.playstation.net/asm/v1/apps/me/baseUrls/userProfile` | Service-discovery: returns the base URL to use for the profile call. |
| User profile | `GET <profileBaseUrl>/userProfile/v1/users/<onlineId>/profile?fields=avatarUrl,personalDetail,plus&...` | Avatar, PS-Plus status, display details. |
| Avatar image | `GET image.api.playstation.com/profile/images/...` | The avatar PNG. |

**Correction to earlier docs**: prior specs (written before this tier was decryptable) inferred
that the host containing `.km.` was a "key-management" service issuing the console **registration
key**, because its name pattern and timing looked the part. Decrypted, it is plainly an
account-info endpoint (`users/me/info`) returning identity/region/age data - **no registration key
is present in its response, and none is fetched from the cloud at all on a reconnect** (the
registration key is held client-side after first pairing; see `ps5-session-establishment.md`). The
`.km.` = key-management guess is withdrawn. Where the registration key *originates* (first-time
pairing) is still not directly observed - it is not in any reconnect capture, cloud or direct.

## Console list — cloud discovery of the account's consoles

This is how the app populates "the list of PS5s on your account" (the third capture selected a
console from this list). It is a genuine cloud-discovery mechanism, distinct from LAN mDNS/SRCH
(`ps5-local-discovery.md`):

```
GET https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/clients?platform=PS5&includeFields=device&limit=5&offset=0
→ { "clients": [ {
      "device": { "name": "<console name>", "language": "en-US",
                  "wakeupEnabledPowerModes": ["networkStandby","mainOnStandby"],
                  "enabledFeatures": ["remotePlay"], "updatedDateTime": "..." },
      "duid": "<consoleDuid>", "platform": "PS5",
      "deviceProperties": [], "version": "7", "updatedDateTime": "..." }, ... ] }
```

Per-console fields worth noting:
- `name` — the console's display name (same value the LAN `SRCH` response returns as `host-name`,
  see `ps5-local-discovery.md`).
- `duid` — the console's device unique id, used to target it in the connect command and signaling
  below. **Not** reproduced (it's a stable hardware identifier).
- `wakeupEnabledPowerModes` — which standby modes allow remote wake-up (e.g.
  `["networkStandby","mainOnStandby"]`, or `[]` if remote wake is disabled). Directly tells the app
  whether it can wake a sleeping console. In the capture, one of the account's two consoles had wake
  enabled and the other had it disabled.
- `enabledFeatures` — includes `"remotePlay"` when the console is set up for it.

`platform=PS5` filters to PS5s; PS4s would presumably use a different filter value. This endpoint is
the cloud equivalent of "which of my consoles can I remote-play, and can I wake them", and is the
right basis for Ripcord's console picker when signed in.

## Client self-update check

```
GET https://remoteplay.dl.playstation.net/remoteplay/module/win/rp-version-win.json
→ { checksum, uri (installer .exe), version }
```

A plain version manifest the client compares against its own build to offer updates. The observed
client build was in the 9.x range. Also seen: repeated `HEAD /remoteplay/redirect` requests
(connectivity/liveness checks) sprinkled throughout.

## Push channel address

```
GET https://mobile-pushcl.np.communication.playstation.net/np/serveraddr?version=2.1&fields=keepAliveStatus&keepAliveStatusType=3
→ { fqdn: "<hostname>-pushcl.np.communication.playstation.net",
    keepAliveStatus: { clientKeepAliveInterval: 10000, clientKeepAliveTimeout: 40000,
                       serverKeepAliveTimeout: 30000, serverPresenceTimeout: 30000 },
    retryIntervalMin, retryIntervalMax }
```

Returns the specific push-front-end hostname the client should connect to, plus keepalive timing
(client pings ~every 10 s; ~30-40 s timeouts). The client then opens a persistent push connection
to that host (the `pushNotification:webSocket.desktop.connect` scope, i.e. a WebSocket). This push
channel is the plausible out-of-band path by which the console is notified of an incoming
remote-play session (see next section) - the console isn't contacted directly by the client at this
stage.

## The remote-play session manager (the core of "connect")

This is the cloud coordination point for a remote-play session. Base:
`https://web.np.playstation.com/api/sessionManager/v1/remotePlaySessions`.

### Create / join — `POST remotePlaySessions` → `201 Created`

Request body (structure):

```json
{ "remotePlaySessions": [ { "members": [ {
    "accountId": "me", "deviceUniqueId": "me", "platform": "me",
    "pushContexts": [ { "pushContextId": "<uuid>" } ]
} ] } ] }
```

The client adds **itself** (`"me"` placeholders resolve server-side from the bearer token) and
supplies a `pushContextId` tying this session to its push connection. Response body:

```json
{ "remotePlaySessions": [ {
    "sessionId": "<uuid>",
    "members": [ { "accountId": "<accountId>", "platform": "REMOTE_PLAY",
                   "deviceUniqueId": "<clientDeviceId>" } ]
} ] }
```

Notable: the `POST` body itself **does not name the console**. The session is account-scoped, and
the client is added as a member. The console is targeted separately - by its `duid` - in the *wake
command* and the *signaling messages* documented below, both of which reference this session's
`sessionId`. The console's connection details (its candidates) are not published in the session
readback; they arrive as an `ANSWER` over the `sessionMessage` signaling channel (see "Candidate
exchange" below). So the earlier open question ("where does address/candidate exchange happen") is
now answered: it happens over `remotePlaySessions/<id>/sessionMessage`, not in the session object.

### Read — `GET remotePlaySessions` (and `?view=v1.0`) → `200 OK`

Returns the current session object(s) for the account: `sessionId`, `createdTimestamp`, and the
`members` array (accountId, onlineId, platform, deviceUniqueId, joinTimestamp).

### Leave / disconnect — `DELETE remotePlaySessions/<sessionId>/members/me` → `204 No Content`

Removing yourself from the session is the disconnect. **The request is bare - no body, no query
parameters.** This is significant for one specific question:

## The "put console to sleep on disconnect" choice is NOT a cloud call

The first capture deliberately ended by choosing "put the console into rest mode" at disconnect.
There is **no** corresponding cloud API call: the only disconnect-related request is the bare
`DELETE .../members/me` above, which carries no "sleep"/"rest" flag anywhere. This corroborates the
hypothesis from `ps5-wan-relay.md` (which reached the same conclusion from the encrypted side): the
rest-mode instruction is delivered to the console over the **direct control channel** (the `RPCS`
teardown), not through PSN's cloud, or is inferred by the console when its last remote-play member
leaves. Either way, an implementation should not expect a cloud endpoint for it.

## Waking / triggering the console — `cloudAssistedNavigation/.../commands`

After creating the session, the client sends a command that reaches the selected console (targeted
by `duid`) and tells it to start a remote-play session:

```
POST https://web.np.playstation.com/api/cloudAssistedNavigation/v2/users/me/commands
{ "commandDetail": {
    "platform": "PS5", "duid": "<consoleDuid>", "commandType": "remotePlay",
    "parameters": { "initialParams": "<json string>" },
    "messageDestination": "SQS" },
  ... }
→ 202,  { "commandId": "<uuid>" }
```

`messageDestination: "SQS"` indicates the command is delivered to the console via an Amazon SQS
queue (PSN's server-side message fan-out to the console). The nested `initialParams` (a JSON string)
carries the fields that seed the *direct* session:

- `accountId`, `roomId` (0), `sessionId` (the `remotePlaySessions` id), `clientType` (`"Windows"`).
  **`accountId` is a JSON *number*, not a string** `[C]` — a bare 19-digit integer in cap96/cap97/cap107, and
  the only field in this body whose *type* differs from the obvious serialization. Note also that only these
  six fields appear (`accountId`, `roomId`, `sessionId`, `clientType`, `data1`, `data2`); `data3`,
  `supportCmd` and `protocolVer` are **not** in this call in any capture, despite appearing in the field list
  below — that list was written from an earlier reading and the three captures settle it.
- Every cloud REST call carries **`User-Agent: RpNetHttpUtilImpl`** `[C]`.
- `supportCmd` (`"130000"`) and `protocolVer` (`"1.0"`) — **the same values that appear as
  `RP-SupportCmd` and `RP-Version` in the direct console handshake** (`ps5-session-establishment.md`).
- `data1`, `data2`, `data3` — three base64 values, 16 bytes each. High-entropy; these read as
  nonces/seed material handed to the console. ~~**These are the strongest lead yet toward the
  direct-session key agreement** - the material that seeds the `RP-Pubkey`/`RP-Nonce`/ECDH exchange is
  being distributed here, in a channel we can read.~~ Values not reproduced (real crypto material);
  noted structurally.

  > **Two readings, one wrong and one right.** The *direct/LAN* key agreement uses none of this — `v1`
  > derives its control key from `RP-Registkey` + the console's `RP-Nonce`, and a session establishes against
  > an offline console. So "these seed the direct-session crypto" is wrong. **But `data1`/`data2` are the
  > account/no-PIN registration seed-delivery key/material — solved `[C]` 2026-08-31** (§ "The no-PIN
  > registration seed" below): the client generates two ephemeral 16-byte values and sends them here as
  > `data1` (the field-cipher **key**) and `data2` (the field-cipher **material**); the console encrypts the
  > registration seed with them and returns it as `customData1`. (`data3`'s role remains **open [X]**.)

### The no-PIN registration seed — `data1`/`data2` + `customData1` `[C]`

The account ("web") registration route delivers its transport seed **through the cloud, encrypted**, and it
is the field cipher (`HalyardControlFieldCrypto`) all the way down. Verified end-to-end against the vendor
client's own `session+0x10` (2026-08-31; live values in the dirty-room note, not reproduced here):

1. The client generates two **ephemeral 16-byte** values and sends them in the `commands` call as `data1`
   (the field-cipher **key**) and `data2` (the field-cipher **material**). These are per-connect and
   client-chosen — high-entropy, distinct every time, which is why earlier reconnect captures read them as
   "ephemeral, unattached."
2. The **console generates the 16-byte registration seed**, field-encrypts it with
   `HalyardControlFieldCrypto(key = data1, material = data2, counter = 0)` — i.e.
   `IV = HMAC-SHA256(contextKey, data2 ‖ be64(0))[:16]`, AES-128-CFB — and returns the ciphertext as
   **`customData1`** (a **double-base64** value: base64 of a base64 string) on the `rps:customData1:updated`
   push channel.
3. The client base64-decodes `customData1` twice → the ciphertext, decrypts it with the same
   `HalyardControlFieldCrypto(data1, data2, 0)` → **the registration seed**.
3b. **Live-confirmed from a console 2026-09-03 `[V]`.** Ripcord's own client completed steps 1–3 against real
   hardware: the console joined the session ~0.75 s after the command, published `customData1`, and our
   decrypt reproduced a usable seed. Previously this was verified only against the vendor client's own
   `session+0x10`. Two details worth recording: the ciphertext is **17 bytes** for the 16-byte seed in every
   captured *and* live value (take the first 16), and the client must **leave its session** when done — the
   vendor's final push frame is a `members:deleted` for itself.

4. That seed is the no-PIN analog of the PIN route's client nonce: `key' = seed XOR
   registrationTable[ requestContext[0x18d] & 0x1f ]` is the transport key that decrypts the `/sess/rgst`
   response (registkey + companion) — see `ps5-session-establishment.md`.

Every primitive is one we already own (`HalyardControlFieldCrypto` + the bundled `contextKey`); the seed is
**received, not derived**, which is why it appears in no plaintext channel and matched no local KDF. This
retro-corrects the earlier "`data1/2/3` open `[X]`" and "`customData1` open `[X]`" readings: they were the
ephemeral key/material and the encrypted seed the whole time.

This command is what makes a *sleeping* console wake and connect (subject to
`wakeupEnabledPowerModes` from the console-list call) `[V]` — but it is not reliable, and the failure has a
specific cause worth knowing before chasing it.

> **Confirmed, and then mis-diagnosed once already.** The command waking a console in standby is observed:
> the RE log records it waking the same console repeatedly across one day's runs, and account pairing
> completing end to end against that console (2026-09-04). An earlier revision of this section marked the
> sleeping half `[X]` on the strength of four consecutive failures from the app — which was wrong, and is
> recorded here because the four failures have a documented cause that looks exactly like a wake that does
> not work.
>
> **The half-open-session trap.** An attempt that fails *after* the console has joined leaves the console
> holding a Remote Play session that nobody is in. It then reports Remote Play as in use, refuses its
> pair-device page, will not join a further cloud session, and shows **none** of the banners a real stream
> shows. Every subsequent attempt fails as `the console never joined the session`, indefinitely — including
> attempts that would otherwise have woken it. Two things do **not** clear it: leaving `members/me` (which
> does not evict the console) and deleting the session (PSN answers **405**). A completed session clears it;
> otherwise the console needs restarting.
>
> A LAN `connect` that exits without a clean disconnect strands the console the same way, so a failed
> stream can block the account route afterwards.
>
> **And it is genuinely flaky beyond that.** Also recorded: `canWake=True` from the console list, the
> command accepted, and six discovery polls finding the console still asleep — with rest-mode settings
> unchanged and supported. The standing advice from that session is the right order to work in: treat a
> console that will not join as a power-state question first.
>
> **Not the PS4 wake endpoint.** `POST {userProfileBase}/userProfile/v1/users/{onlineId}/remoteConsole/
> wakeUp?platform=PS4` exists and is a separate, dedicated wake call, reached after a
> `GET asm/v1/apps/me/baseUrls/userProfile` lookup. It is **PS4-only**: the format string in the vendor
> control library hard-codes `platform=PS4`, and no PS5 capture of ours contains the call. For PS5 the
> `commands` POST above is the wake. Recorded so the endpoint is not mistaken for a missing PS5 step.
## Candidate exchange / signaling — `remotePlaySessions/<id>/sessionMessage`

This is the ICE-style candidate negotiation, carried **in cleartext over HTTPS** (not raw STUN) -
the piece that was opaque when only the UDP tiers were captured. The client `POST`s signaling
messages addressed to the console (`to: [{accountId, deviceUniqueId: <consoleDuid>, platform:
"PS5"}]`) on a named channel:

```
POST https://web.np.playstation.com/api/sessionManager/v1/remotePlaySessions/<sessionId>/sessionMessage
{ "channel": "remote_play:1",
  "payload": "ver=1.0, type=text, body=<json>",
  "to": [ { "accountId": "<accountId>", "deviceUniqueId": "<consoleDuid>", "platform": "PS5" } ] }
→ 202 Accepted
```

The `payload` `body` JSON carries an `action` and, for the offer, a `connRequest`:

```json
{ "action": "OFFER", "reqId": 1, "error": 0,
  "connRequest": {
    "sid": 1, "peerSid": 0, "skey": "<base64>", "natType": 2,
    "candidate": [
      { "type": "STATIC", "addr": "<reflexive/relay ip>", "mappedAddr": "0.0.0.0", "port": <p>, "mappedPort": 0 },
      { "type": "LOCAL",  "addr": "<client LAN ip>",      "mappedAddr": "0.0.0.0", "port": <p>, "mappedPort": 0 } ],
    "defaultRouteMacAddr": "", "localPeerAddr": { "accountId": "<accountId>", "platform": "REMOTE_PLAY" },
    "localHashedId": "<base64>" } }
```

Key points:
- The client advertises **both** a `LOCAL` candidate (its LAN address) and a `STATIC` candidate (its
  reflexive/relay address, on the same port) — this is the source of the "always offers LAN + WAN"
  behavior observed from the packet side (`ps5-network-architecture.md`). The reflexive/relay
  address is exactly the WAN peer seen in the STUN/relay tier, so the STUN tier's job is to *gather*
  this candidate and the `sessionMessage` channel's job is to *exchange* it.
- `natType`, `skey` (a session key field - all-zeros in the OFFER, i.e. filled in later),
  `localHashedId` round out the connection request.
- The exchange is a short state machine, observed as: `OFFER (reqId 1, retried while the console
  wakes/joins)` → `ACCEPT (reqId 2)` → `RESULT (reqId 3)` → a further `OFFER`/`RESULT` round →
  then the direct UDP/TCP session takes over. The console's `ANSWER`/replies come back over the same
  channel (the push/WebSocket side), delivering the console's own candidates.

  **Client-side detail, re-read 2026-08-07** (session 6, requests 128–139 — all of them client→server, which
  is itself the point):
  - The client sends `OFFER` and `RESULT` only. Every `ACCEPT`/`ANSWER` in the sequence above is inferred
    from the reply the client is evidently answering; **none of them appear in HTTP**, which is consistent
    with the push channel carrying the console's whole side.
  - A `RESULT` carries an **empty** `connRequest` (`{}`). It is a bare acknowledgement, not a candidate
    carrier — so nothing is lost by not implementing it until the inbound half exists.
  - Two `OFFER` field values that look like placeholders a reimplementation would be tempted to "fix" are
    real and were verified rather than assumed: `skey` is 16 **zero** bytes at this stage, and `mappedAddr`
    is the literal string `"0.0.0.0"` on both candidates. `defaultRouteMacAddr` is sent as an empty string
    rather than omitted.

**This is the missing half of the rendezvous.** Combined with the STUN/DTLS tier (candidate
*gathering*) and the direct session (`ps5-session-transport.md`), it completes the "how do client
and console find each other and agree on a path" story, and it does so in a readable HTTPS channel
rather than requiring us to decode the encrypted UDP signaling.

## Full observed ordering (one connect cycle)

0. *(First run only)* OAuth2 **authorization-code** flow with passkey/WebAuthn → initial tokens (see
   "Initial sign-in"). Subsequent runs skip straight to step 1.
1. `POST oauth/token` (refresh-token grant) → bearer token.
2. `GET km .../users/me/info` (account/region context).
3. `GET asm .../baseUrls/userProfile` + `GET .../profile` + avatar image (profile hydration).
4. `GET remoteplay.dl .../rp-version-win.json` (self-update check).
5. `GET cloudAssistedNavigation/v2/users/me/clients?platform=PS5` (console list — the picker).
6. `GET .../np/serveraddr` (push host) → open persistent push (WebSocket) connection.
7. `POST remotePlaySessions` (create/join, with pushContextId) → session created.
8. `POST cloudAssistedNavigation/v2/users/me/commands` (`commandType: remotePlay`, targets console
   by `duid`, carries `sessionId` + `supportCmd`/`protocolVer` + seed material) → wakes/triggers the
   console via SQS.
9. `POST remotePlaySessions/<id>/sessionMessage` signaling: `OFFER` (with LAN+reflexive candidates,
   retried) → `ACCEPT` → `RESULT` round-trips; the console's candidates come back over the push
   channel.
10. *(direct-console UDP/TCP session happens here — see the other specs; not visible to a TLS proxy)*
11. `DELETE remotePlaySessions/<id>/members/me` (disconnect).

Steps 1-4 also run when the app merely starts up and shows account info (confirmed by the
NAT-status-only capture in an earlier session); steps 5-11 are specific to listing and connecting to
a console.

## What this changes for the plan

- **Phase 1 "sign-in + connect" is substantially an HTTPS client**, and it is now specified nearly
  end to end: OAuth2 (authorization-code first run, then refresh-token), the
  `cloudAssistedNavigation/.../clients` console list, the `remotePlaySessions` create/delete
  lifecycle, the `cloudAssistedNavigation/.../commands` wake/trigger, and the
  `sessionMessage` OFFER/ACCEPT/RESULT candidate exchange. Together these are enough to drive a
  connection from a cold, signed-out start against real hardware - the biggest newly-tractable
  chunk of Phase 1.
- **Sign-in can lean on standard WebAuthn/passkey** by driving Sony's account web flow (the QR code
  is cross-device FIDO2); we don't reimplement Sony's auth, we drive its endpoints and catch the
  code at the `remoteplay/redirect` redirect_uri. We still must **register our own OAuth client
  credential** rather than reuse the official app's embedded one.
- **Cloud discovery exists** (`clients?platform=PS5`) alongside LAN mDNS/SRCH, and it uniquely
  reports remote-wake capability per console (`wakeupEnabledPowerModes`). Ripcord's console picker
  should use it when signed in and fall back to / combine with LAN discovery.
- **The candidate exchange is readable** (the `sessionMessage` channel), which means we can
  implement the rendezvous without decoding the encrypted UDP signaling - a large de-risking of the
  connect path.
- The **`commands` seed material (`data1/2/3`) and the `sessionMessage` `skey`** are the concrete
  lead toward the direct-session key agreement. This is where a future decryption effort should
  start, and it's in cleartext HTTPS rather than buried in encrypted UDP.
- The **`.km.` = key-management assumption is wrong** and is corrected across the other docs; the
  registration key's cloud origin (if any) remains unobserved and is still tied to the uncaptured
  first-time/PIN pairing flow.

## What's still needed

- ~~Decode the **push WebSocket** messages~~ — **DECODED 2026-08-07 (cap66 PS5 / cap67 PS4). [C]** This was
  the last opaque piece of the connect flow, and it is now readable end to end.

  > **How it was captured.** The push connection bypasses the system HTTP proxy (a direct raw-TLS WSS to the
  > `<n>-pushcl.np.communication.playstation.net` front-end), and the client does **not** honour
  > `SSLKEYLOGFILE` (Schannel), so both earlier approaches were dead. What worked: **Proxifier** forced
  > the vendor client's sockets through **mitmproxy** at the Winsock layer (working around the client's own proxy bypass), with
  > mitmproxy decoding the WebSocket. One catch — `auth.np.ac.playstation.net` is **certificate-pinned** (it
  > rejected the MITM cert with `unknown ca` while every other host accepted it), which broke the push auth
  > and closed the WS with app code `4101`; excluding just that host with mitmproxy `--ignore-hosts` let the
  > push channel authenticate and stream. The push WS endpoint is `GET /np/pushNotification` (Upgrade → `101`),
  > distinct from the `/np/serveraddr` lookup.

  **Frame structure [C].** Push frames are session-manager event notifications: `{version, method: 3001,
  dataType: "psn:sessionManager:sys:rps:<event>", to: {...}, body: {data: {...}}}`. Most are presence/
  membership (`members:created/deleted`, `remotePlaySession:created`) and are ignored. The signaling rides in
  `dataType` **`...rps:sessionMessage:created`**, where `body.data.sessionMessage.payload` is the string
  `"ver=1.0, type=text, body=<JSON>"` and the JSON is the same `connRequest` shape the client POSTs outbound.

  **The exchange is symmetric [C].** There is **no `ANSWER`** — each side sends its own `OFFER` carrying its
  own candidates, then `RESULT`/`ACCEPT` acknowledgements. The client POSTs its OFFER to `sessionMessage`; the
  **console's OFFER arrives over the push channel**, tagged `from.platform: "PROSPERO"` (PS5) or a PS4 tag
  (our own posts echo back tagged `REMOTE_PLAY` and must be filtered out). Observed order per console:
  `OFFER (reqId 1)` → `RESULT (1)` → `ACCEPT (reqId 2)` → `RESULT (2)`.

  **The console's candidates [C].** PS5 offered `STATIC` + `LOCAL` on **port 9303**; PS4 offered
  `STUN` + `STATIC` + `LOCAL` (STUN on an ephemeral port, STATIC/LOCAL on 9303). And unlike our own OFFER, the
  console's `connRequest` carries a **populated `skey` (16 bytes)** and **`localHashedId` (20 bytes)** rather
  than the zero placeholders — see the send-side note below.

  Parsed by `HalyardSignalingMessage.TryParse` (`Ripcord.Cloud.Halyard`), validated against the captured
  frames by `LiveSignalingVectorTests` (dirty-room `ws_frames.txt`).

- ~~A **STUN client** is still needed~~ — **BUILT 2026-08-07** (`Ripcord.Core.Net.Stun`,
  `StunMessage` + `StunClient`), validated against the RFC 5769 canonical vector and a loopback fake server
  (`StunMessageTests` / `StunClientTests`). It gathers this host's reflexive endpoint **on a caller-supplied
  socket**, because the NAT binding is only valid for the port the media transport will later send from. Not
  yet wired into the signaling OFFER — that is the next step — but the reflexive-candidate gap itself is
  closed.

  > **The vendor's own STUN is authenticated, and we do not use it. [C]** cap64's STUN exchange (on 3478/3479)
  > is *classic* STUN (RFC 3489, no magic cookie, all-zero transaction id) and carries a `USERNAME` +
  > `MESSAGE-INTEGRITY` (HMAC-SHA1) on the request, with the response returning the reflexive address in the
  > legacy `0x8020` XOR-MAPPED-ADDRESS plus `SOURCE`/`CHANGED-ADDRESS`. So the vendor's STUN endpoints are part
  > of its authenticated relay tier, needing a credential we have not derived. **Ripcord does not need them for
  > reflexive gathering**: a NAT binding's public address is a property of the NAT, not the observer, so any
  > public STUN server answers — `StunClient` targets ordinary public servers and skips the integrity attrs.
  > Using the vendor *relay* (as opposed to a direct/holepunched path) would be a separate effort that this
  > does not attempt.

- **Send-side gap [X].** `HalyardCloudClient.SendOfferAsync` sends `skey` as 16 zero bytes and omits
  `localHashedId`; the console populates both (16 B / 20 B). Whether the console *requires* non-zero values
  from the client to complete the exchange is untested — the placeholders may be accepted, or may be why our
  own OFFERs have never driven a session. What `localHashedId` is a digest **over** remains underived (20
  bytes, SHA-1-shaped); do not send a plausible-looking wrong value.

- **`customData1` — SOLVED `[C]`: the console-delivered, encrypted no-PIN registration seed.** Each console
  pushes a `...rps:customData1:updated` carrying a **double-base64** value. During a *first-time no-PIN pair*
  it is the registration seed, field-encrypted by the console with the client's `data1`/`data2`:
  `seed = HalyardControlFieldCrypto.Decrypt(key = data1, material = data2, counter = 0, ct = base64⁻²(customData1))`,
  then `key' = seed XOR registrationTable[selector]` decrypts `/sess/rgst`. See "The no-PIN registration
  seed" above and `ps5-session-establishment.md`. (The 2026-08-20 note guessing it was session-scoped
  reconnect noise was right about *reconnects* and wrong about the *first-pair* meaning — the shape it flagged
  as "exactly what the no-PIN hunt is missing" was in fact the answer.)
- A **first-time pairing** capture *through the proxy* - would finally show whether a registration
  key is issued by a cloud call (and by which one), which no reconnect capture can reveal. Note the
  `commands` `data1/2/3` seeds are now a candidate mechanism for how per-session (and possibly
  per-registration) key material is distributed - worth focused attention.
- ~~Work out how the `commands` seed material and `sessionMessage` `skey` relate to the direct
  handshake's `RP-Pubkey`/`RP-Nonce`/ECDH (`ps5-session-establishment.md`) - the path to actually
  decrypting a session.~~ **Superseded** — decrypting a session did not require this; see the note at
  `data1/2/3` above. Establishing what these values *are* remains genuinely open **[X]**, but it is no
  longer on the critical path for anything.
