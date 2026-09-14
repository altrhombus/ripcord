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

**Baseline before you start:** 379 presentation tests and 768 protocol tests pass. The protocol suite also
reports one or two failures from `ports/ripcord-ps3` — third-party mbedTLS in the working tree, and H.264
clause numbers from the H.264 spec, in that branch's blobs, being matched by the redaction sweep's IPv4
pattern - dotted four-part numbers, which is what a clause reference and an address have in common. Both are false positives, neither is from this branch, and `rev-list --all` reaches the second one
whatever is checked out.

---

## Capture these first, before touching anything

Two of the four priced items in `ROADMAP.md` can only be answered at a machine, and **one of them can only be
answered on ARM64** — which is why this session is worth more than an ordinary look.

### 1. `ReceiveQueueBusyDepth`, on ARM64 — the one that gates a verdict

The constant is 16. The roadmap records observed-healthy ARM64 peaks of **18**. It is the discriminator
between *"Losing packets on the network"* and *"Your device is struggling to keep up"*, so today a healthy
handheld can be told its own hardware is at fault — and this branch promotes that verdict to rung 1, where it
becomes the only thing on screen.

- Stream something demanding, healthy, for a few minutes. Open rung 3 (below) and watch **queues → receive**.
- Record the peak.
- Then make it genuinely lossy (see below) and record it again.
- Wanted: a number with daylight on both sides. If healthy peaks at 18 and lossy sits at 30, 24 is a
  defensible threshold; if they overlap, that is a more interesting finding and the verdict needs a different
  discriminator.

### 2. Text scale at 150%

Set Windows text scaling to 150% before launching. `UiScale.AppliesOsTextScaleItself` is `false` — an
assumption its own file warns may be wrong, and if it is, everything renders at 225%.

- Is text roughly 1.5× or roughly 2.25×?
- Then turn on **Settings → Accessibility → Larger text and controls**, restart, and check it is 1.3× on top
  of the OS factor rather than a second multiplication of it.

### Making a stream lossy on purpose

Needed for the HUD work as well as the capture above. Easiest first: put the PC on Wi‑Fi, walk away from the
router, or stream 1080p60 at 40 Mbps over a congested band. Failing that, start a large upload on the same
link.

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

- `ports/ripcord-ps3` failing the redaction sweep. False positives, other branch.
- The rung-3 sheet reads status → target → metrics rather than leading with the sparklines. Deliberate: the
  existing vertical order was preserved so the rail and overlay did not change. Reordering is one line if the
  sheet reads badly.
- No card, hero, first-run or pairing-celebration work has been done. That is waiting on regression baselines
  — screenshots of the grid and session page in light, dark and high contrast — which have to be captured
  *before* those surfaces are touched.
