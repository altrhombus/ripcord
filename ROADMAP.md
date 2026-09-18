# Ripcord roadmap

**What is left to do.** This file is the source of truth for the open backlog. It carries two other things
and nothing more: a **status preamble**, because a backlog with no sense of where the project stands is
unusable, and the **immediate context an open item needs** to be understood — what landed around it, and
why the remaining work is shaped as it is.

What it does *not* carry is a historical record. When something is finished and its story is worth keeping,
the story goes to the journal and a pointer stays here.

**Start with "1.0 — scope" below.** The backlog is a list of everything outstanding, which is a different
question from what has to be true to ship; that section draws the line and the backlog stays the detail
behind it.

Three companions carry the rest, and each answers a different question:

| Question | Where |
|---|---|
| What is this, and does it work? | [`README.md`](README.md) |
| What happened, and when? | [`docs/journal.md`](docs/journal.md) |
| What was consulted, and what did it inform? | [`docs/protocol-research-log.md`](docs/protocol-research-log.md) |
| How does the protocol work? | [`docs/protocol/`](docs/protocol/) |
| How is the code arranged? | [`docs/architecture.md`](docs/architecture.md) |

Completed work is not deleted — it moves to the journal, which keeps the dated record intact.

## Where we are

**Phase 0 — scaffold.** Done.

**Phase 1 — PS5 LAN remote play. Working end-to-end against real hardware.** Pairing from scratch, connect,
video, audio, and controller input are all live-verified. The recorded live figures are **1080p60 at 0.4%
loss, 23.2 Mbps, handshake 2.1 ms, RTT 6.5 ms, 18 ms demux→present** — recorded in the Track D entry
below and in [`docs/journal.md`](docs/journal.md). (A previous revision said the latency/bitrate figures were
unlogged and cited a commit that no longer existed; the figures *are* recorded. Corrected 2026-08-02. The
hashes went too, in 2026-09-09's sweep — see [`docs/README.md`](docs/README.md) on why pre-publication
hashes do not resolve.) What's left in Phase 1 is polish and a couple of production-path gaps,
not research.

| Stage | State |
|---|---|
| 0 — ProtocolLab harness + replay | Done |
| 1 — Cloud sign-in (OAuth2) | **Done and live (2026-08-17).** The credential decision closed 2026-08-07 and the credential is bundled under its own `NOTICE` section; sign-in, the account's console list and cloud wake all work from the app. Still not on the critical path for LAN play against an already-paired console, and a build made with `-p:BundleOAuthClient=false` does without it entirely. |
| 2 — Discovery (cloud list + LAN SRCH/mDNS) | Done — verified against a real PS5 on the LAN |
| 3 — Session orchestration (create/wake/OFFER) | Done for the LAN path (live). **Wake is wired** as of the account work — local broadcast first, the account service as a fallback for a console off this network, reported as `AskedRemotely` rather than `Woken` because acceptance is not confirmation. **OFFER** (cloud signaling) is now built and live-exercised end to end, but the exchange never completes: the console will not join a session this client creates. See the top block. |
| 4 — Direct transport + `/sess` framing + stream demux | Done, live |
| 5 — Crypto seam (registration + control + stream) | **Done and live.** Control-plane field crypto verified byte-for-byte; stream (A/V) crypto verified against real console video; registration/pairing reversed, implemented, and verified live from scratch. Then made fast: batched CTR keystream + PCLMULQDQ GHASH took per-packet cost 216 µs → 7.7 µs (~28x), which is what unlocked 1080p60. |
| 6 — Media + input wiring | Done, live. D3D12 present, **GPU decode via MFT/DXVA** (`MF_LOW_LATENCY`, decoder picked by codec, HEVC + 10-bit P010 supported), WASAPI audio with bounded latency, controller input (GameInput + DualSense raw HID). |

**Phase status vs. the original deferral plan:**
- **Phase 2 — PS4 support. CLOSED 2026-08-05.** Nothing open. The full record of how it got there —
  including the falsified leads and the four differing constants — is in
  [`docs/journal.md`](docs/journal.md) under "Phase 2 — PS4 support, closed 2026-08-05".
- **Phase 3 — DualSense advanced features.** *Partially done* — raw-HID **input** shipped (PS button,
  touchpad click, USB full / BT compact / BT full report formats). Output (haptics, adaptive triggers,
  lightbar) and gyro are still not started.
- **Phase 4 — Adaptive bitrate / WAN relay.** Mostly not started. `HalyardStreamingSession.cs` feeds real
  RTT/loss into `AdaptiveBandwidthController.ReportNetworkSample` and calls `RecommendBitrate()`, sending the
  result to the console via `ReportConnectionQuality` — this wiring is committed (see below). It's
  bitrate-only: per Track C's
  settled findings, `CONNECTION_QUALITY` can't steer resolution (fixed at launch), so this doesn't make
  Phase 4 "done" — WAN relay and the mid-session-bitrate question (below) are still open.

## 1.0 — scope

**Settled 2026-09-11.** The four questions this draft opened with have been answered by the owner and are
recorded under "Decisions" below, along with one constraint those answers collide with. Scope changes from
here are changes, not refinements.

The backlog below has 44 open items and no line through it, which means it cannot answer "are we done yet".
This section draws that line. It is deliberately a *definition* first, because every argument about whether
something belongs in 1.0 turns out to be an argument about what 1.0 is for.

### What 1.0 means

> **Someone who is not the author can download a build, pair their own console, and play — on PS5 or PS4 —
> without hitting a defect the project already knows about.**

That sentence does the work. It admits the things a stranger cannot work around (no build to download, a
pad that goes dead, a toggle that does nothing) and excludes the things they will never notice (a frame of
demux latency, a GHASH hot path, a missing wordmark). It says nothing about feature completeness, because
this is a 1.0 and not a 2.0.

Three consequences worth stating, since each closes an argument:

- **Feature work is not what is left.** The protocol is done for both console generations, LAN and WAN both
  stream, and the app is a working client. What is left is almost entirely *verification*, *distribution*
  and *three known bugs*.
- **"Works on the author's machine" is the thing 1.0 has to stop relying on.** Most of the remaining risk
  is that large parts of the app have never been exercised by a human with a pad in hand, and that CI has
  never run at all.
- **Anything deferred gets said out loud in the release notes.** Shipping without haptics is fine.
  Shipping without haptics and letting someone discover it is not.

### In scope

**1. A build someone can actually run.** There is no release artifact today: `WindowsPackageType=None`, no
installer, no publish profile in use, no tag. 1.0 needs a downloadable x64 build, instructions that work on
a machine that has never had the SDK on it, and a tagged release to hang them from.

**2. CI green on a real runner.** `.github/workflows/ci.yml` has existed for days and has never executed.
It is the only thing that would have caught the launch crash, the shallow-clone gap, and the commit-message
violation without someone noticing by hand. Until it runs once, it is a file, not a check.

