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

We're close, but not one check away. Here's what's left, in order:

1. **The person-driven pass.** Stage A steps 8–10, the two Stage B checks and the input matrix in
   [`docs/design-branch-test-pass.md`](docs/design-branch-test-pass.md), and a sweep for any control that does
   nothing. 2026-10-01 found pads hadn't reached a stream since 2026-08-06, which is the argument for not
   skipping this. The first part ran on 2026-10-02 and found the bugs the journal lists; the
   connect, HUD, pairing and input checks are still to do, and so is the tone-map's one open check, a
   mid-session drag between an HDR and an SDR display.
2. **Tag a release candidate.** A `v1.0-rc1` tag builds the x64 and ARM64 zips into a draft release, which
   nobody sees until it's published. Those are what the next two steps install.
3. **Smoke-check it on ARM64.** The full run passed on 2026-10-05 from a local build; the CI-built ARM64 zip
   only needs to start and stream on the Surface.
4. **Install it on a clean machine.** Pair, stream, sign in, and a pad with and without GameInput, from the zip
   CI built. It's also the first fresh install of the new defaults: 1080p60 at 20 Mbps, H.264.
5. **Tag `v1.0`,** with `SECURITY.md`'s version row and the release notes. Then celebrate.

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
- [ ] A tagged release with x64 and ARM64 zips, and install steps checked on a machine without the SDK. (The
      release job exists since 2026-10-01; nothing has been tagged yet.)
- [x] The ARM64 zip runs on an ARM64 machine: it starts, pairs and streams, with both pads (2026-10-05, the
      Surface, from a local build by the release job's own steps; the CI-built zip gets a smoke check at rc1).
- [x] The three known defects are fixed and confirmed on hardware:
  - [x] **The Xbox pad beside a DualSense.** Fixed, and confirmed on 2026-10-01: both pads in one stream, a
        battery pull mid-stream, and menu navigation with both on.
  - [x] **HDR from the wrong display.** Fixed, and confirmed on 2026-10-01 on a two-monitor desk and across a
        laptop's two GPUs. The "little bright after a drag" turned out to be the GPU driver's tone-mapping. An
        SDR display asked the console for SDR for a while after that; since 2026-10-03 it asks for HDR and
        Ripcord tone-maps it itself, which also covers the drag (see below).
  - [x] **A two-line console name.** Fixed, and checked at every card density, at 100% and 200% display
        scale, and at 100% and 150% text (2026-09-30 and 2026-10-01).
- [ ] Ripcord's own HDR-to-SDR tone-map is right on every GPU and display path. (The Intel and NVIDIA GPUs and
      the Surface are done; a mid-session drag between displays is left. See Blocking 1.0.)
