# Hardware probe run sheet

Everything from Phase 3 onward — discovery, the `/sess/init` → `/sess/ctrl` exchange, the Takion
transport — is verified only against transcribed spec examples, captured vectors, and agreement with
Ripcord's own .NET implementation. That is a strong check on internal consistency and **no check at all**
that a real PS5 accepts any of it. This sheet exists to close that gap.

Read the whole thing once before you start. The three probes are ordered by dependency, and probe 2 is
worthless if probe 1 fails.

---

## 0. Before you leave the desk

### 0.1 Build

```sh
cd ports/ripcord-3ds
make                 # expect: ripcord-3ds{,-linktest,-discovery,-session,-takion}.3dsx
```

If `make` errors about `DEVKITPRO`, you missed `source /etc/profile.d/devkit-env.sh`.

### 0.2 SD card layout

Copy these onto the 3DS SD card. **Keep each `.3dsx` and its config file in the same directory** — every
program resolves its config and its log relative to its own path (`rc_program_dir`), so a split layout
gets you "could not open pairing.txt" and nothing else.

```
sdmc:/3ds/ripcord/
    ripcord-3ds-discovery.3dsx
    ripcord-3ds-session.3dsx      + pairing.txt
    ripcord-3ds-takion.3dsx       + takion.txt
```

**Launch from the Homebrew Launcher**, not from a forwarder or a CIA install. The log files depend on
`argv[0]` being the `.3dsx` path; without it the fallback is bare `sdmc:/`, which still works but scatters
`discovery.log` / `session.log` / `takion.log` at the card root.

### 0.3 `pairing.txt` — and where its values come from

The session probe needs a real pairing record. **This port cannot pair**, and it cannot read the desktop
client's `consoles.json` either — that blob is encrypted at rest. Pair from the lab tool instead, which
prints exactly the two values you need:

```sh
dotnet run --project tools/Ripcord.ProtocolLab -- register <consoleIp> <passcode> <accountId> [clientIdHex] [ps4|ps5]
```

On success it prints `registkey (hex)` and `companion (RP-Key)`. Transcribe both:

```
host=192.168.1.42
platform=ps5
registkey=1a2b3c4d5e6f0011
companion=00112233445566778899aabbccddeeff
```

| Field | Required | Notes |
|---|---|---|
| `host` | yes | The console's LAN address |
| `registkey` | yes | Hex. **Max 8 bytes / 16 hex characters** |
| `companion` | yes | Hex, **exactly 16 bytes / 32 hex characters** |
| `platform` | no | `ps5` (default) or `ps4` |
| `deviceid` | no | Hex, up to 16 bytes; defaults to all-zero |
| `osmajor` / `osminor` | no | Default 10 / 0 |
| `bitrate` | no | Default 10000 |
| `streamingtype` | no | Default 0 |

> **The likeliest silent failure on this sheet.** The three length rules above are enforced by a decoder
> that returns "bad" rather than "too long", and the loader reports all three required fields in one
> message. So an over-long `registkey` produces `pairing.txt is missing host, registkey and/or a 16-byte
> companion` — which reads like a missing field and is actually a length problem. If you see that message
> and the fields are visibly present, count hex characters first.

### 0.4 `takion.txt`

```
host=192.168.1.42
port=9297
```

Read §3 before you bother filling this in — the port number is a guess, deliberately.

### 0.5 Network

- 3DS and console on the **same LAN segment**. Discovery uses the limited broadcast address
  (`255.255.255.255`), which does not cross subnets.
- **Disable AP/client isolation** on the router if it's on. It blocks broadcast between wireless clients
  and will produce a clean, entirely misleading "no consoles responded".
- Don't run the desktop Ripcord client, the vendor client, or a PS Remote Play app against the same
  console during a probe. The console's control listener is not something two clients share gracefully.

---

## 1. Probe 1 — discovery (`ripcord-3ds-discovery.3dsx`)

The cheapest and most informative probe. Everything downstream depends on the console answering here.

**Run it twice: once with the console awake, once with it resting.** The two are different code paths on
the console's side (`200 Ok` vs `620 Server Standby`) and this port parses both.

No config file. It broadcasts SRCH for both families — PS5 on UDP 9302, PS4 on UDP 987 — and lists what
answers within a 4-second window, deduplicated by host-id. START exits.

**Expected on success:**

```
broadcasting SRCH (PS5 9302 + PS4 987)

  [PS5] MyConsoleName        192.168.1.42   id=... sw=... awake=yes

1 console(s) found.
```

**Record for each of the two runs:** the whole line — `host-type`, `host-name`, `address`, `host-id`,
`system-version`, and `awake`.

**What each outcome means:**

