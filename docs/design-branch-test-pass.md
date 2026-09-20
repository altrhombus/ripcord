# Test pass — `feat/app-design-direction`

**The question this answers: what changed on this branch that a person has to look at?**

Everything below was built and unit-tested and **none of it has been seen on a screen**. Written 2026-09-13
against fifteen commits from `main`. Delete this file once the pass is done and its findings are in
[`ROADMAP.md`](../ROADMAP.md).

```
git checkout feat/app-design-direction
msbuild Ripcord.slnx -p:Platform=ARM64 -p:Configuration=Release -m
```

ARM64 Release was cross-built clean from an x64 host on 2026-09-13 (exit 0, `Ripcord.App.exe` produced). It
has never been *launched* on an ARM64 machine, so a crash at startup is a finding in its own right and not a
sign you did something wrong.

**Baseline before you start: everything is green.** 366 presentation tests and 785 protocol tests pass, with
6 skipped — those are the live-vector tests that self-skip without the dirty-room fixtures, which is correct
on a machine that does not have them. The branch is rebased onto `main` and the working tree is clean.

If you see a failure, it is real. That was not true a few days ago — the suite carried two redaction-sweep
failures from the PS3 port's vendored third-party sources, and they are gone: `main`'s `.gitignore` already
covered them, and this branch was simply too old to have those rules.

---

## Capture these first, before touching anything

One of the priced items in `ROADMAP.md` can only be answered at a machine, and only on ARM64 — which is why
this session is worth more than an ordinary look.

### `ReceiveQueueBusyDepth`, on ARM64 — the one that gates a verdict

The constant is 16. The roadmap records observed-healthy ARM64 peaks of **18**. It decides which of two
sentences a struggling player is shown:

```
loss >= 2%  and  receive queue >= 16   ->  "Your device is struggling to keep up"
loss >= 2%  and  receive queue <  16   ->  "Losing packets on the network"
```

Those send people to opposite ends of the house, and this branch promotes that sentence to rung 1 where it is
the only thing on screen. So the number has to be right, and right on ARM64 specifically, because that is the
hardware the current value appears to misjudge.

**You do not have to read it off the screen.** Every session now writes a trace automatically:

```
%LOCALAPPDATA%\Ripcord\state\session-trace-<timestamp>.csv
```

One row per stats tick (twice a second), one file per session, started the moment a connect begins. It
carries the receive queue alongside loss, frame rate, latency, bitrate, decode queue, pipeline latency and
the decode path, plus a `#` preamble naming the adapter, the requested resolution and **the thresholds the
build was compiled with** — so a trace read later does not depend on anyone remembering which constants
produced it. There is no console name, address or account in it.

Rung 3's **PIPELINE** group still shows `queues  decode N · receive M` live if you want to watch, and the
panel prints the trace's path when it opens so you can find the file on a handheld.

#### The two runs have to be produced differently, and that is the whole point

The pairing is not "healthy vs. lossy". Both branches of the verdict require loss ≥ 2% already; what separates
them is whether the *device* is also behind. So produce each condition with a lever that moves only one of
them, or the result cannot distinguish anything.

**Run A — the network is dropping packets and the device is fine.** Expect a LOW queue.

- Wired or strong Wi‑Fi, **720p60** so the decoder has plenty of headroom, hardware decode left on Automatic.
- Introduce loss without touching the CPU: stream over a congested 2.4 GHz band, or start a large sustained
  upload on the same link and leave it running.
- If you want a dialled-in number instead of a congested one, `clumsy` (WinDivert-based, single exe, no
  install) drops a set percentage of inbound UDP. Check it has an ARM64 build before relying on it; if not,
  the congestion route is fine and is closer to what a real player hits anyway.
- Wait for loss to sit above 2% for a while and let it run. **The trace file is the deliverable.**

**Run B — the device cannot keep up and the network is fine.** Expect a HIGH queue.

- Wired or strong Wi‑Fi, **1080p60 at a high bitrate**.
- Force software decode from inside the app: **Settings → Advanced → Which GPU to use → Choose a specific
  GPU**, then pick an adapter the list annotates as having **no hardware video decoding**. Rung 3's
  **path** row should then read `software` rather than `zero-copy`, which is your confirmation the lever
  worked.
- If every adapter on the machine decodes in hardware, fall back to CPU contention: peg all cores with a busy
  loop for the duration.
- Stop the stream when you have a few minutes of it. **The trace file is the deliverable** — no need to read anything.

**Run C — baseline.** 1080p60, hardware decode, healthy network, a few minutes. **Record the peak.** This is
the number that has to sit safely *below* whatever threshold you pick, or a healthy ARM64 stream keeps getting
told its hardware is at fault — which is the bug being chased.

#### What to send back

The three CSVs. Nothing needs reading or labelling — the decode-path column tells run B apart from the other
two, and the loss column separates A from C, so the files identify themselves.

What is being looked for: C (healthy) < A (network loss) << B (device starved), with daylight between A and B
and comfortable clearance above C.

If **A and B overlap**, that is the more interesting result and it is worth more than a tuned constant: it
means receive-queue depth does not separate the two causes on this hardware, and the verdict needs a different
discriminator — decode time or presented-vs-decoded frame drift are the obvious candidates. Record the numbers
and leave the constant alone rather than picking one that happens to fit one run.

---