**3. The three known defects.** All were found on hardware and all sit in the primary path:
- an Xbox pad is dead while a DualSense is attached — the single most likely first-run configuration for
  someone with a console and a spare controller;
- HDR is reported from the wrong display, so the readiness panel lies on a multi-monitor desk;
- a two-line console name overflows its card, which is the first screen anyone sees.

**4. The hardware-verification debt.** Six input commits, Stage A steps 8–10 and two Stage B checks landed
without ever being driven by a person. This is not code — it is an afternoon with a pad, a console and a
list. It is also where the three bugs above came from, so the expectation should be that it finds more.

**5. Controls that do what they say.** `LargeUiScale` is wired: `UiScale` holds the policy, `AppScale`
applies it to the WinUI resources at startup, and the switch now says it takes effect on restart. What is
left is not code — it is one look at a screen. See the accessibility item below for the question that is
still open, which has to be answered at 150% on a real display and cannot be settled by argument.

**6. Two protocol gaps that could bite a console we have never seen.** ~~The GMAC rotation-window boundary
and `CurveForVersion` having no answer for non-P521 versions.~~ **Both closed 2026-09-11.** The rotation
boundary turned out to be already live-validated across 223 windows and was mis-described here as a quiet
risk when its failure mode is loud; the curve gap is closed by refusing an unobserved version instead of
guessing at it. Everything else in Track B is an optimisation or an
open research question and can wait.

**7. An MSIX alongside the zip.** `EnableMsixTooling` is already on and `Package.appxmanifest` already
exists, so this is packaging and verification rather than new plumbing — but see the decisions below for the
signing constraint and the two behavioural differences a packaged build brings.

**8. `LargeUiScale` wired through.** App-wide scaling, which collides with the fixed console-card cell
height and wants the 150% OS text scale checked at the machine.

**9. Honest first-run docs.** What works, what does not, which console generations, and the fact that it is
English-only. The README is already unusually honest; 1.0 needs it to also be *complete* about limits.

### Explicitly out, and said so in the release notes

- **DualSense output** — haptics, adaptive triggers, lightbar, gyro, touchpad drag. Input works; output is
  a whole subsystem and its absence is not a defect.
- **Track D's ten latency and quality items.** The stream already runs 1080p60 at 23 Mbps with 18 ms
  demux→present. Every one of these makes a working thing better.
- **WAN relay (Phase 4).** Peer-to-peer WAN already works through STUN. Relay is the fallback for networks
  where it does not, and nobody is blocked on it today.
- **Trimming.** `PublishTrimmed=False` costs download size, not correctness. Six reflection sites in the
  cloud layer stand between here and flipping it.
- **ARM64 binaries.** The code is architecture-clean and ARM64 is a first-class *build* platform, but no
  ARM64 build has been verified recently and the author's machine is x64. Ship x64; let ARM64 build from
  source until someone can test one.
- **Translations.** The catalogues exist and a speaker can contribute one; shipping an unreviewed machine
  translation would be worse than English.
- **A purchased code-signing certificate.** The zip is unsigned and SmartScreen will say so on first run;
  the README has to say so first. The MSIX is self-signed — see the decisions below for what that costs.
- **The wordmark**, the senkusha probe questions, and the remaining accessibility items other than
  `LargeUiScale`.

### Exit criteria

1.0 ships when every line is true:

- [ ] CI passes on a real runner, on a clean clone, for every job.
- [ ] A tagged release exists with an x64 zip and an x64 MSIX attached, and install steps for both
      verified on a machine without the SDK.
- [ ] The MSIX has been installed and launched from its signed package, not just built - including a pair
      and a stream, because packaged data paths and packaged resource loading are both different.
- [ ] `LargeUiScale` changes the UI, at 100% and at the OS 150% text scale — and at 150% the text is
      scaled once, not twice. See `UiScale.AppliesOsTextScaleItself`.
- [ ] The three known defects are fixed, each confirmed on hardware.
- [ ] The input stack, Stage A steps 8–10 and the two Stage B checks have been driven by a person, and
      whatever that finds is either fixed or listed.
- [ ] No user-visible control is inert.
- [x] The GMAC window boundary and the non-P521 curve gap are resolved — see Track B.
- [ ] `SECURITY.md` names a supported version rather than "no release yet", and GitHub private
      vulnerability reporting is enabled.
- [ ] The README states the limits a first-time user meets in their first ten minutes.

### Decisions (settled 2026-09-11)

**Distribution: a zip and an MSIX.** The zip is the primary artifact and the one the README points at. The
MSIX exists for people who want Start-menu integration and clean uninstall.

**Code signing: no purchased certificate.** These two decisions meet at a real constraint, so it is written
down rather than discovered during packaging: **Windows will not install an MSIX that is not signed by a
certificate the machine already trusts.** An unsigned `.msix` is not a thing a user can double-click, so the
MSIX has to be signed with a *self-signed* certificate whose thumbprint is published beside the download,
and installing it means trusting that certificate first. That is a worse first run than the zip, which is
why the zip leads. If that trade is unacceptable the honest options are to drop the MSIX until there is a
real certificate, or to publish it for people who will re-sign it themselves — but "MSIX, unsigned,
double-click to install" is not available.

**Two consequences of shipping an MSIX that are easy to miss, and both need testing rather than reasoning:**

- **A packaged app stores its data somewhere else.** `DefaultPlatformPaths` resolves under
  `LocalApplicationData`, which Windows redirects for packaged apps. Someone who pairs a console with the
  zip and then installs the MSIX will find their paired consoles gone, and the DPAPI-protected credential
  blob is written per-user in that same tree.
- **Resource loading differs between packaged and unpackaged.** The XAML catalogue resolves through MRT,
  and the packaged path is a different one from the unpackaged path the app runs today. Both need to be
  launched, not just built — this is the failure mode that produced a launch crash this week, where the
  markup compiled, the PRI indexed, and the app died on load.

**`LargeUiScale`: wire it.** It becomes real 1.0 work rather than a one-line hide. It is app-wide scaling,
it collides with the fixed console-card cell height already noted in the backlog, and the plan wants the OS
text scale verified at 150% at the machine — so it lands with the hardware pass rather than before it.

**The account tier is in the supported surface.** Sign-in, the account console list, cloud wake and the
account pairing route are all part of what 1.0 claims to support, and therefore part of what the hardware
pass has to exercise.

## Backlog

### Follow-ups from the settings-page crash (cause found and fixed 2026-08-06)
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
- [ ] **Sweep the remaining native call sites for the same hazard.** `VideoCapabilities` and the media/input
      interop are reachable from several places; only `AboutPage` demonstrably had the pattern right. Worth a
      structural guard rather than a convention, on the `BundledInteropConstantsTests` precedent — this is
      exactly the class of rule that prose has already failed to hold in this repo.
