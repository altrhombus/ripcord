# Ripcord roadmap

This is what's left to do. Finished work moves to [`docs/journal.md`](docs/journal.md), and the long version
of this file, everything it said up to 2026-09-30, lives on in
[`docs/history/roadmap-to-2026-09-30.md`](docs/history/roadmap-to-2026-09-30.md). When an item below says
"details in the history", that's where to look.

`[X]` still means what it means everywhere else here: assumed, never confirmed against a console.

---

## 1.0: the short version

Here's the bar: someone who isn't me downloads Ripcord, pairs their own PS5 or PS4, and plays—without hitting
a bug I already know about. 1.0 is the dotnet client's. The console ports version on their own (`ps3-v1.0`
is the PS3 port's release, not Ripcord's).

We're close! Here's what's left, in order:

1. **Finish the hardware pass.** Most of it ran on 2026-10-01, and everything it turned up is fixed (the
   journal has the whole list). Still on the bench: the roomy console card, a 200% display, and a quick look
   at the touch panel in its new spot above the hint bar.
2. **Fix what the review turned up.** One bad packet can end a session, and sign-in doesn't survive a token
   refresh. Neither is a long fix.
3. **Package it.** A zip that runs on a machine that's never seen the .NET SDK, with GameInput's
   redistributable alongside it.
4. **Install it on a clean machine.** Pair, stream, done.
5. **Tag `v1.0`.** Then celebrate.

Everything else in this file is real, and it can wait. (Yes, even the Apple TV app.)

---

## Where things stand