| Result | Reading |
|---|---|
| Both runs list the console, `awake` differs correctly | Phase 3 is confirmed on real hardware. Proceed. |
| Awake works, resting shows nothing | Either the console doesn't answer at rest (check its power setting for "stay connected to the internet"), or the standby response parse is wrong. Grab the raw bytes before concluding. |
| `no consoles responded` on both | Almost certainly network, not protocol — recheck §0.5 before suspecting the parser. |
| `bind() failed: 22` | **Fixed 2026-08-12** — the socket bound port 0, which SOC rejects with `EINVAL`. Rebuild and recopy `ripcord-3ds-discovery.3dsx`; it now binds a fixed port and logs `bound local port 9310`. |
| Something answers but a field is blank or garbled | **This is the interesting failure.** It means the wire format differs from `docs/protocol/ps5-local-discovery.md`. Note exactly which field. |

**Do not continue to probe 2 until an awake console shows up here**, with the address matching your
`pairing.txt` `host`.

---

## 2. Probe 2 — the control plane (`ripcord-3ds-session.3dsx`)

The one that matters most. This is the first end-to-end use of the Phase 0 crypto against real console
output, and the first time this port asks a console for anything.

**The console must already be awake.** This port does not implement the LAN wake exchange — a resting
console cannot be woken from the 3DS, and the probe will simply fail to connect. Wake it from a
controller or the desktop client first.

The program: arms the control listener (UDP `SRC3`/`SRC2` to port 9295, since the console does not hold
TCP 9295 open continuously), connects, `GET /sess/init`, derives the control key from the returned
`RP-Nonce` plus your companion, reconnects, `GET /sess/ctrl` with five encrypted `RP-*` headers, then
enters the persistent binary control channel.

**Stay in the binary channel for at least 60 seconds.** This is not padding. Missing or malformed
heartbeat replies are what make a real console reset the session roughly 15–30 seconds in, so a run that
exits at 10 seconds has not tested the thing most likely to be wrong.

Controls: **Y** requests rest mode, **START** exits.

**The four checkpoints, in order.** Record which one you reach:

1. `control listener armed (got RES3)` — the arm probe works. *(Non-fatal if absent: the probe arms the
   console whether or not the reply reaches you, so the run continues either way.)*
2. `/sess/init -> 200` — the console accepted a plaintext `RP-Registkey` and returned an `RP-Nonce`.
3. `/sess/ctrl -> 200` — **the big one.** This means all five encrypted fields decrypted correctly on the
   console. The control-plane crypto is confirmed against real hardware.
4. `session ready (N-byte session id)` — the binary channel is live and framing is correct.

**Triage:**

| Where it stops | Most likely cause |
|---|---|
| `could not open .../pairing.txt` | §0.2 layout, or launched outside HBL |
| `pairing.txt is missing host, registkey and/or a 16-byte companion` | Check hex lengths first — see the callout in §0.3 |
| `TCP connect for /sess/init failed` | Console asleep, wrong `host`, or the arm probe never landed |
| `/sess/init -> 4xx` | The registration key is rejected — stale or wrong pairing record. Re-pair. |
| `/sess/init` OK but no usable `RP-Nonce` | Header present but not 16 bytes after base64 — a real spec finding, capture it |
| `/sess/ctrl -> 4xx` after `/sess/init -> 200` | **The most valuable failure on this sheet.** The nonce and companion produced a key the console disagrees with — i.e. the codec-selector assumption (hardcoded 2), the KDF, or the field cipher is wrong. Record the exact status code. |
| Reaches the channel, then `ctrl connection closed by console` after ~15–30 s | Heartbeat handling. Note the elapsed time and any `ctrl message type 0x....` lines just before. |
| `console requests sign-in` | Expected and not a bug — this build does not implement the login submission. |

Any `ctrl message type 0x....` line is a message type this port does not model. **Write down every one**,
with its byte count — that list is direct input to the next phase.

---

## 3. Probe 3 — Takion (`ripcord-3ds-takion.3dsx`)

**Read this before running it, and calibrate your expectations downward.**

This probe is explicitly exploratory, and a failure here is *weak evidence of nothing*. Three reasons —
and the first, added after the 2026-08-12 run, is decisive:

1. **There is nothing listening.** The console opens its stream UDP listeners only between `/sess/ctrl`
   and the stream. Run standalone, with no live session, an INIT has no possible recipient. This probe
   cannot succeed against a console by construction, whatever the port.
2. **The default port is the wrong one anyway.** 9297 is the *senkusha* port; the A/V stream port is
   9296, one below it (`HalyardStreamingSession.SenkushaPort`).
3. **A real console gates the INIT.** It expects the binary control channel's session-ready frame and a
   keyless senkusha probe first, and this program does neither.

So the realistic outcome is `handshake did not complete (5 attempts x 1000 ms)`, and that tells you
approximately nothing about whether `takion_handshake.c` is correct. Run it anyway — it costs two
minutes, and the upside is asymmetric:

- **If the handshake completes**, that is a genuinely valuable and somewhat surprising result. Record the
  local and peer verification tags, and whether anything arrives in the 10-second poll window.
- **If it fails**, record only that it failed. Do not open a bug against the handshake codec on this basis.

The honest way to test Takion is against a console that has already been walked through probe 2 to
session-ready, which is connect-flow work this port has not built yet.

> **First run (2026-08-12) crashed before it got that far** — an ARM11 data abort, nothing logged past the
> banner. Cause was ours and had nothing to do with Takion's wire format: `takion_reliable_channel` is
> 49.6 KB and was a stack local, against libctru's 32 KB main-thread stack, so the frame blew up in the
> function prologue. Fixed (`static`), and the build now enforces `-Wframe-larger-than=8192` so it cannot
> recur silently. **Rebuild and recopy `ripcord-3ds-takion.3dsx` before re-running.** The expectations
> above are unchanged: the transport still has no hardware evidence either way.

---

## 3b. Probe 4 — the connect flow (`ripcord-3ds-connect.3dsx`)

**This is the one to run now.** It supersedes probe 3 for testing anything below the control plane,
because it is the only program that carries a live session all the way to the stream ports. Reads the
same `pairing.txt`; writes `connect.log`.

The sequence, and what each line means:

| Log line | Meaning |
|---|---|
| `/sess/init -> 200`, `/sess/ctrl -> 200` | Control plane, already confirmed on 2026-08-12 |
| `control plane up; checking the sign-in gate` | Waiting 1 s for a PIN prompt |
| `console requires a sign-in PIN` | **Stop.** A locked console silently drops every Takion INIT, so nothing past here can work. Sign in on the console itself and re-run. This build cannot enter a PIN. |
| `session ready` | `SESSION_ID` seen — the console is willing to stream |
| `senkusha: established` | The 9297 bring-up handshake completed |
| `senkusha: SESSION_REPLY - bring-up complete` | The console answered the keyless exchange |
| `stream: Takion ESTABLISHED` | **New ground.** The 9296 handshake worked — this is the first hardware evidence Takion is correct |
| `stream: SESSION_REQUEST N bytes (curve P-521), fragmenting` | Our request, >1000 bytes so it fragments |
| `stream: SESSION_REPLY` | The console accepted the launch spec and our ECDH key |
| `STREAM KEYS DERIVED` | **The milestone — reached 2026-08-12.** Takion, the protobuf, the launch spec and the ECDH agreement all accepted by a real PS5. A 203-byte SESSION_REPLY whose signature verified. |

**Where it stops, and what that implicates** — each step narrows the suspect list, which is the point of
running them in one program:

| Last line reached | What is implicated |
|---|---|
| `senkusha: handshake did not complete` | Takion handshake, or the session isn't as ready as `SESSION_ID` suggested |
| senkusha OK, `stream: Takion handshake did not complete` | Port 9296 may be wrong for this firmware — the spec says the stream port is in principle negotiable and one capture used 9297. Trying 9297 is the documented alternative. |
| `stream: Takion ESTABLISHED` then nothing | **Seen on 2026-08-12, and it was a fragmentation bug, not the request contents** — continuations went out on channel 0 (fixed; see SETUP.md Phase 6b). The run now always logs a timeout line with a count of reliable messages received. **0 messages** = the console ignored the request: suspect the launch spec's OFB counter (0, unpinned), then the omitted `adaptiveStreamMode:"resize"`, then the protobuf. **Non-zero** = it is talking to us about something else; log the types. |
| `SESSION_REPLY rejected` | We got a reply but its `ecdhSignature` did not verify, or it was on the wrong curve. Do not dismiss this — it is also what a MITM looks like. |

Note it does **not** send REST_MODE, so the console is left awake for the next run.

## 4. What to bring back

Three files from the SD card — this is the whole point of the dual console/SD logging:

```
discovery.log     (two runs: awake and resting, appended — each run starts "---- new run ----")
session.log
takion.log
```

Plus, written down or photographed:

- The furthest checkpoint reached in probe 2, and the exact status code if it stopped at a `4xx`.
- Every `ctrl message type 0x....` line.
- How long the binary channel survived before closing, if it closed.

Logs append rather than overwrite, so a second run does not destroy the first. Copy the whole file.

---

## 5. Afterwards

Update the "Where this stands" checklist in [`README.md`](README.md) and the relevant phase section in
[`SETUP.md`](SETUP.md) with what actually happened — including the failures. Phases 1 and 2 both set the
precedent that a first real run gets written up with its numbers and its caveats, and the Phase 2 writeup
is more useful *because* it records the vblank bug the first run surfaced rather than quietly fixing it.

If probe 2 reaches `/sess/ctrl -> 200`, that is the single largest de-risking event available to this
port right now: it converts four phases of "agrees with our own .NET implementation" into "a real PS5
accepted it."