- [x] The defaults are 1080p60 at 20 Mbps, H.264 and no HDR (the owner's decision, 2026-10-05). HEVC and HDR
      are a choice in Settings, which keeps HDR games' highlights; a stored HEVC on a PC that can't decode it
      falls back to H.264 at connect.
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
supports. The package requires Windows 11 25H2 (build 26200). ARM64 is part of 1.0 too (decided
2026-10-01), so every release ships an x64 zip and an ARM64 one.

**Out of 1.0, and said so in the release notes:** DualSense output (haptics, adaptive triggers, lightbar,
gyro, touchpad drag), the latency and quality polish below, WAN relay, trimming, translations, and a
purchased signing certificate.

---

## Blocking 1.0

### Packaging

- [x] The unused `systemAIModels` capability is gone, the package requires 25H2, `dotnet publish` is
      self-contained, and `PRIVACY.md` and generated third-party notices ship in the app folder.
- [x] **GameInput's redistributable ships in the zip** (2026-10-01), and Settings > About says whether it's
      installed. The one `GameInputRedist.msi` installs on ARM64 too, and the app detects it (the Surface,
      2026-10-05).
- [ ] **Ripcord's HDR-to-SDR tone-map, on every GPU** (built 2026-10-03; the Intel and NVIDIA GPUs match each
      other and the console's own screenshot, and look right on the Surface at 200%, 2026-10-05): a mid-session
      drag between an HDR and an SDR display is left.
- [x] **The console's HDR "On When Supported"** (2026-10-05): asked for HDR it sends HDR10 for everything, an SDR
      game inside it with white near 255 nits; asked for SDR, an HDR game's stream clips as under "Always On".
- [x] **SDR games inside HDR10 looked slightly dull** after the tone-map (white at 91%). The renderer now
      measures the picture's peak and tone-maps SDR-like content from it (2026-10-05).
- [ ] **Clean-machine check.** Install the zip on a machine that has never had the SDK, then pair and stream.
      Sign in too: the web view's profile moved to `%LocalAppData%\Ripcord\WebView2`. Windows Sandbox is a
      clean Windows every launch, good for "does it start with nothing installed", though it won't pass
      pads or the GPU through.

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
- [ ] **A two-word name trims on a regular card at 150% text** ("Bedroom PS4" shows as "Bedroom…"): at that
      size the card has room for one line of name. Nothing is lost (the status line is kept, and the full name
      is in the tooltip), but taller cards or a smaller name at large text sizes would read better. A design
      call.
- [ ] **Pad focus inside dialogs.** Directional focus doesn't reach into `ContentDialog`s: the rename dialog,
      the remove confirmation and `LoginPinDialog`.
- [ ] **The page renames** from the app plan: `ConsolesPage` → `HomePage`, `AddConsolePage` → `PairPage`,
      `KeyBindingsPage` → `ControlsPage`. After the hardware pass, so they don't move what it's checking.
- [ ] **A keyframe the decoder couldn't start from** is invisible today: the controller counts encoded frames,
      not decoded ones. Needs a decoded-frame count from `D3D12VideoDecodePipeline`.
- [ ] **Does the console card rejoin the tile?** A dark-theme look decision for a real screen (~30 minutes).
- [ ] **HDR through a second GPU.** Render on a GPU that doesn't drive the display, and HDR presents black
      while SDR comes through fine. Why is `[X]`; my guess is the SwapChainPanel's 10-bit format through the
      copy between GPUs, and an FP16 scRGB back buffer is the experiment. Until then that case is tone-mapped
      to SDR by Ripcord (since 2026-10-03), so nobody sees black, and Auto doesn't go there.
- [ ] **HDR set-ups nobody's tried yet:** a machine with only an NVIDIA GPU, and Auto on a laptop with a
      monitor on the dGPU's own port (every port on the test laptop, dock included, goes through the iGPU).
- [ ] **HDR changes the app doesn't notice:** turning "Use HDR" on or off mid-session without moving the
      window, and Settings while it's open.
- [ ] **The copied diagnostics report leaves out where zero-copy fell back.** The F3 panel says; the text
      doesn't.
- [ ] **Two WinUI mysteries with workarounds in place** `[X]`: why XY focus drops a candidate once focus has
      come from the title bar (`FocusPilot`'s straight-line fallback), and why full screen leaves the content
      host a pixel down (`ContentBridge`).
- [ ] **Why a discovery family went quiet** on 2026-08-05 is still unexplained. The spinner fix stays.
- [ ] **Verify the GMAC on inbound control packets** once the stream keys exist. Our tag check now drops
      packets from outside the association, but anyone who can see the tag can still forge control DATA; the C
      core already checks the GMAC (review, H2).
- [ ] **`MFStartup` from an STA** is the suspected exact cause of the 2026-08-06 Settings crash `[X]`. The
      native-class guard holds either way.
- [ ] **Smaller hardening from the reviews:** the TCP control channel spinning on EOF, unbounded control-plane
      reassembly (and `HalyardCtrlMessage.TryParse`'s length sum overflowing near `int.MaxValue`), Takion's
      retransmit with no backoff or cap and its unbounded inbound queue, an audio device change keeping the old
      format, pointer-only on-screen buttons, and GameInput's microsecond timestamps where the input writer
      expects `DateTime` ticks (now mixed, since its neutral frame uses `DateTime` ticks). Details in the
      captures-folder reviews.
- [ ] **Threading, from the second review:** `InputRouter`'s lock and `SessionController`'s gate are both
      taken on the UI thread, and the decode pipeline's lock is held across decode bursts. The rule that held
      for the observables ("no lock the UI thread can contend") needs applying, not `SpinGate` everywhere.
      Also: only `MainWindow` guards work queued after close (the dispatcher itself should), the composite
      can publish merged frames out of order, GameInput replays one connection event where it now has several
      pads, and its poll timer isn't waited for on dispose.
- [ ] **A race in adding a console:** `AddConsoleFlow`'s scan cancellation (`_scanCts`) is read and replaced
      across threads.
- [ ] **Surface `DroppedPacketCount`** in the diagnostics overlay, so a burst of rejected control packets is
      visible.
- [ ] **A test for the session page's input wiring,** or move it into Presentation. The pads went unwired
      for two months with nothing noticing.
- [ ] **Trimming.** The app code trims clean; CsWinRT's ABI layer still produces 37 warnings that need
      understanding first. Buys download size only.
- [ ] **The Windows App SDK metapackage brings unused AI, ML and Search components** into the publish (~45 MB
      of the 270 MB on 2.2; 2.5 added Search). Reference the component packages instead.

### Sign-in and the cloud tier

- [ ] **`PersistRefreshed` does DPAPI and disk I/O under the token provider's lock,** and `_account` is read
      from other threads without a fence. Neither has bitten; both are worth tidying.

- [ ] **Sign in without the telemetry permission.** The sign-in asks for `sbahn:pc.telemetry.publish`, copied
      with the official client's other scopes, though Ripcord never publishes anything. Drop it and check that
      sign-in, the console list, wake and the internet route still work. `PRIVACY.md` says this meanwhile.
- [ ] **Add a console from the account's list, away from home.** The Add Console screen lists only what the
      network scan finds, because a saved console needs its local address, and the account's console list
      doesn't give one. Signed in, it could also list the account's consoles the scan didn't find, pair them
      through the account (which needs no address), and use the internet route until a local address is seen.
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
- [ ] **Is there any way to steer the console's bitrate mid-session?** Not `CONNECTION_QUALITY`: it runs
      console to client, and the vendor client never sends one (2026-10-02, research log). What the vendor client
      does send is `CORRUPT_FRAME` about once a second. A capture of it on real hardware (not a VM, which corrupted
      frames on its own) under added loss would show whether that, or anything else, moves the console's rate.
      Then remove Ripcord's unused send path (`ConnectionQualityReporter` and the stream's send).
- [ ] **Make a reconnect start from the recommendation.** The adaptive controller works out a lower quality
      under loss, and nothing acts on it: a reconnect starts at the configured settings. Start the next session's
      launchSpec from the recommendation, and bring back the setting ("Adapt quality automatically") and the
      diagnostics panel's adaptive line with it. Both were hidden for 1.0 on 2026-10-02, because all they did was
      show a recommendation nothing used; the stored setting is kept for this.
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
- [ ] **The engine's UDP sockets on Windows,** before Windows moves onto it. `ripcord-net` doesn't switch off
      `SIO_UDP_CONNRESET`, so one ICMP port-unreachable would fail the next receive, which ended dotnet-client
      sessions until 2026-10-01 (`UdpChannelResetTests` is the model). The Mac and the console ports aren't
      affected: it's Windows behaviour.

### The Mac, iPhone, iPad and Apple TV

- [ ] **The Mac's sign-in has the dotnet client's token bugs** (fixed there on 2026-10-01): `restore()`
      clears the store on any service error, a 503 included; a background refresh is never stored; a restore
      whose account lookup fails, the network included, keeps the spent token; and nothing stops two restores
      at once spending one token. Port the fix and `HalyardAccountGatewayTests` to `AccountGateway.swift` and
      its `TokenProvider`, and clear only on 400 `invalid_grant`.

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

- [ ] **Repository security:** branch protection on `main`, secret scanning and push protection, Dependabot,
      actions pinned by SHA, and a CI check that every commit's identity is the project's.
- [ ] **The release, beyond the zip:** sign the exe. Smart App Control, on by default on some new PCs and fresh
      Windows 11 installs, blocks an unsigned app with no way to run it anyway, which makes this the biggest
      obstacle to a stranger downloading and playing (Artifact Signing can sign one inside a zip, not only an
      MSIX), check the notices are fresh in CI rather than only present, and note that only `dotnet publish`
      makes the self-contained build (`msbuild -t:Publish` doesn't set `_IsPublishing`).
- [ ] **ProtocolLab's `register` and `pair`** still print the registration key and `RP-Key`; route them through
      the redactor. And the leak guard should warn, not stay silent, when it can't read its denylist.
- [ ] **The engine and ports items the second review found untracked:** its M1, M2, L4, and the `free` case of
      the engine's re-entrancy finding (`ripcord_client_free` returns `void`, so "return Busy" can't cover a
      free from inside a callback). Those gate the first `RIPCORD_ENGINE` seam. Also `halyard_client.c`, which
      has no production consumer.

- [ ] **Verify the review's store and market claims** before acting on any of them.
- [ ] **Signing the MSIX:** try Azure Artifact Signing.
- [ ] **Retire or rebase `feat/vita-port`.**

---

## Never commit

Captures, key material and vendor binaries stay in the gitignored `docs/protocol/captures/` captures folder. That
includes the live vectors in `registration_crypto_vectors.json` and `control_crypto_vectors.json`, which carry
our own console's registration key, live nonces, the real MachineGuid and an account id. Publishing those
exposes our own console and account for zero interop benefit, and it can't be taken back.