- [ ] **`MFStartup` from an STA is the suspected specific mechanism** but is `[X]`: the fix was verified by
      behaviour (Settings opens and renders), not by establishing which of the two calls in that function is
      the one that cannot tolerate the apartment. Worth knowing before writing the structural guard, since it
      determines whether the rule is "no MF on the UI thread" or something broader.


### Open — nothing requests a keyframe when no frame has *ever* decoded (carried over 2026-08-06)
- [ ] `KeyFrameRequested` is raised in exactly **one** place: catastrophic decode backlog,
      `jobs.Count >= MaxQueuedFrames` (`D3D12VideoDecodePipeline.cs:240-251`). **Nothing triggers a keyframe
      request for "submitted many access units and never decoded a single frame."** That is a loop with no
      exit: if the first IDR (carrying SPS/PPS) is lost or arrives before the decoder is ready, the MFT accepts
      every later access unit and returns `MF_E_TRANSFORM_NEED_MORE_INPUT` forever — the failure
      `VideoRenderer.cpp` already documents — so no frame decodes, so the queue never backs up, so the one
      trigger never fires. The stall watchdog does not fire either: it distinguishes a static scene from a dead
      session by console activity, and the console is still talking.
  - Kept open after the codec-detection fix above, which explained the observed instance without needing this.
    It remains a genuine hole in the recovery path, reachable whenever a first IDR is genuinely lost.
  - **Fix shape:** request a keyframe when no frame has decoded within N ms of the stream coming up, bounded
    and rate-limited. The plumbing already exists — `HalyardStreamingSession.RequestKeyFrame` is a no-op before
    the stream is up and is rate-limited internally, and `SessionController.OnKeyFrameRequested` already knows
    about both sides. Only the trigger is missing, and its event-source reason string would need to stop saying
    "decoder backlog resynchronisation".
  - Still unvaried, and still not implicated by anything: the launchSpec pins
    `"videoEncoderProfile":"hw4.1"` unconditionally (`HalyardStreamingSession.BuildLaunchSpecJson`), an
    H.264-shaped profile token reproduced verbatim from a vendor capture.


### Open question — `AsyncObservable` can end a sequence without signalling it (2026-08-05)
- [ ] **`AsyncObservable.Create` swallows `OperationCanceledException` and then raises neither `OnCompleted` nor
      `OnError`** (`src/Ripcord.Core/Reactive/AsyncObservable.cs`). Any consumer that waits for a terminal signal
      therefore waits forever if the producer ends that way. `AddConsolePage` knew this — its comment said "a
      disposed subscription raises neither OnCompleted nor OnError" — and guarded it with a per-family
      cancellation registration, which only helps when *our* token is cancelled, not when the producer throws
      OCE on its own.
  - **Symptom seen live (2026-08-05):** the add-console progress bar never disappeared while the user watched the
    results list, i.e. with nothing cancelled. Worked around in `AddConsoleFlow` by making the search *window*
    the authority on when a scan ends and treating the scanner's terminal signal as a fast path, so the spinner
    is now self-limiting no matter what the transport does. Pinned by
    `Scan_ThatNeverSignalsCompletion_StillEndsAfterTheWindow`.
  - **Not established:** *why* a discovery family went quiet in that run. The workaround makes the UI symptom
    impossible, but the cause is unproven, and the same primitive is used by the session/streaming path — where a
    silently-ended sequence would not have a convenient window to fall back on. Worth understanding before
    trusting `AsyncObservable` in a new place.
  - Deliberately **not** changed here: making `Create` signal on cancellation is a one-line change to a Core
    primitive the streaming path depends on, and it does not belong in an app-layer refactor.


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
- [ ] **Eyeball high contrast and transparency-off.** `AppEffects` reads both correctly and the branches are
      trivial, but the HC-on rendering has never been *seen* — both are system-wide settings and toggling them
      changes the whole desktop, so it belongs to whoever is at the machine.
      (Left-Alt + Left-Shift + PrintScreen toggles HC.)
- [ ] **The remaining page renames and splits** from the same plan section: `ConsolesPage` → `HomePage`,
      `AddConsolePage` → `PairPage` (never cached — a flow must start clean), `KeyBindingsPage` →
      `ControlsPage`. Only `HomePage` should be cached, to keep grid scroll position and the realized
      containers `PrepareConnectAnimation` needs.
- [ ] **`LargeUiScale` / `TextScaleFactor`.** The plan wants the OS text scale verified empirically at 150%
      *before* building the fix, which is another at-the-machine check.


### Open bug — an Xbox pad is dead while a DualSense is attached (found on hardware 2026-08-06)
- [ ] **With both pads connected, the Xbox pad produces no input whatsoever.** Not "input arrives and is
      dropped" — instrumented at the router, **not one frame with a button set arrives at all** across ~35,000
      frames while the pad was connected and being pressed. The DualSense works throughout (it comes in on the
      raw-HID engine, a separate path).
  - **Cause is in `GameInputControllerSource`**, which polls
    `GetCurrentReading(GameInputKindGamepad, nullptr, …)`. `nullptr` means "most recent reading from ANY
    gamepad", and a Bluetooth DualSense — which GameInput also enumerates — reports continuously and wins that
    race essentially always. The class's own summary already scoped itself to "a single polled device"; what
    was not appreciated is that the single device is chosen by *whoever reported last*.
  - **Disconnecting the DualSense is not sufficient.** The Xbox pad had to be **re-plugged** before GameInput
    would read it, so the binding is not re-evaluated when the competing device goes away.
  - **Not a regression.** Before the input router, chrome read GameInput alone, so the same starvation applied.
    What changed is that the DualSense now works in menus (via raw HID), which makes the Xbox failure the
    visible half of a problem that was always there.
  - **`CompositeControllerSource`'s doc comment claimed this was fixed** — "with a DualSense attached an Xbox
    pad could not be used at all … it was the act of choosing that broke it". Running both engines fixed the
    choosing and the hot-swap case, not this one. Comment corrected in both classes rather than left to
    mislead the next reader; the merging itself is correct and is simply never handed the second pad's frames.
  - **Fix:** enumerate GameInput devices and read each explicitly instead of passing `nullptr`, publishing one
    frame per device so the composite can merge them. That is native work in `Ripcord.Input.Interop`
    (`GamepadReader`) plus the managed source above it, and it is the real "several controllers act as one
    pad" the composite already advertises.


