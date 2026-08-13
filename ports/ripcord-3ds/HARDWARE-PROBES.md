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
| `STREAM KEYS DERIVED` | **Reached 2026-08-12.** Takion, the protobuf, the launch spec and the ECDH agreement all accepted by a real PS5. A 203-byte SESSION_REPLY whose signature verified. |
| `stream: GMAC sealing enabled` | Everything sent from here carries a tag — including SACKs |
| `stream: STREAM_INFO` | The console sent the SPS/PPS parameter sets. **Proves our sealing is correct**, since it would not have got this far otherwise |
| `stream: STREAM_INFO_ACK sent` | A/V should start within a second or two |
| `FIRST A/V PACKET ... GMAC VERIFIED` | **The next milestone.** Real media, authenticating with the derived receive key |
| `media window: N video, M audio, 0 GMAC verify failure(s)` | The stream-plane crypto is correct against real traffic — 2,808 host cases' worth of layer finally seeing a real packet |

> **Log output is on the BOTTOM screen.** The top screen is the video output and belongs to MVD.

**The decoder is OFF until you press X**, deliberately. MVD decode runs on the same thread as the
receive loop, so if it costs more than the inter-packet gap it starves the socket — and the symptom
(packet loss) looks like a network problem rather than a CPU one. Run the window with decode off, toggle
it on, and compare the packet counts; Phase 2 already established that CPU contention on this core costs
real UDP throughput, so this is a measurement, not a precaution.

| Log line | Meaning |
|---|---|
| `MVD ready: 640x360 H.264 -> BGR565 240x400` | The decoder initialised at the resolution the **console** chose |
| `MVD needs a New 3DS` | Expected on an original 3DS — there is no video decode block at all |
| `FIRST DECODED PICTURE` | MVD rendered into the framebuffer. **Not yet seen** — the first hardware attempt failed with an unaligned input buffer (see below) |
| `MVD: N picture(s), M NAL unit(s) fed, P parameter set(s)` | `P` should be small and non-zero: SPS/PPS arrive before every IDR and MVD answers `MVD_STATUS_PARAMSET`, which is success, not failure |

**Where it stops in the media phase:**

| Symptom | What is implicated |
|---|---|
| No `STREAM_INFO` within 15 s | **Sealing.** The keys are agreed by definition at that point, so suspect tag offset 5, key position 9, the tag+keypos-zeroed AAD, or the position advancing by 16-byte-aligned length rather than by 1. |
| `STREAM_INFO_ACK sent` then no media | The keepalives (1 s Takion heartbeat, 200 ms congestion report) or their sealing. The console stops sending if they lapse. |
| A/V arrives but `GMAC verify failure(s)` non-zero | The receive-direction key (direction 3), the per-packet nonce derivation, or the A/V AAD rule — which differs from control's: A/V zeroes **only** the tag, not the key position, and takes the key position **from the packet** rather than choosing it. **One isolated failure is not this** — 1 in 3,879 was seen once on 2026-08-12 and never again in 11,448 packets, which is a corrupted datagram, not a crypto fault. The first four failures now log their rotation window; clustering at a boundary would be the shape that *is* a bug. |
| Frames assemble but **every** NAL unit errors | Seen 2026-08-12: 1105 fed, 1105 errors, result `0xd96170ca`. The input buffer was `linearAlloc`'d rather than `linearMemAlign(size, 0x40)` — MVD reads through physical addresses and refuses an unaligned one. *All* units failing is the signature of a rejected buffer; a bad bitstream fails *some*. Fixed. |
| `0 picture(s)` with **0 process errors** and N render errors | Seen 2026-08-12, result `0xd961710d`. The bitstream decoded perfectly and had nowhere to go: the top screen was 24-bit BGR8 (`gfxInitDefault`) while MVD emits 16-bit BGR565, *and* `consoleInit(GFX_TOP)` had given that framebuffer to the text console. Fixed by `gfxInit(GSP_RGB565_OES, GSP_BGR8_OES, false)` with the console on the bottom screen, as the devkitPro example does. **Zero process errors with non-zero render errors is the signature** — it means the decode path is right and the output path is wrong. |
| Frames assemble but **some** MVD units error | A raw result code with a non-zero `parameter set(s)` count means the SPS/PPS path works and the bitstream is the problem. Zero parameter sets means the units never reached the decoder at all. |
| `MVD wants a 400x240 stream; console gave 640x360` | Expected. This is currently where the video path stops — see below. |
| `DISCONNECT from console: <reason>` | The console hung up and said why. Asking for a non-standard 400x240 resolution reproducibly triggers this within a second of sealing. |

### Where the video path stands (2026-08-12)

Everything up to and including decode works: frames assemble, MVD accepts every NAL unit, and
`mvdstdRenderVideoFrame` returns `MVD_STATUS_OK`. What does not work is getting those pixels somewhere
visible, and the constraint is hard, established over several hardware runs:

- **MVD does not scale.** Input 640x360 with output 240x400 gave one render error per keyframe.
- **MVD would not write our own linear output buffer.** Input == output == 640x360 into a page-aligned
  buffer rendered "successfully" and left all 524,288 pixels zero, with the configured output address
  verified equal to the buffer's physical address on hardware. A CPU-drawn test square in the same frame
  *did* appear, proving the framebuffer, cache-flush and swap paths were all fine.
- **The console will not send a screen-sized stream.** Asking for 400x240 — which would have made frames
  directly renderable — produced two `DISCONNECT` messages within a second of sealing, twice.

Route (1) is now implemented and awaiting a run:

1. **`mvdstdSetupOutputBuffers()`** — the documented way to have rendered frames written to buffers of
   our own "instead of the output specified by configuration". That is exactly the failure above, and
   this is the API that exists to address it. The buffer is registered through the entrylist rather than
   assigned to `config.physaddr_outdata0`, which is the only difference between this and the attempt
   that produced an all-zero buffer. It is also sized to a **macroblock-aligned height** (368 rows for a
   360-row frame), since a decoder writing its padded picture buffer into a buffer sized for the
   unpadded height is another way a legitimate write lands out of bounds.

   The one-shot non-zero pixel count is back, and answers this in one line.

**Resolution negotiation is settled, and it closes one escape route permanently.** A six-candidate probe
(`proberesolutions=1`) established that the console encodes only its standard ladder: 640x360 accepted,
960x540 **clamped down to 640x360**, and 320x180 / 480x270 / 512x288 / 400x240 all refused with the
console's own stated reason:

```
Nagare did not init! AvCap failed to initialize video: [InitResult:-5]
```

That is the console's video encoder failing to initialise — a capability limit, not a policy, so no
launch spec will talk it round. Alignment was never the criterion either: 400x240 and 512x288 are both
macroblock-aligned and still refused.

**So resampling on this side is mandatory and permanent**, and "ask the console for a screen-sized
stream" is off the table for good. That makes the per-frame scale a fixed cost in the CPU budget rather
than an optimisation.

**Route (1) failed on hardware**: `SetupOutputBuffers` returned `MVD_STATUS_OK` (note: *not* 0 — MVD
reports success as `0x17000`, and testing `!= 0` wasted a run), renders succeeded, and the buffer stayed
entirely zero. Transposed dimensions (candidate 1) behaved identically.

**The likely answer came from 3dbrew, not from the example.** `MVD_Services` states: *"Linear memory
virtual addresses must be in the 0x30\* region; the system doesn't support the 0x14\* region."* The 3DS
has two linear-heap mappings and which one `linearAlloc` returns depends on the kernel and how the
process was launched. A buffer in the unsupported mapping is refused **silently** — the render still
reports success — which matches every symptom that survived the config-address check, the entrylist,
page alignment and macroblock padding. The build now logs the buffer's virtual address and says which
region it is in.

*(Reading 3dbrew is not a clean-room concern: that rule covers other implementations of the PS5
protocol, not Nintendo hardware documentation. Copying GPL homebrew source would be an Apache-2.0
licensing problem, but reading documentation is not.)*

**The memory region was not the answer either** (`0x3016c000` — the supported mapping), and neither was
the status-code theory. Instrumenting `ProcessNALUnit`'s return values gave:

```
ProcessNALUnit said: OK 0, FRAMEREADY 1237, NALUPROCFLAG 0, other 0
```

**Every unit returns FRAMEREADY.** So the decoder genuinely holds 1,237 real decoded pictures, and
`RenderVideoFrame` reports success on them, and the pixels are not in the entrylist buffer, not in the
config-addressed buffer, and not on screen. That rules out the entire class of explanation where we were
asking for a frame that did not exist.

**What is left is reading a working implementation.** `Core-2-Extreme/Video_player_for_3DS` does MVD
H.264 decode on New 3DS and is the obvious reference. It is **GPL-3.0**, so its code cannot be copied
into this Apache-2.0 tree — but reading it to learn which calls happen in which order is a factual
question about a hardware API, not copying expression, and implementing independently from that
understanding is exactly the method this project already uses for the protocol. (The clean-room rule does
not apply at all here: it covers other implementations of the *PS5 protocol*, not Nintendo hardware.)
2. **MVD input cropping** (`enable_cropping` + `input_crop_*`) — render a 400x240 window of the 640x360
   frame straight to the framebuffer, which is the one output target MVD is proven to write. Documented
   fields; would show a picture immediately, but the middle of the screen rather than the whole screen.
3. **A PICA200 scaling pass** — needs (1) working first, since the frame still has to land somewhere.
| Picture decodes but looks wrong | The output geometry, which is the least-certain part of `rc_mvd.c`. The framebuffer is transposed (240x400 in memory for a 400x240 screen) and the official example's source is already screen-sized, so it never answers what MVD does with a 640x360 input. Vary `MVD_OUTPUT_WIDTH`/`HEIGHT` and the input dimensions before suspecting the decode path. |

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
