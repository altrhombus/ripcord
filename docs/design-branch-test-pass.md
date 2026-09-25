# Test pass — the app design work

**The question this answers: what a person has to look at, on the surfaces the design work rebuilt.**

Written 2026-09-13 for `feat/app-design-direction`; last revised 2026-09-24, when that branch merged. The
branch name is gone from the title because the work is on `main` and the script outlives it. Delete this file
once the pass is done and its findings are in [`ROADMAP.md`](../ROADMAP.md).

```
msbuild Ripcord.slnx -p:Platform=ARM64 -p:Configuration=Release -m
```

**What has now been seen on a screen, and what has not.** The ARM64 build has been launched and streamed on
real hardware (2026-09-19), so "does it start" and "does it stream" are answered. Four sessions were recorded
and the measurement section that used to open this file is **done** — see the journal. What has *not* been
looked at is everything under "What to test" below: the surfaces were exercised incidentally while chasing
numbers, not walked deliberately.

**Baseline: everything is green.** 452 presentation tests and 791 protocol tests pass, with 6 skipped — the
live-vector tests that self-skip without the dirty-room fixtures, and the ARM PMULL paths, which is correct on
a machine that has neither.

If you see a failure, it is real.

**Three findings already came out of the hardware sessions, and all three are fixed** — listed so they are
not re-reported: the session trace recorded an empty GPU name; the trace's health column was lagged one row
behind its own numbers; and a console that was powered on the whole time was shown as unreachable because a
single probe datagram went missing. That last one is worth a deliberate re-check on the same VLAN, since it
is the only one whose fix cannot be proven off-device.

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

Sections are now **Picture / Controls / When you play / Account / Advanced**, and Advanced is an
expander rather than a heading. There is no Accessibility section: the switch it held was removed when
Ripcord stopped scaling its own text.

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
- [ ] **Reachability, on the VLAN that broke it.** This is the one fix on the branch that cannot be proven off
      the device, so it is worth reaching for deliberately. Open the list with the console powered on and
      across the VLAN, several times. It should now settle on a real status rather than claiming it cannot be
      reached; the probe gets three asks instead of one.
- [ ] **And the dead end is gone regardless.** Even when a console genuinely is switched off, the action is
      now only *dimmed* — never disabled. Confirm it is still pressable, and that pressing it produces a
      connect attempt that says what went wrong rather than nothing happening. A silent probe must never be
      able to lock you out of a console that is sitting there working.

### 6. Tokens — cheap to check, easy to regress

- [ ] The **About** page and the session overlays: no corner looks wrong. Ten hard-coded radii were replaced
      with tokens and the touch bar's was deliberately changed from 14 to 12.

### 7. The console card and its wedge

The card was rebuilt around the play wedge, and hero and grid are now one template at three densities — so a
fault in one is a fault in all three, which is the point.

- [ ] **Hover and press the wedge.** Three different marks: a wash on hover, a brighter bleed on press, the
      system ring on focus. If press is indistinguishable from hover, say so — the press state is the app's
      primary action answering.
- [ ] **In high contrast, both new states must vanish entirely** and the shape must still read. The accent
      bleed painted colour here once; it is suppressed through `AppEffects.AccentWashOpacity` now.
- [ ] **Press a card cold** — first connect of a session, while the D3D12 device is still coming up. The
      press must land immediately even though the connect does not.
- [ ] **One console is still one press.** Open the window, press A. Focus must already be on the card at hero
      density; if it is not, the focus call is landing before the container is prepared.
- [ ] **A two-line console name** no longer overflows. Set a long nickname and check all three densities.
- [ ] **Last played** reads on its own row and is not truncated at grid density.

### 8. Pairing — discovery leads, and sign-in is the lazy path

- [ ] **The flow opens on the search**, not on a family question. The family is asked only for a typed
      address or a scan that found nothing.
- [ ] **No dead Xbox button** anywhere in the flow.
- [ ] **Signed out, sign-in leads** and the code form is not drawn yet. The link to it is present,
      unweighted, and reaches the form in one press.
- [ ] **Sign in from the pairing step and stay there.** This is the fix for a real complaint: it used to
      navigate to Settings and leave the flow abandoned behind you. Expect the modal over the step you were
      on, and when it closes, the same step with the account id filled in — and, if the account already knows
      this console, no code box at all.
- [ ] **Cancel the sign-in.** No error bar, and the offer is still there.
- [ ] **The celebration.** Mark at hero size, "Paired." with a period, **Play now** drawn as the wedge. Focus
      must be on Play now, not the name box — on a handheld the name box opens a soft keyboard over half the
      screen for a field nobody has to fill in.

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
- ~~No card, hero, first-run or pairing-celebration work has been done.~~ **All of it has now landed** —
  §7 and §8 above cover it. The regression baselines this was waiting on were never captured, so the
  before-set does not exist; see below for what that changes.

## Regression baselines — and these do not need the ARM64 machine

The repository contains **no product screenshots at all**, and the card redesign is the highest
visual-regression risk left in the plan. Capturing these costs about an hour and unblocks the largest
remaining piece of design work.

**Capture them wherever is convenient, including an x64 desktop.** Nothing in the layout is
architecture-conditional — the only place the app reads the processor architecture at all is one string in
the F8 diagnostics report. Same Windows App SDK, same theme resources, same control templates.

**The constraint is consistency, not architecture.** A baseline is only useful against a later shot taken
the same way, so whichever machine takes these has to take the comparison shots too. A diff between an x64
desktop and an ARM64 handheld shows display scaling, window size and GPU, not the change under review. Note
the machine, its display scale and the window size alongside the files.

**Two things genuinely do need the other hardware, and they are not baselines:** the pad and touch passes in
"Per surface, per input" above, and the reachability re-check on the VLAN. Those stay with the handheld.

Two surfaces, three themes, and the states that only exist under an input device:

```
[ ] Console grid   light · dark · high contrast      (a populated list, two or more cards)
[ ] Console grid   the empty state, all three themes
[ ] Session page   light · dark · high contrast      (needs a live stream)
[ ] Session page   each of the three HUD rungs       (hidden · summary · full)
```

**Amended: the card now HAS hover, press and focus states, and the before-set was never taken.** This note
used to say to skip those shots because there was nothing to capture — the card had no legible focus state and
no distinct hover state. That was true and is the reason the states were built. It also means these are no
longer *regression* baselines against the old card: that set does not exist and cannot be taken now without
checking out an earlier commit.

So capture them as the **first** set, of the card as it stands, and include the states:

```
[ ] Console card   rest · hover · pressed · focused      (dark, then light, then high contrast)
```

The value is unchanged even though the framing is: the next person to touch this card needs something to
diff against, and today there is still nothing in the tree.

- **Window size matters and should be recorded**, because HUD placement is a function of it: the panel becomes
  a rail, a sheet or an overlay depending on the viewport and the video's aspect. Capture the session page at
  a wide window and at something close to phone-narrow, or the baseline only covers one of three layouts.
- **PNG, not JPEG**, and name them `<surface>-<theme>-<state>.png`. Where they live is an open question —
  they are the first binaries of their kind in this tree — so park them outside the repo for now and let the
  card work settle the question of whether they belong in it.
- **High contrast is worth the most per shot.** It has been coded for and never looked at, and it is the
  theme where a hard-coded colour shows up as unreadable rather than merely off.