### Stage C landed — steps 5–10, all UNTESTED ON HARDWARE (2026-08-06)
- [ ] **Hand-test the whole input stack.** Six commits landed without a pad in hand.
      Everything below builds, launches and passes 695 tests; none of it has been driven by a human.
  - **Focus invariants**: the watchdog re-seeding, `FocusAnchor` keeping the caret on the same
    discovered console when the list reorders, focus returning after a dialog. The anchor needs **two consoles
    answering at different times** to fire at all — with one console it never runs and a passing test proves
    nothing.
  - **Dialogs**: every prompt should now own the pad. Check the mid-session disconnect prompt
    especially, and that focus returns to the card you opened a menu from.
  - **Controller text entry**: the one most likely to be wrong on first contact. Does the keypad
    reach the login-pin dialog? Is shift-as-one-shot right? Backspace removes the last character and there is
    no caret movement — a deliberate call, and one you may disagree with after typing an IP address.
  - **Key bindings page**: rebinding should no longer throw focus to the top of the page.
  - **Hint bar** (`afcdccc`): does it read at couch distance, and does the mode hysteresis feel right when a
    mouse is nudged mid-session?
  - **Touch**: the 48px targets, and the one thing deliberately NOT wired — whether press-and-hold
    on a console card raises `ContextRequested`. The container has a `ContextFlyout` so it should, but that is
    reasoning, not observation. If it does not, wire `Holding` with `handledEventsToo: true`.


### Open bug — HDR is reported from the wrong display (found on hardware 2026-08-06)
- [ ] **`VideoCapabilities::IsHdrDisplayAvailable` answers "is ANY connected display HDR", not "is the display
      this window is on HDR".** It enumerates every output of every DXGI adapter and returns true on the first
      one whose colour space is `DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020`
      (`src/Ripcord.Media.Interop/VideoCapabilities.cpp`).
  - **Observed:** a laptop panel that is HDR-capable and HDR-enabled, with the app window on an external
    monitor that is neither. Settings offered HDR; the display actually showing the video cannot present it.
  - **The "every adapter" loop is deliberate and correct for what it was written for** — on a hybrid laptop
    the display often hangs off the integrated GPU while decoding happens on the discrete one. The bug is the
    question being asked, not the enumeration.
  - **Fix:** take the window handle, resolve its monitor with `MonitorFromWindow`, and match that against
    `DXGI_OUTPUT_DESC1::Monitor` — answering for the output the video will actually appear on. Note the window
    can be dragged between monitors mid-session, so the answer is not a one-time startup fact and the setting
    should re-evaluate on a display change.
  - **Not attempted here:** this is a native `Ripcord.Media.Interop` change plus an interop signature change,
    and it needs MSBuild rather than `dotnet build`. Filed rather than half-done, because a probe that looks
    fixed and still answers the wrong question is worse than one known to be wrong.


### Stage C — step 2 landed, step 3 is gated (2026-08-06)
**Standing note for this branch:** every UI change since 2026-08-06 has been verified by launching the app and
driving it through UI Automation, not by building it. That habit exists because building cleanly and passing
182 tests said nothing about a Settings page that crashed on open, and because the accessibility work above
crashed on launch the first time and was caught the same way.
- [ ] Steps 5–10 (focus invariants, ModalHost, soft keyboard, KeyBindingsPage, hint bar, touch and keyboard
      passes).
- [ ] **Steps 3–4 need a pad to confirm.** Both landed 2026-08-06; the full record of what they changed is
      in [`docs/journal.md`](docs/journal.md) under this stage.
  - **Step 3 (`InputRouter`/`InputScope`)** — the two things it claims to fix are exactly the two needing
    hardware: a DualSense working *in the menus* (it previously worked only in a stream, because the chrome
    read GameInput only), and the mid-session dialog — open the disconnect prompt with a controller, check
    the pad reaches its buttons, that the combo that opened it does not stay held inside the game, and that
    "Stay connected" resumes forwarding. Menu navigation generally is worth a glance on the same pass
    (auto-repeat, deadzone, B-to-go-back): the whole path from frame to focus was rewired.
  - **Step 4 (`FocusPilot`)** — scroll rate and feel, North on a card and on the hero, and directional
    movement generally.


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
- [ ] **`LargeUiScale` is still cosmetic-only** and collides with the fixed console-card cell height — see the
      card-layout entry below. Stage B's problem, recorded here so it is not discovered as a surprise.


### Console-card layout — fixed cell height is a standing constraint (noted 2026-08-05)
The clipped "Played 31 min ago" line is fixed (the container's 12px gutter margin was being subtracted from
`ItemsWrapGrid.ItemHeight`, so the card was 164 tall while its rows were sized for 176), and the slack now
lives in an empty row so a shortfall closes up whitespace instead of chopping text. Two limits remain, both
for Stage B's card redesign rather than a patch:
- [ ] **A two-line display name still overflows.** `MaxLines="2"` on the name plus a fixed cell height cannot
      both be honoured — a long nickname needs ~28px the cell does not have. Either the name goes single-line
      with an ellipsis (the tooltip already carries the full name) or the card stops being fixed-height.
- [ ] **Fixed `ItemHeight` is incompatible with the planned text scaling.** `LargeUiScale` and the OS
      `TextScaleFactor` both grow every line in the card while the cell stays put, so the same clipping
      returns at 150%. This is the general form of the bug above, and it applies to every fixed dimension in
      the app — which is why the design-system work makes sizes tokens rather than literals. `ItemsWrapGrid`
      requires a fixed item size, so a scalable card means either binding the cell size to the same scale
      factor or moving to `ItemsRepeater` — and the latter costs the built-in gamepad focus behaviour that
      `GridView` was chosen for in the first place.


### Track A — Live-test the streaming-quality work
> **Plan written 2026-08-02:** `captures/console_session_plan.md` (dirty room) batches every remaining
> hardware-gated item across this track and Tracks B/C into one trip, in a fixed order — Phase 1 needs the
> console *asleep*, a state you get once per session, so the ordering is load-bearing rather than advisory.
- [ ] **Do the senkusha probes actually succeed? — RESTATED 2026-08-02.** This previously read "first live run
      of the senkusha probes, never executed against hardware", which is **wrong**:
      `HalyardStreamingSession` runs the bring-up on *every* connect, and the console requires it between
      `/sess/ctrl` and the stream or it never answers the stream's SESSION exchange. We stream successfully, so
      it has run many times.
  - What has never been checked is whether the probes **succeed or silently fall back**. `RunSenkushaAsync`
    swallows timeouts by design and an unconfirmed probe leaves the interface-derived estimate in place, so a
    completely non-functional probe is externally indistinguishable from a working one.
  - **Half of it is a ~2-minute diagnostics read.** `MtuConfirmed` false means we fell back to the interface
    estimate — that half is unambiguous and settles the MTU probe.
  - **CORRECTION 2026-08-02: the RTT half cannot be read this way, and an earlier version of this entry said
    it could.** `DeclaredRttMs` is *not* null when the echo probe fails: `RunSenkushaAsync` seeds `samples`
    with the two handshake round trips and only *replaces* them if the echo probe returns, so a non-null RTT
    is reported even if all ten pings are lost. It is null only when the whole bring-up fails. Distinguishing
    echo success from handshake fallback needs a **new signal** — the cheapest being to surface whether the
    echo probe contributed, rather than inferring it from a value that always has a fallback behind it.
  - The stranding worry in this item was **real and is now fixed**: the client-MTU close was not
    on a `finally`, so a probe timeout unwound past it and was swallowed, leaving the console in client-MTU
    mode. Re-verifying with a deliberately failed probe (block 9297 briefly) is still worth doing.