| Client | State |
|---|---|
| **Windows** (the dotnet client) | Streams PS5 and PS4, on the LAN and over the internet. Heading for 1.0 |
| **PlayStation 3** | Released as [`ps3-v1.0`](https://github.com/altrhombus/ripcord/releases/tag/ps3-v1.0): 720p60 from a PS5 or PS4 |
| **New 3DS** | Streams a PS5 |
| **macOS** | On the Rust engine. Streams from `ripcord-lab`; the app itself hasn't met a console yet |
| **iPhone, iPad, Apple TV** | Builds for all three; hasn't run on a device yet |
| **PS Vita** | On its own branch (`feat/vita-port`), well behind `main` |

CI is green on every job, the nightly engine run (Miri plus the fuzzers) passes daily, and the engine
measures 0.92 µs a packet on Windows x64 against the managed engine's 14.29 µs.

---

## The 1.0 checklist

1.0 ships when every line is true.

- [x] CI passes on a real runner, on a clean clone, for every job.
- [x] The GMAC window boundary and the non-P521 curve gap are resolved.
- [ ] A tagged release with an x64 zip, and install steps checked on a machine without the SDK.
- [ ] The three known defects are fixed and confirmed on hardware:
  - [x] **The Xbox pad beside a DualSense.** Fixed, and confirmed on 2026-10-01: both pads in one stream, a
        battery pull mid-stream, and menu navigation with both on.
  - [x] **HDR from the wrong display.** Fixed, and confirmed on 2026-10-01 on a two-monitor desk and across a
        laptop's two GPUs. The "little bright after a drag" turned out to be the GPU driver's tone-mapping, so
        a session started on an SDR display now asks the console for SDR instead. The drag itself can wait
        (see After 1.0).
  - [ ] **A two-line console name.** Fixed and checked at 100% and 150% text. Open: the roomy card and a 200%
        display.
- [ ] The input stack, Stage A steps 8–10 and the two Stage B checks have been driven by a person, and
      whatever that finds is fixed or listed. The script is
      [`docs/design-branch-test-pass.md`](docs/design-branch-test-pass.md).
- [ ] No user-visible control is inert.
- [ ] `SECURITY.md` names a supported version, and private vulnerability reporting is on. (Reporting went on
      2026-09-30; the version table is written.)
- [ ] The README states the limits a first-time user meets in their first ten minutes. (Written 2026-09-30.)

**Decided.** The zip leads, and an MSIX is optional; signing it is still open (a self-signed package asks
each user to trust a certificate as administrator, so I'd like to try Azure Artifact Signing). The account
tier, meaning sign-in, the account's console list, cloud wake and account pairing, is part of what 1.0
supports. The package requires Windows 11 25H2 (build 26200).

**Out of 1.0, and said so in the release notes:** DualSense output (haptics, adaptive triggers, lightbar,
gyro, touchpad drag), the latency and quality polish below, WAN relay, trimming, ARM64 binaries (it builds,
it hasn't been run recently), translations, and a purchased signing certificate.

---

## Blocking 1.0

### From the hardware pass

- [ ] **The touch panel and the hint bar.** They used to overlap; the stream's bottom band now lifts by the
      bar's height. Pick up a pad mid-stream, move the mouse, and check the panel sits above the bar.

### From the 2026-09-30 review

An outside-eyes review, kept in the captures folder. Every item here was checked against the code first.

- [ ] **One spoofed datagram can end a session.** `TakionDataChunk.TryParse` accepts a 12-byte chunk, then
      slices the first-fragment payload from byte 9 of an 8-byte value. Bound the slice, catch per packet in
      both receive loops, drop verification-tag mismatches, and add a test for the 12-byte chunk.
- [ ] **Sign-in doesn't survive a refreshed token or an outage.** A rotated refresh token lives in memory only,
      and any cloud error at restore, a 503 included, clears the stored account. Persist on refresh; clear only
      on a definite rejection.

### Packaging

- [x] The unused `systemAIModels` capability is gone, the package requires 25H2, `dotnet publish` is
      self-contained, and `PRIVACY.md` and generated third-party notices ship in the app folder.
- [ ] **Ship GameInput's redistributable.** `GameInputRedist.dll` isn't in the published app; every machine
      tried so far had it from an earlier install. GameInput's own README says to ship `GameInputRedist.msi`
      with anything that uses it, or an Xbox pad may do nothing on a clean machine.
- [ ] **Clean-machine check.** Install the zip on a machine that has never had the SDK, then pair and stream.
      Sign in too: the web view's profile moved to `%LocalAppData%\Ripcord\WebView2`.

### Stated as a limit, not fixed for 1.0

- **Internet play to a console in rest mode** probably fails in the dotnet client: `EnsureSignedInAsync`
  fails the session when SESSION_ID doesn't follow a passcode, and a woken console sent it only after the A/V
  leg on the Mac (journal, 2026-09-25) `[X]`. The README says so. Confirm on the dotnet client, then port the
  Mac's continuation.

---

## After 1.0

### The dotnet client

- [ ] **Smaller things the hardware pass is likely to find more of.** Regression screenshots of the new card
      and the session page (light, dark, high contrast); eyeballing high contrast and transparency-off; Narrator
      over the card grid; two open judgements (the connect screen's 112 px reserved gap, and whether the
      diagnostics strip should rise to a letterboxed picture's edge). Details in the history.
- [ ] **Pad focus inside dialogs.** Directional focus doesn't reach into `ContentDialog`s: the rename dialog,
      the remove confirmation and `LoginPinDialog`.
- [ ] **The page renames** from the app plan: `ConsolesPage` → `HomePage`, `AddConsolePage` → `PairPage`,
      `KeyBindingsPage` → `ControlsPage`. After the hardware pass, so they don't move what it's checking.
- [ ] **A keyframe the decoder couldn't start from** is invisible today: the controller counts encoded frames,
      not decoded ones. Needs a decoded-frame count from `D3D12VideoDecodePipeline`.
- [ ] **Does the console card rejoin the tile?** A dark-theme look decision for a real screen (~30 minutes).
- [ ] **HDR after a mid-session drag to an SDR display** is tone-mapped by the GPU driver, and every vendor
      does it differently: washed out on Intel, crushed on NVIDIA. Our own tone-map, built from ITU-R BT.2390,
      would make it the same everywhere. Most apps don't handle the drag at all, so this one's polish.
- [ ] **HDR through a second GPU.** Render on a GPU that doesn't drive the display, and HDR presents black
      while SDR comes through fine. Why is `[X]`; my guess is the SwapChainPanel's 10-bit format through the
      copy between GPUs, and an FP16 scRGB back buffer is the experiment. Until then it's SDR, and Auto
      doesn't go there.
- [ ] **HDR set-ups nobody's tried yet:** a machine with only an NVIDIA GPU, and Auto on a laptop with a
      monitor on the dGPU's own port (every port on the test laptop, dock included, goes through the iGPU).
- [ ] **HDR changes the app doesn't notice:** turning "Use HDR" on or off mid-session without moving the
      window, and Settings while it's open.
- [ ] **The copied diagnostics report leaves out where zero-copy fell back.** The F3 panel says; the text
      doesn't.
- [ ] **`CompositeControllerSource` keeps a disconnected engine's last frame.** The stuck button is fixed
      where it started, but the merge shouldn't depend on an engine sending a neutral frame.
- [ ] **Two WinUI mysteries with workarounds in place** `[X]`: why XY focus drops a candidate once focus has
      come from the title bar (`FocusPilot`'s straight-line fallback), and why full screen leaves the content
      host a pixel down (`ContentBridge`).
- [ ] **Why a discovery family went quiet** on 2026-08-05 is still unexplained. The spinner fix stays.
- [ ] **`MFStartup` from an STA** is the suspected exact cause of the 2026-08-06 Settings crash `[X]`. The
      native-class guard holds either way.
- [ ] **Smaller hardening from the review:** the TCP control channel spinning on EOF, unbounded control-plane
      reassembly, the Takion loops dying on one ICMP error, an audio device change keeping the old format,
      pointer-only on-screen buttons, and GameInput's microsecond timestamps where the input writer expects
      `DateTime` ticks. Details in the captures-folder review.
- [ ] **Trimming.** The app code trims clean; CsWinRT's ABI layer still produces 37 warnings that need
      understanding first. Buys download size only.
- [ ] **The Windows App SDK metapackage brings unused AI, ML and Search components** into the publish (~45 MB
      of the 270 MB on 2.2; 2.5 added Search). Reference the component packages instead.

### Sign-in and the cloud tier

- [ ] **Sign in without the telemetry permission.** The sign-in asks for `sbahn:pc.telemetry.publish`, copied
      with the official client's other scopes, though Ripcord never publishes anything. Drop it and check that
      sign-in, the console list, wake and the internet route still work. `PRIVACY.md` says this meanwhile.
- [ ] **An honest User-Agent.** Cloud calls send the vendor client's own, and whether PSN requires it is `[X]`.
- [ ] **A build where you supply the OAuth credential.** The `client.json` override exists; should a published
      build ship without the bundled one?
- [ ] **The account-pairing flag check,** which needs a console with remote play or rest-mode wake switched
      off, then switched back.

### Streaming quality and the protocol

- [ ] **Do the senkusha probes succeed, or fall back silently?** `MtuConfirmed` answers the MTU half in two
      minutes; the echo half needs a new signal, since the RTT always has a fallback behind it.
- [ ] **Capture `BANDWIDTH_COMMAND`**, the one senkusha piece never seen (absent from two LAN captures). Try
      automatic quality, a bad Wi‑Fi link, or an internet session.
- [ ] **Does the console honour a mid-session target bitrate?** Build the forced-bitrate hook (F4 cycling
      20/10/5 Mbps) and test on a perfect link with a low target. Don't build reconnect-on-collapse first.
- [ ] **The PS4's 16-byte MTU frame after the passcode.** We never send it and both families stream anyway.
      Drive a locked PS4 to a stream to be sure.
- [ ] **Which congestion packet size do we get?** We send the 15-byte form; an older capture used 23 bytes.
- [ ] **The presented/decoded ratio under pure network loss**, from any lossy session's trace.
- [ ] **The stream SESSION_REQUEST's `encryptedKey`:** we send four zero bytes, the vendor sends it empty
      (`22 00`). A one-line change in each implementation, wanting one hardware run.
- [ ] **Check resolution against bitrate.** The console grants resolution by bitrate, and the default is
      10,000 kb/s. Measure what a default session actually gets.
- [ ] **Latency polish, only if measurement says so:** D3D12-native decode, PTS-based A/V sync, an early demuxer
      flush, `CODECAPI_AVLowLatencyMode`, the unused `LatencyMode`, GHASH aggregation, caching the GMAC key per
      rotation window, and the A/V loop's allocation churn. Details in the history.
- [ ] **`AdaptiveBandwidthController` blames the network for loss we caused ourselves** (a full A/V queue).
- [ ] **WAN relay.** Rendezvous, push, STUN and cloud wake are done; the relay carries media at the same ports
      and framing, so it's re-addressing, not a new transport.

### Controllers

- [ ] **DualSense over Bluetooth, full report `0x31`.** The layout, its CRC and the activation by feature report
      `0x05` come from the public layout, not our capture `[X]`. Capture one. Same for the DualSense Edge id.
- [ ] **A pad and the keyboard together**, which the merge handles in tests but no two real devices have tried,
      and keyboard play on the Ally.
- [ ] **Unconfirmed input values:** D-pad up/down assigned by elimination, the touchpad click's code, motion
      full scale, orientation packing, and the history parser's last 35%. None affect streaming today.
- [ ] **DualSense output:** haptics, adaptive triggers, lightbar, gyro calibration, touchpad drag.

### The Rust engine

- [ ] **Windows ARM64 per-packet figure** (x64 is done). `engine/README.md` has the three commands.
- [ ] **The probes and rate control against a console,** and the rendezvous route on the engine.
- [ ] **The arm probe is never answered**, so every LAN connect waits ~2.2 s for it `[X]`. Does a reply exist?
- [ ] **Protocol questions carried from the C core,** each `[X]`: the opener's request word (0x40), a login prompt
      after the 1 s window, the console refusing TCP 9295 after a rendezvous session, servicing the 9303
      association, registering on every connect, and PS4's account route.
- [ ] **Re-entrant ABI calls** from a host callback alias `&mut` (review). Return `Busy` before the .NET host
      arrives.
- [ ] **Windows onto the engine,** one seam at a time behind `RIPCORD_ENGINE`, after 1.0.

### The Mac, iPhone, iPad and Apple TV

- [ ] **The Mac app's first hardware session:** the library's states, pairing both routes, launch timing,
      capture, the inspector, latency beside the dotnet client's. Then recording, PiP, HDR end to end, and
      widgets and intents under a signed build `[X]`.
- [ ] **The Mac on the engine, on hardware:** the account route, `pair` and `account-pair`.
- [ ] **Internet play in the Mac app,** a pad attached, passkey sign-in, and the Keychain under the app's own
      signature.
- [ ] **The first `macos-v*` release.** Six secrets and a Developer ID; the signing steps have never run.
- [ ] **iPhone, iPad and Apple TV on real devices**, signing, and the rest of
      [`docs/ios-plan.md`](docs/ios-plan.md).

### Console ports and libripcord

- [ ] **The PS3 port reconnects on its own** after a stall, with bounded attempts and backoff, like
      `SessionController`'s watchdog.
- [ ] **Pair a port from the desktop app.** Design note for review: [`docs/port-pairing.md`](docs/port-pairing.md).
- [ ] **Build the new socket code for the console SDKs** `[X]`: `select()`, `suseconds_t`, `rc_udp_open_bound`.
- [ ] **An inert build for the C ports**, without the interop constants, if a port ever needs one.

### Decisions and checks

- [ ] **Verify the review's store and market claims** before acting on any of them.
- [ ] **Signing the MSIX:** try Azure Artifact Signing.
- [ ] **Retire or rebase `feat/vita-port`.**

---

## Never commit

Captures, key material and vendor binaries stay in the gitignored `docs/protocol/captures/` captures folder. That
includes the live vectors in `registration_crypto_vectors.json` and `control_crypto_vectors.json`, which carry
our own console's registration key, live nonces, the real MachineGuid and an account id. Publishing those
exposes our own console and account for zero interop benefit, and it can't be taken back.