## What to test, in the order it matters

### 1. Connect — every session meets it, and it changed most

The 460 px centred card with a spinner is gone. Identity top-left, status low-left, centre empty.

- [ ] Connect to a console that is **awake**. The console's mark and name appear top-left; status sits
      bottom-left; nothing is centred.
- [ ] Connect to a console in **rest mode**. The trail — three uneven dashes in the console's accent — lights
      the second dash while waking and the third when connecting.
- [ ] **Does the identity land where the card's mark was?** The `ConnectedAnimation` is supposed to make this
      read as the card becoming the window. If it jumps, say so; that is the whole idea of the composition.
- [ ] Make a connect **fail** (pull the console's network, or power it off mid-connect). The trail stops at
      the dash it failed on and stays there. Try again / Back to consoles are reachable.
- [ ] Force a **reconnect** mid-stream. The trail should be *absent* — reconnect has no phase, and a trail
      claiming progress there would be lying. An indeterminate bar appears instead.

### 2. The HUD — three rungs, one key

- [ ] **Rung 1.** With a healthy stream: nothing on screen. Make it lossy and wait — a single line appears
      bottom-left after about five seconds. It should *not* appear for a brief wobble, and should clear itself
      about six seconds after the stream recovers.
- [ ] **F3 shows and hides.** Press it, press it again — the HUD must go away, never deeper. This is the
      interaction the design exists to protect; if a second press ever reveals more, that is a bug.
- [ ] **Details** on the summary strip opens rung 3. F3 from there hides; F3 again returns to **rung 3**, not
      to the summary.
- [ ] **Rung 2's four numbers.** Only the ones past their threshold wear colour. On a healthy stream all four
      are white; on a lossy one, loss and frames colour and the other two do not.
- [ ] **Sparkline colours mean something now.** The loss line used to be red at zero loss. It should be
      neutral white until loss crosses 2%.
- [ ] **Rung 2 vs the touch bar.** *The one I could not reason out.* Both live on the bottom edge. On the
      handheld, summon the touch controls with the HUD at rung 2 and see whether they collide.

### 3. HUD placement — where the panel goes

- [ ] **Fullscreen 16:9** → rung 3 overlays the picture. Expected: this is the only case that covers video.
- [ ] **Ultrawide or a very wide window** → it becomes a rail in the black pillar beside the picture, covering
      nothing. Needs a pillar of 300 px or more. *Untested on real hardware and the legibility of a 268 px rail
      is a judgement I made from a mockup.*
- [ ] **A window taller than 16:9** (drag it tall, roughly 1920×1600) → it turns on its side into the bar under
      the picture, as five columns, and must not grow over the video.
- [ ] Resize across those thresholds and watch for the panel jumping shape mid-drag.

### 4. Settings — re-tiered

Sections are now **Picture / Controls / When you play / Accessibility / Account / Advanced**.

- [ ] Every control still works. The page moved a lot of markup; a lost `x:Name` shows up as a crash on open,
      not at build time.
- [ ] There is **no "Larger text and controls"** switch and no Accessibility section. Ripcord now leaves text
      size to Windows; if the OS scale is set to 150% the app should follow it on its own, and if it does not,
      that is a platform finding worth recording rather than a reason to bring the switch back.
- [ ] **Diagnostics is a three-way picker** (Off / Summary / Full), not a switch. Set it to Full, restart,
      connect — the stream opens at rung 3.
- [ ] **Settings migration.** Before switching to this branch, note what your existing `settings.json` says.
      After running this build, every other setting must be unchanged. Nine unit tests cover this, but the
      failure mode it guards against is *silently losing every preference*, so it is worth one real look.

### 5. Home page — dialogs

Rename / Details / Remove moved out of code-behind into the string catalogue.

- [ ] All three open, read correctly, and do what they say.
- [ ] **Details** omits rows the console does not know. A console that reports its own name and has not been
      renamed should *not* show a "Reported name" row duplicating the title.
- [ ] **Remove** names the console it is about to forget.

### 6. Tokens — cheap to check, easy to regress

- [ ] The **About** page and the session overlays: no corner looks wrong. Ten hard-coded radii were replaced
      with tokens and the touch bar's was deliberately changed from 14 to 12.

---

## Per surface, per input

From [`history/app-reimagining-test-plan.md`](history/app-reimagining-test-plan.md), which still applies:

```
[ ] REACH    every interactive element can be focused/activated with this input alone
[ ] ACT      once reached, every element performs its action
[ ] ESCAPE   the surface can be left with this input alone
[ ] VISIBLE  focus or pressed state is always visible, legible at 2 m
```

The one to spend real attention on is **the new Details button on rung 2 with a pad** — it is a new control on
a surface that deliberately takes no gamepad input, so the honest answer may be that it is pointer/touch and
keyboard only, and that the three-way setting is how a pad-only player chooses their rung. If that feels wrong
in the chair, it is a design finding rather than a bug.

## Known and not worth reporting

- The rung-3 sheet reads status → target → metrics rather than leading with the sparklines. Deliberate: the
  existing vertical order was preserved so the rail and overlay did not change. Reordering is one line if the
  sheet reads badly.
- No card, hero, first-run or pairing-celebration work has been done. That is waiting on regression baselines
  — screenshots of the grid and session page in light, dark and high contrast — which have to be captured
  *before* those surfaces are touched.