- [ ] Senkusha **bandwidth** probe — still blocked on never having observed it. See Track C.


### Track B — Phase 1 production-path gaps
The stack connects and streams; these are the bits that still lean on dev-machine scaffolding.
*(Already done, previously listed here: session factory (`HalyardSessionFactory`), DPAPI-backed credential
store (`PairedConsoleStore`, `dpapi:` prefix), pairing UX (`PairConsoleDialog` → live registration), first
live end-to-end connect.)*
- [ ] **Trimming is off. The JSON blocker is gone; three smaller ones are not.** `PublishTrimmed=False` in
      every config. It was breaking Release because trimming disables `System.Text.Json` reflection and the app
      died on first deserialization. **Task #33 landed 2026-08-02** and trim analysis now reports **zero JSON
      warnings** for `Ripcord.Core`, `Ripcord.Protocol.Halyard` and `Ripcord.App`. What still stands between
      here and flipping the flag:
  - **`Ripcord.Cloud.Halyard` still uses reflection** (6 × IL2026). Not an attribute away: it serialises
    *anonymous types* and deserialises through a *generic helper*, neither of which source generation can see,
    so the DTOs need to become real types first. Off the LAN path. **No longer parked behind the OAuth
    decision** — that closed 2026-08-07 and the account tier shipped, so these six warnings are now the
    largest app-code blocker to `PublishTrimmed=true` and are workable on their own merits.
  - ~~**One non-JSON reflection site**: `IDeviceIdentity.cs:69` calls `Type.GetMethod` (IL2075).~~ **GONE
    2026-08-07.** Replaced with a direct `RegGetValueW` P/Invoke. It was not only a trim warning: the
    reflection resolved `Microsoft.Win32.Registry` inside `Ripcord.App` (`net10.0-windows`) and silently
    returned nothing in every neutral `net10.0` host, so the device id was correct in the app and **empty in
    `ProtocolLab` on the same machine** — which took account sign-in down with it. Verified byte-identical to
    the registry value afterwards, so existing pairings (which use the same id as the registration client id)
    are unaffected. `PlatformSeamTests.DeviceIdentity_ResolvesOnWindows_FromANeutralHost` guards it.
  - **Whether WinUI 3 itself trims cleanly — ANSWERED 2026-08-06.** Measured: a trimmed Release publish of
    `Ripcord.App` produces **37 ILLink warnings, all IL2075/IL2081, every one from CsWinRT's ABI layer** —
    generic fallback initialisers for `IReadOnlyDictionary`, `IVectorView`, `IAsyncOperation` and friends.
    **None come from app code.** So it does not trim cleanly, but it fails in one contained place rather than
    diffusely, which makes the flag a question about whether those fallbacks are reachable at runtime rather
    than a question about the whole UI stack. Still: do not assume the flag flips once the two items above are
    done — the CsWinRT warnings have to be understood first, and this only established their shape.
    (Measured while evaluating `CommunityToolkit.WinUI.Controls.SettingsControls`, which proved trim-neutral:
    identical warning count and published size with and without it.)
  - Worth keeping in view: published size is the *only* thing this buys, and it matters mainly for handheld
    deployment. It is not on the path to anything else.

