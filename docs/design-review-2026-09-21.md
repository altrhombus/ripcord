# Second design review — `feat/app-design-direction`

**The question this answers: the direction is settled and half-built. Is it right, and what is missing?**

Written 2026-09-21, against `cf6ac0a`. This is a **review, not a settlement.** Nothing here amends
[`design.md`](design.md) until you decide it does; where I disagree with that file I say so and price the
disagreement, because the project's own rule is that reopening a settled decision is a change rather than a
refinement and the cost should be visible before it is paid.

Read [`design.md`](design.md) first — it is the thing being reviewed, and this file assumes it.

Mock-ups of the card's states, first run, the celebration and the density steps:
<https://claude.ai/artifact/VxrhXEgMJizpbnhXGAqh3L>

---

## Amended 2026-09-21, after review

Three findings were re-decided once the owner had read this file, and the work has since been built. It was
carried out from a written instruction set, `design-review-handoff.md`, which said to delete itself once the
work had landed and its remaining findings had homes — so it is gone. Its hardware checks are §7 and §8 of
[`design-branch-test-pass.md`](design-branch-test-pass.md) and the narrative is in
[`journal.md`](journal.md). **Where the amendments below disagree with the body of this file, the amendments
win.**

- **B3 is superseded.** One shared `ConsoleCard` control was the recommendation here. The hero instead
  becomes a *grid of one* — `ConsoleCardState` already carries everything `RenderHero()` set by hand, so the
  hero needed code-behind only because it was not inside an items control. Divergence becomes
  unrepresentable rather than discouraged, and ~110 lines of code-behind go.
- **E2 is softened.** This file said to delete the "which console" step. Xbox work is starting, so the step
  is repositioned instead: discovery leads and answers the question itself, the step survives for the
  manual-address path, and its buttons are built from implemented platforms so Xbox appears when it works
  and not before. No work is deleted.
- **The touch-bar collision is confirmed**, on the desktop as well as the handheld, and is now a defect
  rather than an open question — see §0 of the handoff for the arithmetic. Regression baselines are no
  longer a blocker: this has not shipped.

**One finding was added during implementation.** `AlertVisible` was suppressed while the connect overlay was
up but not while the HUD was open, and rung 2 leads with the same verdict in the same words — so a lossy
stream could stack the identical sentence twice against the bottom edge, underneath the touch bar. Fixed in
the portable layer.

---

## The verdict, before the detail

**The direction is right and the reasoning behind it is better than most shipping design systems manage.**
I went looking for the places where a design doc has talked itself into something, and mostly found the
opposite: the borrow-behaviour/own-expression line, killing the `NavigationView`, the hover-wash-versus-
focus-ring split, the facet-with-a-rim over the accent slab, the three-rung ladder with a toggle that never
deepens, `HudPlacement` claiming dead space, deleting `AppScale`, and keeping the wedge on a console that did
not answer — those are all correct, several are non-obvious, and I would defend every one of them in a room.

So this review is not about the direction. It is about three things:

1. **The app's stated job is to disappear, and the biggest opportunity to do that is not in the design at
   all.** It is one press away from being zero presses, and that press is currently unavoidable because
   Ripcord has no way to be launched *at* a console. See [A](#a-the-through-line-one-press-is-not-the-floor).
2. **The loudest element in the app is the only inert one.** The wedge reads as a button, is drawn at a third
   of the card, and responds to nothing — no hover, no focus, no press. There is no pressed state anywhere on
   the primary action of the product. See [B](#b-the-card).
3. **The surfaces that carry the whole delight budget are the ones that are not built,** and where they *are*
   built they contradict the settled design. Pairing asks the user the exact question `design.md` says the app
   must answer for them, and offers a console the app cannot stream. See [E](#e-first-run-and-pairing).

Everything is priced as **refinement** (inside the settled direction), **change** (touches a settled value),
or **reversal** (contradicts a settled decision).

---

## A. The through-line: one press is not the floor

### A1 — Ripcord cannot be launched at a console — *new capability*

Today the fastest path to a game is: launch Ripcord → home → press A on the hero. That is good, and the hero
layout earned it. But the audience this is for does not launch things from a Start menu — they launch from a
handheld's game launcher, from Steam Big Picture, from a pinned taskbar icon, from a controller. None of those
can express *"play on the living room PS5"*, because the executable takes no argument saying so.

**The load-bearing primitive is a launch argument, not a jump list:**

```
ripcord.exe --play "Living room PS5"     # by name or by console id
ripcord.exe --play-last
```

That one thing unlocks every entry point at once, including the ones I cannot anticipate:

| Entry point | Needs | Works in |
|---|---|---|
| Steam / Big Picture as a non-Steam game | the argument | zip **and** MSIX |
| A handheld launcher (Armoury Crate, Legion Space, Playnite) | the argument | zip **and** MSIX |
| A desktop/Start shortcut, offered from the card's overflow menu | the argument + a `.lnk` writer | zip **and** MSIX |
| The taskbar jump list | package identity | **MSIX only** — see below |
| `ripcord://play/<id>` | a registered protocol handler | either, with a registry write |

**On the jump list specifically, since you asked.** `Windows.UI.StartScreen.JumpList` needs package identity.
`WindowsPackageType` is `None` today, so in the zip — which ROADMAP names as the *primary* artifact —
`JumpList.IsSupported()` returns `false` and the API is simply unavailable. In the MSIX it works fine. Three
honest options:

- **Recommended: guard on `IsSupported()` and let it degrade.** The MSIX gets a real jump list for free, in
  about thirty lines and no interop; the zip gets nothing and loses nothing, because the zip user reaches the
  same behaviour through the shortcut the overflow menu offers them. Rebuild the list whenever the paired-
  console list changes.
- **Parity in both builds** means the Win32 COM path — `ICustomDestinationList` + `IShellLink`, plus
  `SetCurrentProcessExplicitAppUserModelID` so the taskbar groups correctly. It works unpackaged and it is
  perhaps 200 lines of CsWin32 interop. Worth it only if the zip having no jump list bothers you; given the
  zip user has a desktop shortcut that does the same job, I do not think it should.
- **Sparse package identity** gets you the WinRT API unpackaged, but it needs a signed package, which ROADMAP
  has already decided against buying. Not available.

**And a recommendation against the obvious alternative.** Do *not* ship "connect to X when Ripcord opens" as a
setting. Someone who opens the app to rename a console should not find themselves in a game. **Intent belongs
to the entry point, not to a preference** — a shortcut that says "play" always means play, and the plain icon
always means "open the app". That distinction is free, it is unambiguous, and it is the thing a preference
cannot express.

> **Price: new capability.** The argument is small. The shortcut-creation command is a `.lnk` writer and one
> flyout entry. The jump list is thirty lines behind a support check. I would put the argument and the
> shortcut in 1.0 and the jump list wherever it falls out.

### A2 — What the home page is *for*, once A1 exists

If the shell can launch you into a game, the home page stops being the road to play and becomes the place you
manage consoles. That does not mean it gets worse — the hero should stay exactly as it is, because the app
icon is still the most common entry point and one press is still the right cost there. But it does mean the
overflow menu grows the most valuable command in the product (**Create a shortcut**), and that "how do I get
here faster?" now has an answer the app can give.

---

## B. The card

### B1 — The wedge responds to nothing — *refinement, and the biggest single win here*

The card has a hover wash and a focus ring. The wedge has neither. It changes for exactly one input: `Muted`.

So the element drawn to read as the primary action — a third of the card, a play triangle, a lit diagonal — is
the one element in the app that never acknowledges you. Hover the card and a flat 9%-white rectangle appears
*behind* the wedge while the wedge itself sits still. That is the uncanny part, and it is why the card can
look finished in a screenshot and feel dead under a hand.

**The wedge should carry the card's interaction state.** This does not touch the hover-is-a-wash / focus-is-a-
ring rule — focus stays the ring and the wedge stays out of it. It is the *wash* gaining a second facet:

- **Hover:** the bleed goes from `0x24` to roughly `0x3A` and the rim to full-strength accent, over
  `RipcordDurationStateChange`. The diagonal lights; nothing moves.
- **Focus:** nothing extra. The ring owns it, deliberately, and adding a second focus mark here would
  reintroduce exactly the confusion the 2026-09-20 amendment removed.
- **Pressed:** see B2.

The control already has every property this needs (`AccentColor`, `RimBrush`, `Muted`); it wants two more
inputs and two storyboards.

### B2 — Nothing anywhere confirms a press on the primary action — *refinement*

- **Grid card:** `GridViewItem`'s stock pressed visuals paint the *container's* background, and the card's
  opaque `Border` covers it completely. Invisible by construction.
- **Hero card:** `RipcordCardButtonStyle` strips the template to a bare `ContentPresenter` — there are
  literally no visual states.

On a warm LAN connect the page changes in a second or two and nobody notices. The case that matters is the
cold one: the first connect of a session, where `PrepareAsync` is bringing up a D3D12 device and the UI thread
is busy. Press, nothing, press again. That is the oldest bug in interaction design and the app is currently
built to have it.

**Recommend one pressed treatment shared by both cards:** the bleed goes to full and the card scales to 0.985
for `RipcordDurationInstant` (83 ms), gated on `AppMotion.Enabled`. Windows 11's own tiles depress; this is the
Fluent-native answer rather than an invented one.

**And make the press the start of the handoff, not the navigation.** The moment the card is invoked, light the
wedge fully and leave it lit while navigation happens. The `ConnectedAnimation` then lifts an element that is
already in its destination state, so the transition reads as one motion rather than a jump-cut into a
different-looking thing.

This also closes ROADMAP's own exit criterion — *"No user-visible control is inert"* — in spirit, on the
control it matters most for.

### B3 — Two implementations of one card, already drifting — *change*

The grid card is a `DataTemplate` bound through `State.*`. The hero card is hand-written markup driven by an
imperative `RenderHero()`. They have already diverged, and every divergence favours the grid:

| | Grid card | Hero card |
|---|---|---|
| "Checking…" while a probe is in flight | `ProgressRing` | a grey dot — **breaks the grid card's own rule** |
| `PrimaryActionLabel` ("Wake & play") | absent | present |
| Tooltip repeating a truncated name | present | absent |
| Overflow button hit area | 48 × 48 | `SubtleButtonStyle`, no minimum — **under target** |

The first row is the one to care about. The grid card's own comment argues it: *"'finding out' and 'found out,
it is nothing' are opposite answers and must not look alike."* The hero card — the layout a one-console user
sees every single time — makes them look alike.

**Recommend one `ConsoleCard` user control with a `Density` property (`Grid` | `Hero`),** consumed by both
layouts. Every state becomes expressible once, the fourth row above fixes itself, and the regression baselines
ROADMAP wants have half as many surfaces to cover.

### B4 — The hero's overflow button is below the touch minimum — *refinement*

Covered in the table above; calling it out separately because it is a one-line fix on the layout a handheld
user meets most often, and `RipcordTouchTarget` exists precisely so this is not a judgement call.

### B5 — The grid card spends a third of its width on a mark that repeats what the card already says — *change*

280 px of card, minus a 92 px wedge, minus 16/100 of margin, leaves about **164 px of text column**. The code
comments record two clipping incidents caused by exactly that budget ("Played 11 Sep 2", and the row that lost
its bottom third), and ROADMAP records a third — a two-line console name overflowing, *"the first screen anyone
sees."* Three incidents in one constraint is the constraint telling you something.

The 33% is a *drawn* proportion, not a measured one. The hero can afford 35% because it is 520 px wide; 35% of
520 is a wedge you can see. 33% of 280 is a wedge that is already small *and* is eating the only text column
on the card.

**Options, in the order I would take them:**

- **(a) Let the cell breathe at desktop widths.** Covered by [D1](#d1--all-three-form-factors-cost-one-rule-not-three-modes)
  anyway: at ≥ 1920 the cell goes to 360 × 232 and the whole problem evaporates at the sizes where people have
  many consoles.
- **(b) Trim the grid wedge to ~68–72 px (24–26%) at the narrow step.** The slant is a ratio of height, so the
  signature is untouched — the angle is identical, the zone is narrower. The card gains ~22 px of text.
- **(c) Accept and truncate.** Which is what happens today, three times.

I would do (a) and (b). This is a *change* — it touches a proportion `design.md` states as a number — which is
why it is priced as one rather than slipped in.

### B6 — The hover wash is a `Visibility` toggle — *refinement*

Binary, instant, no transition. Every hover in Windows cross-fades, `Ripcord.Motion.xaml` defines the exact
tokens for it, and they are unspent. Opacity over `RipcordDurationInstant`.

### B7 — Hover is driven by raw pointer events rather than a visual state — *refinement*

`PointerEntered` / `PointerExited` / `PointerCanceled` on the `Border` is a hand-rolled `PointerOver`, and it
has the failure modes the stock one does not: a pen does not always produce the pair, and a pointer capture
taken elsewhere can leave the wash stuck on a card you have navigated away from. `VisualStateManager`'s own
`PointerOver` on the container is free and correct.

---

## C. Connect

### C1 — On a fast connect the player reads three headlines in two seconds — *refinement, highest value on this surface*

`Preparing video…` → `Checking credentials…` → `Connecting to your console…`, each at `TitleTextBlockStyle`,
on a LAN connect to an awake console. That is a strobe, and it is the **common** case — the case
`design.md` correctly says must not feel like a ceremony.

The engineering reason for announcing each phase before it runs is right and must be kept: when something
hangs, the last line on screen names it. The fix is not to say less, it is to **not promote a line that has
not earned the screen**:

> A stage's text appears only once that stage has been running ~600 ms. Before that, the headline holds the
> console's name.

A 1.8-second connect then shows one calm line and cuts to the game. A hang shows exactly what it shows today.
Nothing is lost and the common case stops flickering.

**And the app already has this pattern.** `HealthAlertGate` exists to decide when a message has earned the
screen over a running game, with hysteresis, tested, in the portable layer. Connect deserves the same gate,
written the same way, for the same reason. That is a symmetry argument as much as a UX one.

### C2 — The trail is the mark with the mark taken out — *change, and the best delight-per-pixel in the app*

`brand/README.md` is specific about why the dashes are uneven and right-aligned: *"uneven, because a tidy trio
is a fast-forward button; right-aligned, because that keeps the gap to the wedge constant and puts the
raggedness on the outside edge, where it reads as a trail rather than as a mistake."*

Both of those reasons are about the dashes' relationship **to the wedge**. On the connect screen the wedge is
absent, the dashes are lit left-to-right, and the raggedness is now *inside* the progress reading — so the
middle segment is 70% wider than the first for a reason the viewer cannot recover, and the shape reads as a
progress bar with odd segments rather than as the mark.

**Put the wedge at the head of the trail.** ~24 px, leading, with the three dashes behind it lighting as the
connect travels. Then it *is* the mark, doing the thing the brand document says the mark means — *"pull it and
it's away"* — at the one moment in the product where that sentence is literally true.

It is sub-second, it blocks nothing, it recurs without wearing out because it is never in the way, and it
costs one control instance. If there is a single frame in this app worth spending identity on, it is this one.

(It interacts well with C1: on a fast connect the trail barely lights, which is correct. The trail is for the
slow case, which is exactly when someone wants it.)

### C3 — There is no visible way to abandon a connect — *refinement*

`StatusActions` stays `Collapsed` until a stage is terminal. A rest-mode wake polls for up to 30 seconds with
nothing on screen offering a way out. Escape works; the app's own code comments establish that an
undiscoverable escape is a trap (*"without it the exit gesture is undiscoverable, which is what made a running
stream feel like a trap on a handheld"*) — the same reasoning applies here and has not been applied.

**After ~3 seconds of any non-terminal connect, show a quiet "Back to consoles" under the status line.** Not on
entry, which would be the ceremony the composition exists to avoid. Three seconds is about when a person
starts wondering, and offering the exit at the moment they wonder is the difference between patience and
worry.

### C4 — Spend the third personality room on the handoff, not the wake wait — *reversal, argued*

`design.md` names three rooms for personality: first run, a successful pair, and **the wake wait**. I would
keep the first two and challenge the third, using the document's own argument.

The wake wait is 10–25 seconds and it recurs every evening for anyone who rests their console. That is the
exact shape of the thing `design.md` rejects one section earlier: *"setup can afford ceremony because you do it
once, and connecting happens several times an evening. A ceremony performed daily is a chore."* A charming
wake wait is a chore with a smile on it. Keep it calm — name, trail, one line, nothing more.

**Spend it on the first frame instead.** Today the picture arrives as a cut. A ~150 ms opacity cross-fade from
the connect composition into the stream, with the identity mark the last thing to leave, is the moment the app
disappears — and making that deliberate is the most repeatable delight in the product, because it is over
before anyone could tire of it.

`Ripcord.Motion.xaml` already permits precisely this and nothing more: *"anything on the video layer but
opacity, under 150 ms."* The rule was written for this.

### C5 — Two near-identical strings for different states — *refinement*

`SessionPage_Starting` is `"Starting…"`; `StreamHealthAssessor` returns `"Starting up…"`. Same surface,
different meanings, indistinguishable at a glance.

---

## D. Form factor — desktop, handheld and couch

You asked whether all three can be served, and floated auto-detection with a manual opt-in. **All three, and it
costs one rule rather than three modes — and no setting at all.**

### D1 — All three form factors cost one rule, not three modes

Pad and touch reachability is already required by the handheld, and the couch adds nothing new to it. The only
real difference between a desk, a handheld and a sofa is **apparent size**, and the project has already made
the right call about who owns that: Windows does. Re-introducing a "TV mode" scale would be `AppScale` wearing
a different hat, and it would be wrong for the same reasons.

**But your Big Picture instinct is correct and it is not the same problem.** Windows sets display scale from
the EDID, and a 55" 1080p television commonly reports 100%. So a Big Picture launch lands a 292 px card on a
screen being read from three metres, and the user has tuned nothing because from their point of view they were
in a game launcher a second ago.

The distinction that resolves it:

> **Windows owns text size. Ripcord owns the size of Ripcord's own cards.**

That is not a reopening of the `AppScale` decision — it is the opposite of it. The type ramp stays the
platform's, untouched; what responds is Ripcord's own furniture, and it responds to **the viewport**, not to a
preference. `ItemsWrapGrid` needs a fixed item size, so it is a stepped rule at the breakpoints
`ApplyColumnCount` already uses:

| Viewport width | Columns | Cell | Hero |
|---|---|---|---|
| < 640 | 1 | 292 × 188 | 520 |
| 640–1007 | 2 | 292 × 188 | 520 |
| 1008–1919 | as many as fit | 292 × 188 | 520 |
| **≥ 1920** | as many as fit | **360 × 232** | **640** |

The top step is where a maximised desktop window and a Big Picture session on a TV both land, and one rule
serves both. Nothing to detect, nothing to choose, nothing for a user to get wrong — which is the same
doctrine that deleted the scale switch.

It also retires [B5](#b5--the-grid-card-spends-a-third-of-its-width-on-a-mark-that-repeats-what-the-card-already-says--change)
at the sizes where people own several consoles.

**If a manual override is ever wanted, make it an entry point rather than a preference** — the same argument as
[A1](#a1--ripcord-cannot-be-launched-at-a-console--new-capability). A `--big-picture` launch argument means
what it says; a settings toggle called "TV mode" is a thing to find, get wrong, and forget you set. I would
ship D1 alone and wait to see whether anyone asks.

### D2 — Rung 1 shows a keyboard key to a player who has no keyboard — *refinement*

The health alert ends in a pill reading **F3**. On a handheld, mid-game, with a controller in both hands, that
is a string naming a key that does not exist on the device.

The app already solves this elsewhere: `InputHintBar` adapts to the current input mode. Rung 1 should read from
the same source — "F3" after keyboard input, the pad glyph or nothing after pad input — and **on touch, the
whole alert should be tappable** and open rung 2.

That last part also improves the answer to the pad-only question below.

### D3 — The pad-only HUD answer gets better for free

`design.md`'s honest answer for a pad-only player is "choose your rung in Settings before you connect." That is
true and it is weak — it asks someone to predict, before a session, whether that session will go wrong. With
D2's tappable alert, a handheld player reaches rung 2 by touching the thing that just told them something is
wrong, which is the obvious gesture and needs no foresight.

The touch-bar collision on the bottom edge is still untested and stays on the test list.

---

## E. First run and pairing

This is where the delight budget lives, and it is the part that is not built. It is also where the
implementation contradicts the settled design most directly.

### E1 — The first thing a new user sees is an absence, drawn with the wrong mark — *unbuilt*

Current empty state: a `FamilyMark` in `TextFillColorTertiaryBrush`, "No consoles yet", a paragraph, an accent
button.

Two problems, and the second is the serious one:

- **"No consoles yet" reports a deficiency** to someone who has done nothing wrong and has been using the app
  for four seconds.
- **`FamilyMark` is the console badge, not the app's mark.** Its own XAML says so: *"The play wedge is left out
  deliberately: the wedge is the **app's** identity."* So the app introduces itself with the half of its logo
  that means "a console vendor", greyed to tertiary.

`design.md` already specifies the fix and nobody has built it: mark, one sentence, one primary action, and the
same-network limit stated before it is met. Per your answer, no wordmark — the full mark carries it, and the
title bar already says the name.

```
      ◤ ▬▬  ▬      (the full mark, hero size, in Ripcord's own colours)

      Play your PlayStation on this PC.

      Ripcord finds your console on your network — keep both on the
      same Wi-Fi or wired connection.

      [ Add your console ]
```

### E2 — Pairing asks the player two questions the app can answer, and offers a console it cannot stream — *reversal*

**Step 1** asks *"Which console are you connecting to?"* with **PS5 / PS4 / Xbox**. `Lanyard` is reserved in
`ConsolePlatform` and not implemented. Offering it in the first thirty seconds of the product is a promise the
next screen breaks, and a new user has no way to know it is a placeholder.

The question is also **answerable**: discovery reports the platform. Lead with the scan and it answers itself.
The family question survives only on the manual-address path, where it genuinely is not knowable.

**Step 3** shows a `RadioButtons` labelled *"How should the console confirm this PC?"*, offering the account
route against the code route. That is **precisely** the question `design.md` says the app must answer on the
player's behalf:

> *"The route is a mechanism, and the player has no stake in it. A casual player does not know what a pairing
> code is; asking them to choose between two mechanisms is the kind of question the app should answer for
> them."*

The settled model is already written: signed in with the console on the account → pair, and state the route as
a fact; not signed in → the code route leads, with sign-in beside it phrased as what it saves them; either way
the other route is one quiet link away.

**What this buys.** First run drops from five steps to three for the common case — **Find → Link → Paired** —
which then matches the mark's three dashes, so the stepper can honestly *be* the trail. Today it is **four
equal dashes in equal columns**: the tidy trio `brand/README.md` explicitly forbids, plus a fourth, sitting in
an app whose other progress indicator is three uneven ones. The two indicators in this product currently
disagree with each other, and one of them disagrees with the brand.

### E3 — Two hard-coded English strings in the page a new user meets first — *refinement*

```xml
<x:String>Through my account — no code needed</x:String>
<x:String>With a code from the console</x:String>
```

`AddConsolePage.xaml`, bypassing the catalogue — the same defect this branch just fixed for the console
flyout, on the app's other most important page. If E2 lands they disappear with the control; if it does not,
they still need moving.

### E4 — The celebration is a stock green checkmark — *unbuilt, and the best-spent frame in the product*

`FontIcon &#xE930;` in `SystemFillColorSuccessBrush`, beside the word "Paired". That is what a form looks like
when it validates.

`design.md` is right about why this moment deserves more than any other: it happens once per console, it is the
only point where the player did something that could have failed, and the reward is the reason they installed
the app. Build it as specified — the mark assembling at hero size, "Paired." with a period, **Done** as a peer
of **Play now** because the record is already on disk.

**One addition. Make "Play now" the wedge, at hero size, not an accent button.** The same language as every
card they will ever press afterwards.

That turns the celebration into the app's only tutorial, and it does not feel like one: the first time you
ever see the wedge, it launches your game — so every card from then on is a promise you have already seen kept.

### E5 — Keep the name box, but do not let it take focus

Naming after pairing is the right call and the code already argues it well. On a pad, make sure the text box
cannot hold focus on entry ahead of **Play now** — a soft keyboard opening on a handheld at the celebration
would undo the whole moment.

---

## F. Settings

### F1 — The doc prescribes a collapsed Advanced and a search box; the page has five flat headers — *unbuilt*

Stated so the divergence is on the record either way.

### F2 — I would drop the search box from the design — *reversal, and it makes F1 cheaper*

Windows Settings has search because it has hundreds of settings across dozens of pages. Ripcord has about
twenty on one scrolling page. A search box over twenty items mostly returns everything, needs an empty state
designed for it, and is itself a *Windows Settings mannerism* rather than a *Fluent behaviour* — which the
position's own line separates deliberately: borrow the behaviour, not the appearance.

**Collapse Advanced, keep it last, skip the box, and amend `design.md`.** The stated reason for the box was to
make collapsing Advanced safe; at this scale, collapsing Advanced is safe because the page is one screen of
scroll with five headings on it.

### F3 — Keep "When you play"

The best-written thing on the page, and exactly the register a consumer app should use. Noting it so nobody
tidies it into "Session".

---

## G. Where the docs and the build disagree

Each of these is cheap and unambiguous. They are listed together because doc drift is its own defect —
`design.md` is the thing the next person will trust.

| `design.md` / `brand/README.md` says | The branch does | |
|---|---|---|
| An unreachable console has no wedge | The wedge is muted | **Doc already amended 2026-09-20. Implementation is right.** |
| First run: mark, one sentence, one action | Grey `FamilyMark`, "No consoles yet" | [E1](#e1--the-first-thing-a-new-user-sees-is-an-absence-drawn-with-the-wrong-mark--unbuilt) |
| The app answers the pairing-route question | A `RadioButtons` asks the user | [E2](#e2--pairing-asks-the-player-two-questions-the-app-can-answer-and-offers-a-console-it-cannot-stream--reversal) |
| Celebration: the mark assembles at hero size | Stock green check-mark | [E4](#e4--the-celebration-is-a-stock-green-checkmark--unbuilt-and-the-best-spent-frame-in-the-product) |
| Settings: Advanced collapsed, plus a search box | Five flat headers, no search | [F1](#f1--the-doc-prescribes-a-collapsed-advanced-and-a-search-box-the-page-has-five-flat-headers--unbuilt) / [F2](#f2--i-would-drop-the-search-box-from-the-design--reversal-and-it-makes-f1-cheaper) |
| The live pad readout moves to the controls page | Still in the rung-3 panel (`ControllerButtonsText`) | Unbuilt; one of the few rung moves the ladder specified |
| Dashes are uneven because a tidy trio is a fast-forward button | `AddConsolePage` draws four equal dashes in equal columns | [E2](#e2--pairing-asks-the-player-two-questions-the-app-can-answer-and-offers-a-console-it-cannot-stream--reversal) |
| There is no Accessibility section, deliberately | `design-branch-test-pass.md` §4 lists one **and then says there is none, two lines later** | Stale; fix the test-pass doc |

---

## H. What I would not change, and why it is worth saying

A review that only lists faults leaves the next person unsure which decisions are load-bearing. These are, and
they should survive any of the above:

- **Borrow behaviour, spend identity where the surface is ours alone.** The clearest statement of that line I
  have read in a project document, and the table that follows it is correctly drawn.
- **Killing the `NavigationView`.** One destination and two commands does not need a rail, and the observation
  that it cost a gamer their first press is exactly the right lens.
- **Hover is a wash, focus is a ring, and focus uses the system visual at 3 px.** Right, right, and right —
  including the reasoning that the system visual is what makes high contrast correct without anyone
  remembering.
- **The facet with a rim, over the accent slab.** The high-contrast failure that prompted it (the accent
  resolving to the text brush and making a third of the card a solid block) is the right reason, and the
  general principle — *identity in material and form, because that is what survives when colour must go* — is
  the best idea in the design.
- **The wedge stays on a console that did not answer.** Removing an affordance on the strength of one lost UDP
  datagram is the worst kind of paternalism, and the amendment is the document working as intended.
- **The ladder, and `F3` toggling rather than deepening.** Unusually disciplined. Do not let anyone add a
  cycling second press.
- **`HudPlacement` claiming dead space.** Genuinely clever, pure, and tested.
- **Deleting `AppScale` / `UiScale`.** Correct, and the argument generalises — see
  [D1](#d1--all-three-form-factors-cost-one-rule-not-three-modes).
- **`StreamHealthAssessor`'s verdicts split device-from-network and carry a real remedy.** Better than the
  commercial clients. The only thing wrong with it is that rung 1 shows the diagnosis and keeps the remedy a
  keypress away — worth considering whether the shortest actionable clause belongs on the alert itself
  ("Wi-Fi is losing packets — a wired connection will help"), which is still one line and still no numbers.

---

## I. If you only do some of it

Ordered by value per hour, not by section.

**Do first — small, and they fix things people will feel:**

1. [B2](#b2--nothing-anywhere-confirms-a-press-on-the-primary-action--refinement) — a pressed state on the card. The primary action of the product currently acknowledges nothing.
2. [C1](#c1--on-a-fast-connect-the-player-reads-three-headlines-in-two-seconds--refinement-highest-value-on-this-surface) — the connect dwell gate. Every session meets it, and the fast path stops strobing.
3. [B1](#b1--the-wedge-responds-to-nothing--refinement-and-the-biggest-single-win-here) — the wedge carries hover and press.
4. [D2](#d2--rung-1-shows-a-keyboard-key-to-a-player-who-has-no-keyboard--refinement) — stop showing "F3" to a controller.
5. [B4](#b4--the-heros-overflow-button-is-below-the-touch-minimum--refinement) + [C5](#c5--two-near-identical-strings-for-different-states--refinement) + [E3](#e3--two-hard-coded-english-strings-in-the-page-a-new-user-meets-first--refinement) + the stale test-pass bullet — minutes each.

**Then — the delight budget, which is the part that is missing rather than wrong:**

6. [E4](#e4--the-celebration-is-a-stock-green-checkmark--unbuilt-and-the-best-spent-frame-in-the-product) — the pairing celebration, with **Play now** as the wedge.
7. [E1](#e1--the-first-thing-a-new-user-sees-is-an-absence-drawn-with-the-wrong-mark--unbuilt) — first run.
8. [C2](#c2--the-trail-is-the-mark-with-the-mark-taken-out--change-and-the-best-delight-per-pixel-in-the-app) — the wedge leads the connect trail.
9. [C4](#c4--spend-the-third-personality-room-on-the-handoff-not-the-wake-wait--reversal-argued) — the first-frame cross-fade; calm the wake wait.

**Then — structural, and they make everything after them cheaper:**

10. [B3](#b3--two-implementations-of-one-card-already-drifting--change) — one card control. Do this **before** the regression baselines, not after.
11. [E2](#e2--pairing-asks-the-player-two-questions-the-app-can-answer-and-offers-a-console-it-cannot-stream--reversal) — pairing leads with the scan and answers the route question. Removes a whole step and an unshippable option.
12. [A1](#a1--ripcord-cannot-be-launched-at-a-console--new-capability) — the launch argument and **Create a shortcut**. The jump list follows for free in the MSIX.
13. [D1](#d1--all-three-form-factors-cost-one-rule-not-three-modes) — density steps.
14. [B5](#b5--the-grid-card-spends-a-third-of-its-width-on-a-mark-that-repeats-what-the-card-already-says--change) / [C3](#c3--there-is-no-visible-way-to-abandon-a-connect--refinement) / [F2](#f2--i-would-drop-the-search-box-from-the-design--reversal-and-it-makes-f1-cheaper) — the rest.

**One sequencing note that matters:** ROADMAP wants regression baselines captured before the card is touched.
Items 1, 3 and 10 all touch the card. Capture the baselines first or accept that they record a card nobody is
keeping.

---

## J. Open questions I could not answer from here

- **Does WinUI 3 actually apply `UISettings.TextScaleFactor`?** `design.md` records this as unverified and then
  builds a position on top of it. The position is right regardless — an app should not compete with the OS on
  text size — but if the platform does *not* honour it, then a user who set text scale in Windows gets nothing
  from Ripcord, and that is a limit the README should state rather than a thing to quietly hope about. One
  hardware check settles it.
- **Does the touch bar collide with rung 2 on the handheld?** Already on the test list; nothing I can add off
  the device.
- **Is the rail legible at 300 px in a black pillar?** The threshold was chosen from a mock-up, by the test
  doc's own admission. Worth checking that a bright panel in peripheral vision against pure black is not worse
  than the overlay it exists to avoid.