#### Open — needs a console or a capture to resolve
- [ ] **Does the console honour a mid-session target bitrate?** Unresolved, and the answer changes the design
      of everything downstream. Evidence leans *no* (16.1 Mbps measured against a 13.5 Mbps target), but later
      readings were confounded by VBR noise (20 → 34 → 20 Mbps at a fixed 40 Mbps cap).
  - Can't be tested as the code stands: `SessionConfig` is captured once in `SessionPage.StartSessionAsync`
    and never re-read, and `IBandwidthController` has no setter.
  - **Hook to build first:** `AdaptiveBandwidthController.ForcedBitrateKbps` (nullable) overriding the ladder
    in `RecommendBitrate()`, plus F4 on the session page cycling off/20/10/5 Mbps so a sweep needs no
    rebuild. Label it "forced" in the HUD; gate the key behind the diagnostics overlay.
  - **Do NOT test by causing congestion** — fatally confounded. Needs a *perfect* link with an artificially
    *low* target, so only compliance can explain a drop.
  - Protocol: 40 Mbps cap, HEVC, adaptive + connection reporting both ON (the target only reaches the wire
    when reporting is on), park on a **static** scene, ~30 s baseline, force to ~⅓ of baseline, watch
    30–60 s, verify loss stays 0.0% or the run is void, then release and confirm recovery.
  - **If ignored:** resolution is fixed at launch, so the only real response to a sustained bandwidth collapse
    is a reconnect at a lower `bwKbpsSent` — user-visible, so prompted/opt-in, never silent. Don't build
    reconnect-on-collapse before knowing.
  - **Status: the hook is still not built.** (Task #15.)
- [ ] **Congestion packet size is version-negotiated — which version do we actually get?** `cap47` sends a
      **15-byte** type-5 packet (what we implement); the older `session8` sends a **23-byte** one with a
      different field layout entirely (sequence @1, 90 kHz timestamp @3, GMAC @15, key position @19). Both are
      the vendor client against the same console, so something — client build, console firmware, or the
      negotiated protocol version — selects between them, and `HalyardTakionStream` hardcodes the 15-byte form.
  - ~~Cheap next step: pull the negotiated protocol version out of both captures' handshakes and correlate.~~ **That step does not work and was checked 2026-08-02:** neither capture contains a version negotiation at all — zero datagrams with base byte `0x06`/`0x07` in either `session8` or `cap47`. The only other signal, the `/sess/init` pubkey length, resolves to P-521 in both and so narrows only to versions 0x0d–0x11, the same bucket. The heading is right: this needs a console, or a new capture pairing a version negotiation with a congestion packet. If
    the 23-byte form belongs to a version we can be offered, this is a latent break.
  - Low urgency: 1080p60 runs fine today, which means either we get the 15-byte version or the console
    tolerates/ignores malformed congestion reports. Worth knowing which, since "ignored" would also mean our
    loss reporting has never actually reached the console.
- [ ] **Capture `BANDWIDTH_COMMAND` (command=3).** The one senkusha piece never observed — and **absent from
      two independent LAN captures** (`session8`, `cap47`), so this is no longer "we haven't looked," it's "it
      doesn't happen on a LAN under the settings we've used."
  - What `cap47` *did* buy: the probe sequence is byte-identical to `session8` from a **different client IP**
    (`.195` vs `.100`) — independent corroboration of the whole implementation, same constants (1454, 1254,
    `num=1`, ids 1/1/2), 53 senkusha packets, no command 3.
  - Remaining triggers, cheapest first: (a) confirm what quality setting `cap47` used — if it was already
    *automatic*, that hypothesis is dead; (b) vendor app on *automatic* quality if it wasn't; (c) a
    deliberately degraded Wi-Fi link; (d) an actual **internet** session.
  - **Handle remote captures as credential material** — they carry PSN auth tokens. Gitignored captures dir
    only.
  - Why it's worth the trouble: a real bandwidth measurement is the honest input to `bwKbpsSent`, which is the
    one lever we *know* changes the console's resolution choice. Today that number is the user's configured
    cap, which is a guess about the link rather than a measurement of it.


#### Correctness risks — need verification
Both need a console or a capture to settle, hence here rather than in Track D.
- [x] **GMAC rotation window boundary — closed 2026-09-11, and it was never the risk this entry described.**
      The item asked for validation "against a known-answer vector or a live capture at that exact boundary".
      That validation already existed and predates the entry: `ps5-remoteplay-v1-spec.md` §5.4 records the
      chained form GMAC-verifying **13670 of 13672 A/V packets across 223 rotation windows**, the two misses
      being a torn capture tail, and marks it **[V] live-validated 2026-07-22**. Rotation is not an untested
      path; it is one of the better-evidenced things in the crypto.

      Two corrections to what this entry claimed:

      - **The stated symptom was backwards.** It predicted "an intermittent, very hard-to-diagnose auth
        failure roughly once per rotation window". Key positions advance fast enough that a real session
        crosses windows continuously, so a wrong rotation kills the stream seconds in, every time — which is
        exactly what the superseded form did before it was corrected. A bug here is one of the loudest
        available, not one of the quietest.
      - **The committed fixture cannot reach it.** `stream_packet_vectors.json` carries key positions from 9
        to 720 — all of window 0. So the shipped vectors could never have pinned rotation regardless of how
        many boundary tests were written against them, and nothing said so.

      What was actually missing was a guard on the *form*, since the real historical bug was folding
      `aes_key` where the spec folds `key0`. Both are one plausible line, both yield a correct-looking
      16-byte key, and both satisfy every "does it change at the boundary and hold inside the window" test.
      `GmacRotationFormTests` now recomputes the spec's formula independently and, separately, requires the
      superseded form to *disagree* — so a revert has something to trip over rather than moving both sides
      together.
- [x] **ECDH curve gap for non-P521 protocol versions — closed 2026-09-11 by refusing rather than guessing.**
      The decision this entry offered was "validate it, or throw `NotSupportedException` until we can". We
      cannot validate it — no capture of any other version exists — so `CurveForVersion` now throws, naming
      the version and saying why.

      The fallback was not a conservative default, it was a retracted guess still in force: the spec says in
      as many words that *"the earlier 'v1 uses P-256, the default' was a guess"*, then corrects only the
      range it had confirmed. The guess stayed behind as the `else` branch.

      Removing it found more than expected, because a silent default is used by whatever forgets to think:

      - `GenerateEphemeralPublicKey(int protocolVersion = 0)` defaulted to version 0, which landed straight
        on the guessed curve. The parameter is now required, at the interface and both implementations.
      - `GenerateKeyPair(curve = NistP256)` was the same trap one level down, and is now required too.
      - **Two mock consoles negotiated version 9**, a number no console has been observed using. So the two
        most end-to-end tests in the suite — full handshake, key agreement, decrypt a server packet — were
        running entirely on the unvalidated P-256 branch, while the curve real hardware uses went untested
        there. Both now speak version 17, the one our own capture negotiated.

      P-256 itself is untouched and still selected from a 65-byte peer key by `CurveForPublicKeyLength`,
      which is evidence rather than inference. To support another version, observe one and widen the
      validated range — do not reinstate a default.


### Track D — Quality / latency leftovers
- [ ] **D3D12-native decode path (mode 3)**, behind a capability check. Today decode is MFT/DXVA. (Task #14.)
      Measure before building — the current path already hits 1080p60 with headroom.
- [ ] **True PTS-based A/V sync.** Audio latency is *bounded* (ring ceiling 200 ms → trim oldest to 100 ms,
      channel-aligned), not synced. Audio may now slightly *lead* video — fine for menu clicks; if lip-sync
      feels off, raise `TargetLatencyMs` toward the video latency.
- [ ] **Demuxer early-flush** — we flush a frame on the *next* frame's first packet, costing ~1 frame interval
      (~16 ms @60). Naive early-flush breaks wire-loss accounting (parity units arriving after the flush
      counted as loss → false congestion feedback → console needlessly drops bitrate); tried it, 4 tests
      failed, reverted. Correct shape is a present-early/account-later split: `EmitFrameIfNeeded()` guarded by
      a `_frameEmitted` flag on source-complete, with stats and teardown staying on index-change. **Only do
      this if measurement says the demuxer is a meaningful chunk.**
- [ ] Marginal: `CODECAPI_AVLowLatencyMode` on the decoder (needs `<codecapi.h>`). `MF_LOW_LATENCY` is
      already set and did help; this is the incremental one.
- [ ] `SessionConfig.LatencyMode` is currently **unused** in the launchSpec — possible minor lever if it maps
      to a low-latency hint.
- [ ] **GHASH is still ~83% of the per-packet GMAC cost even on PMULL** — not the incidental allocations.
      Measured on ARM64 (1426 B packet, 91 AAD blocks): `AesGcmCore.Mac` ≈ 9.4 µs, of which GHASH's 93
      multiplies are ≈ 7.9 µs (0.085 µs each). The lever is therefore the standard GHASH aggregation —
      precompute a table of H powers and fold 4–8 blocks per reduction — which cuts the reduction count
      proportionally. Worth it only if measurement says the A/V loop is still the constraint.
- [ ] Minor, and **not** where the remaining time goes: the per-call `Aes.Create()` in `AesGcmCore.CreateEcb`
      (fresh CNG key handle per packet, hit by both `Mac` and `Encrypt`) measures **1.23 µs — ~11%** of
      per-packet A/V crypto, and `ComputeTag`'s `packet.ToArray()` is 0.15 µs. Arch-independent and unfixed on
      **both** x64 and ARM64: `CreateReusableEcb` exists but has exactly one caller,
      `HalyardPacketCrypto._payloadAes` (the CTR payload cipher), so the GMAC side never got it. If it is
      cached, note the GMAC key **rotates** every 45000 key positions — cache per rotation window, not per
      session, or it fails minutes into a session at the first boundary. See `PacketCryptoHotPathTests`.
- [ ] **A/V loop allocation churn is what remains, and it is a latency/jitter item, not a throughput one.**
      Post-PMULL the receive queue oscillates 0-18 at 1080p60 (it used to sit flat at 0 on x64 at lower
      bitrate), and present-fps peaks slightly above target — burst-then-catch-up rather than steady state.
      Sources, all per packet at ~2000 packets/s:
  - Two ~1.4 kB copies per datagram — `HalyardStreamDemuxer.TryOpenMedia`'s `packet.ToArray()` and
    `HalyardPacketCrypto.ComputeTag`'s — ≈ 5.8 MB/s of Gen0 churn.
  - `HalyardPacketCrypto.GmacKey` takes its `window != 0` branch on **every** packet once a session passes key
    position 45000 (a few seconds in), doing a `BigInteger` multiply, an allocating `IvAdd`, and a SHA-256
    fold each time. The "deliberately left uncached" comment there guards against caching it *per session*;
    memoising by **window index** is safe, since the key is a pure function of that index. Keep two entries so
    a UDP reorder across a boundary still hits.
  - Not urgent at ~2.7% core load. Do it if the queue peaks bother you, or before pushing toward 4K.
- [ ] **`StreamHealthAssessor.ReceiveQueueBusyDepth` (16) is now below observed-healthy peaks.** It was
      calibrated when the receive queue sat flat at 0; ARM64 at 1080p60 peaks at 18 with a healthy 0.4% loss.
      It gates the split between the two loss verdicts, so a Wi‑Fi blip taking loss past `LossWarnRatio` (2%)
      while the queue happens to be at an 18-deep peak would print "Your device is struggling to keep up —
      close other apps, or lower the stream resolution" for a genuine *network* problem, on a machine with
      ~35x crypto headroom. That is the ARM64 misattribution inverted. Prefer requiring the depth to be
      **sustained** over raising the constant: the signal wanted is "climbing toward capacity", not "briefly
      nonzero". Needs depth samples from more than one device before retuning.
- [ ] **`AdaptiveBandwidthController` cannot tell wire loss from loss we inflicted on ourselves.** It sees
      only `NetworkSample.LossRatio`, so a full `AvQueueDepth` (our processing shedding via `DropOldest`)
      reads as a bad network and it steps the console down — exactly what happened on the ARM64 first run,
      and the step-down needs a reconnect to apply so it degrades the session without fixing anything.
      `StreamHealthAssessor` already makes this distinction for the *user* using `ReceiveQueueDepth`
      (`ReceiveQueueBusyDepth`); the controller should get the same signal and hold the ladder steady (or
      surface "we are the bottleneck") instead of blaming the link.

### Track E — Controller (Phase 3 continuation)
- [ ] Confirm whether the feature-report 0x05 activation actually upgrades DualSense BT to report **0x31**.
      Only needed for touchpad-xy + gyro; everything else works via compat mode today.
- [ ] Retest DualSense over **USB** (earlier failure looked like device/Windows state, not our code — the
      raw-HID path is the same one the capture tool used successfully).
- [ ] **Remaining input validation** (task #27). Confirmed working: Xbox pad, DualSense, keyboard, and
      hot-swap between pads. DualSense over **Bluetooth** was confirmed on 2026-07-24 — but on the
      *pre-composite* single-engine path, so it needs a **regression retest** now that
      `CompositeControllerSource` runs both engines at once. Genuinely untested: a pad and keyboard
      **together** (the merge path is unit-tested but has never seen two real devices at once), and keyboard
      play-testing on the Ally.
- [ ] **Unconfirmed input values** — small, and all marked as such in-code so they can't be mistaken for
      settled. None affect streaming today.
  - **D-pad Up/Down (`0x80`/`0x81`) are assigned by elimination.** Both were pressed during an earlier
    misbehaving-input period in the capture, so their first-press times can't order them. Left/Right are
    directly timed. One clean press of each in any future capture settles it.
  - **Touchpad click has no confirmed code.** The scripted step produced no eighteenth code; `0x91` is
    retained so the feature keeps working. It may instead ride the `00 00 00 21` event family the history
    parser currently skips.
  - **Motion full-scale range is underived**, so real gyro/accel can't be sent at correct magnitude yet
    (needs a capture with known applied motion). Harmless today — we never populate them — but a prerequisite
    for the gyro work below.
  - **Orientation packing is underived**; the writer sends a documented zero placeholder. Also inert until
    motion data exists.
  - **History parser reaches 65% of packets** — at least one further `00 00 00 <subtype>` form remains
    undecoded. Only matters for *reading* console-side history, which we don't do.
- [ ] Touchpad **finger-drag** wire events — the input writer only sends touchpad *click* today.
- [ ] Gyro calibration (parser leaves gyro/accel null pending it).
- [ ] **Phase B output** — haptics, adaptive triggers, lightbar. Needs a console→client haptics path that
      doesn't exist yet (cf. the haptics byte in the audio packet layout).


### Track F — UX
- [ ] **Controller-friendly UI overhaul — mostly landed; re-scoped 2026-08-02.** The original framing ("no
      obvious connect affordance", "exit is mouse/keyboard only") is **stale on both counts**: `ConsolesPage`
      is the startup page and gives every console an accent "Connect" button, and the pad exit gesture ships
      as the *default*. Pad input is still suppressed from the UI **while streaming**, which is deliberate.
      *Partly addressed* — the touch flyout now covers the buttons a non-PS pad can't reach (PS, Create,
      touchpad) plus diagnostics and exit, and the hold-to-exit gesture (Options+Create+L1+R1) is confirmed
      working on the Ally. **Menu navigation also ships** — directional focus with auto-repeat and a
      configurable stick deadzone, covering every page hosted in the nav frame. Two real gaps remain, and they
      are what this item is now about: it binds `GameInputControllerSource` **directly rather than the
      composite source**, so a DualSense arriving purely over raw HID cannot drive menus; and focus search is
      scoped to the window content, so directional movement does not reach inside `ContentDialog` popups
      (activation does). **The `ContentDialog` gap narrowed 2026-08-04**: the add-console flow — by far its
      biggest victim, being a whole multi-field form — is now a page in the nav frame, so it inherits the
      working directional focus. What still cannot be reached by pad is the small stuff: the rename dialog,
      the remove confirmation, and `LoginPinDialog`. Smaller and more uniform than it was, which makes the
      real fix better scoped.
- [x] **Localization — done for both layers; no second language ships.** `Ripcord.Presentation` carries
      its text in a `.resx` (108 entries) and `Ripcord.App`'s markup in `Strings/en-US/Resources.resw`
      (188 entries), every element reached by `x:Uid`. Seven guards across `LocalizationTests` check both
      catalogues in both directions and require a translator comment on every entry. Verified
      cross-platform: the portable layer builds for `linux-x64` and `osx-arm64`, satellite assemblies are
      produced, and a culture switch resolves them; the XAML half was verified by dumping the built PRI
      rather than trusting the build, because an unindexed `.resw` fails silently at run time.
      **Adding a language needs no code** — see `CONTRIBUTING.md`.
  - **Deliberately still literal, and it is the same rule in both layers:** the diagnostics report, the F3
    overlay and its 25 XAML labels, exception messages for programmer error, protocol and product
    identifiers, an example IP address and a row of bullet characters. Audience decides: text a maintainer
    reads when helping you follows the maintainer.
  - **No translations ship**, deliberately. An unreviewed machine translation is worse than honest English.
- [ ] **Accessibility backlog.** Three of these are pre-existing; the redesign made the first more visible
      rather than causing it.
  - **`LargeUiScale` is applied, but one premise under it is still unverified.** The mechanism landed:
    `UiScale` (portable, tested) decides the multiplier, `AppScale` writes scaled sizes into the WinUI
    resources before the first window exists.

    Two things were learned building it that contradict
    `docs/history/app-reimagining-plan.md`, and the plan is left as written because it is a historical
    record — the corrections live in `AppScale`'s own documentation, which is where someone changing this
    will be standing:

    1. **The plan's mechanism cannot work.** It proposed overriding WinUI's font-size keys at app level. The
       stock text ramp reaches them through `StaticResource`, not `ThemeResource`, and
       `XamlControlsResources` *defines* those keys — so a reference inside it resolves locally and never
       escalates to `Application.Resources`. Overriding them changes nothing. Control-internal text is the
       opposite (`{ThemeResource ControlContentThemeFontSize}`) and is handled the easy way, so there are two
       mechanisms in `AppScale` because the platform has two behaviours.
    2. **The premise may be false, and getting it wrong is worse than doing nothing.** The plan asserts WinUI
       3 desktop ignores `UISettings.TextScaleFactor`, and then says in italics to verify that before
       building on it. Nobody did, and Microsoft's text-scaling documentation says the opposite. If the
       platform already applies the OS factor and we multiply by it too, a user at 150% gets a 225% Ripcord —
       an accessibility setting breaking the layout it was meant to rescue. So the app-level switch is
       currently a flat 1.3× and the OS factor is not in our arithmetic at all.

    **The open item is one observation:** set Windows text size to 150%, launch Ripcord, see whether its text
    grows. If it does not, flip `UiScale.AppliesOsTextScaleItself` to `true` — that constant exists to be the
    only thing that changes. `UiScaleTests` asserts the factor is applied exactly once either way.
  - **Screen-reader pass over the card grid.** Each card composes its own `AutomationProperties.Name`
    (name, family, status, action) because a `GridViewItem` whose content is a panel has none of its own —
    but reading *order* across a wrapping grid is not automatic and has not been checked with Narrator.
  - **Green-beside-red in the vendor palette** is inert while no Nintendo family exists, but Xbox-green and
    Nintendo-red would sit adjacent in the family picker the day one does. `brand/README.md` already flags
    this trio as the palette's weak point. The mitigation is in the design — the text label is the primary
    carrier, never the colour — but it wants re-checking rather than assuming.
- [ ] **Wordmark.** No typeface chosen, so nothing ships the name as artwork. Blocks a proper wide tile and
      splash lockup — both currently mark-only.
- [ ] **Pair a console port from the desktop app — proposed 2026-09-16, not started.** A port asks the
      desktop to sign in on its behalf: the port enters a pairing mode and announces itself on the LAN, a
      running Ripcord on a PC or Mac sees it offered in its own UI, does the PSN sign-in and the console
      registration with a real browser and a real keyboard, and hands the finished pairing record back.

      **What it is for.** Registration needs two things a small port is bad at asking for: the PSN account
      id — a nineteen-digit number — and the console's PIN. `ports/ripcord-ps3` now reads the account id out
      of the PS3 itself when that PS3 is signed in to PSN (see its `DECODE.md`), and when it is not, the
      best it can do is say so and suggest signing in. That is a real improvement and it is not the same as
      not asking: a port on hardware that cannot sign in to PSN at all, or a user who does not want to,
      still has nineteen digits to find and type. This removes both questions from the television rather
      than making them easier, and it removes them for **every** port rather than for the one that happened
      to have a readable cache.

      **Where it belongs.** In `ports/common` and behind a `Ripcord.Presentation` seam, not in any one port.
      The console half is a UDP announce and a small transfer; the desktop half is a discovery source, a
      confirmation, and a reuse of the registration it already performs. Neither half is PS3-specific and
      writing it as though it were would mean writing it twice.

      **Three constraints, and the first is the one to design around.**
      1. **The record is secret material.** `registkey` and `companion` are tied to one console and one
         account — the wrong side of `CLAUDE.md`'s generic-versus-personal line — so they must not cross a
         LAN in clear, and an announce that anything on the network can claim is an announce that hands them
         to whoever asks first. The obvious shape is a short code shown on the television and typed into the
         desktop, which authenticates the pairing and keys the transfer in one step. That is a design
         decision, not a detail to leave to the implementer.
      2. **The port must still work alone.** This is an *alternative* to the on-screen keyboard, never a
         replacement: a console with no PC on the network has to keep the flow it has.
      3. **It does not need the cloud tier.** Sign-in happens on the desktop, which already has it. Nothing
         about this asks a console to speak OAuth — see `ports/ripcord-ps3/DECODE.md` on why that is not a
         road worth starting down.


### Track G — Deferred phases
- [ ] Phase 4 — WAN relay (and real adaptive bitrate, gated on Track C's bitrate experiment). **Advanced but
      not done as of 2026-08-17:** the rendezvous, push channel, STUN and cloud wake are built and committed
      on `feat/account-and-wan`, and every cloud step is live-confirmed. Blocked on account-based device
      registration — see the top block. The media *relay* leg is untouched beyond the observation in
      `ps5-wan-relay.md` that the relay carries real media at the same ports and framing, so it is
      re-addressing rather than a new transport.

## Hard rule — never commit

Captures, extracted constants, key material, and vendor binaries stay in the gitignored
`docs/protocol/captures/` dirty room. In particular the **live vectors** in
`registration_crypto_vectors.json` / `control_crypto_vectors.json` — as distinct from the extracted tables in
the same files — carry our own console's registration key, live nonces, the real MachineGuid, and an account
id. Publishing those exposes our own console and account with zero interop benefit, and it is irreversible
once in a public history. The skipped `Live*VectorTests` are doing exactly the right thing.
