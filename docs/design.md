# Ripcord design

**The question this answers: what does Ripcord look like, and what decides that?**

Settled 2026-09-13. This supersedes the design sections of
[`history/app-reimagining-plan.md`](history/app-reimagining-plan.md), which is marked historical and
describes an earlier state. Most of what that plan established — the 4 px token scale, the elevation model,
the closed motion list, the tone guide — survives untouched and is restated here only where this review
changed it. Where the two disagree, this file wins.

This is not a style guide to apply mechanically. It is the set of decisions a new surface has to be
consistent with, plus the reasoning that makes each one arguable rather than arbitrary.

## The position

**Borrow the OS's behaviour; spend identity where the surface is ours alone.**

Fluent is prescriptive about *behaviour* and nearly silent about *expression*. Departing from the first is
what makes an app feel foreign; the second is where an app becomes itself. So the line is drawn explicitly:

| Stays indistinguishable from Windows | Becomes distinctly Ripcord |
|---|---|
| Settings rows, dialogs, flyouts | The console card |
| Focus visuals, control shapes and radii | First run |
| The title bar and the back model | The connect sequence |
| Keyboard conventions | The pairing celebration |
| The system accent, for interactive state | The stream HUD |

This extends a line the codebase already drew. `Ripcord.Tokens.xaml` forbids overriding
`ControlCornerRadius` — *"a button that is rounder here than everywhere else in Windows reads as a foreign
app"* — while defining its own card, pill, panel and hero radii. That is already borrow-behaviour /
own-expression. This file names it and says where it falls.

**Why the review happened.** The design system was a system of *prohibitions*. Every rule in
`src/Ripcord.App/Styles/` said what must not happen and almost nothing said what Ripcord looks like; the
whole expressive budget was three vendor accents which, by their own comment, are for marks and low-opacity
washes only. An app built that way reads as default WinUI because that is exactly what it is.

## The mark, and the one thing it rules out

**No fourth hue.** The palette's logic is three dashes, one per console family (see
[`../brand/README.md`](../brand/README.md)). A Ripcord colour placed beside them competes with that reading
and makes the mark say something it does not mean.

Identity therefore comes from **material and form**. That is also what survives high contrast, where
decorative colour must disappear entirely and only shape is left — so the constraint and the accessibility
requirement push the same way.

## Form: the wedge

The play wedge is Ripcord's action language. On a console card it is not an icon inside a button — it *is*
the primary action, occupying a slanted zone at the card's trailing edge, which makes "the whole card is one
focus stop" visually true rather than a fact you have to already know.

Three rules keep it from becoming decoration:

- **A wedge means "this launches something."** The card's play action, the primary action of first run and
  of the pairing celebration. Never something that merely navigates.
- **Absent, not disabled.** A console that cannot be reached has *no wedge at all*. The affordance is
  missing rather than greyed, matching the standing rule that an offline console is a normal state and not
  a fault.
- **It scales with its container, not as a fixed chip.** 92 px beside a 292×188 grid card, 184 px beside the
  one-console hero, 138 px at 150 % text scale.

The diagonal is the loudest thing in the app. It stays on Ripcord's own surfaces and never reaches a
settings row.

## Material: the gradient rule

Cards carry a directional gradient, a hairline stroke and an inner top highlight — the treatment the app
tile has always used and the app itself never did.

> **The gradient's direction and its ratio are Ripcord's; its lightness belongs to the theme.**

In dark theme the card runs deeper than the Mica ground (`#23272E → #12151A`, the tile's own values). In
light theme it runs *lighter* than the ground, so it reads as raised paper rather than a hole cut in the
page. A dark card in light theme was considered and rejected: it defies a preference the user stated to the
operating system, which is not something an app gets to overrule for style.

This is also what finally makes the L2-never-shadowed rule work as written — separation comes from fill,
which is how Windows 11 separates cards, rather than from a shadow the rule forbids.

## Connect: no ceremony

The first attempt was centred, full-bleed, with enumerated phases and a reassuring time estimate. It read as
Windows Setup, and that was a fault in the concept rather than the styling: **setup can afford ceremony
because you do it once, and connecting happens several times an evening.** A ceremony performed daily is a
chore.

- **Nothing is centred, because the centre belongs to the game.** The layout reserves the space the picture
  will occupy.
- The console identity persists where the card's own mark sat — the `ConnectedAnimation` lands it there — so
  connect is the card becoming the window, not a new place navigated to.
- One line of status, low and left, over a 3 px trail. The phase checklist is gone: it was a rung-3 fact
  sitting at rung 1.
- "Usually about ten seconds" is reserved for waits that are genuinely long (the account route's 10–40 s
  rendezvous) and appears only once seconds have already passed. Said every time, it teaches the player to
  expect a wait.

**Reconnect and failure reuse this composition exactly**, so there is no second place to look when things go
wrong. Verdict first, then what to do, then the buttons. The mark *dims* on failure rather than turning red,
because a console entering rest mode is behaving normally. The reconnect escape reads "Stop trying", not
"Cancel", which during a countdown is ambiguous about whether it abandons the session or just this attempt.

## The HUD: the ladder, implemented

The three-rung disclosure ladder was the project's stated thesis, and the diagnostics panel did not
implement one — eight groups at uniform caption weight in a scrolling column, all of it rung 3. It becomes
three states of one control:

| Rung | Question | Entry |
|---|---|---|
| 1 | Is anything wrong? | Automatic and self-clearing, with hysteresis. One line, no numbers. |
| 2 | Is it me or the network? | `F3`. Verdict, four numbers, capability pills. |
| 3 | What exactly is happening? | The **Details** control on the strip. Every field the old panel held. |

**`F3` shows and hides; it never reveals more.** Toggle is the learned convention for a debug overlay, so a
cycling second press does the opposite of what the muscle expects — and covers *more* of the game at the
moment someone was trying to uncover it. Going deeper is a visible control instead, which is also what makes
rung 3 discoverable at all; a hidden second keypress is not an affordance. The HUD returns to whichever rung
it was last at, and `ShowDiagnosticsOverlay` becomes three-way (off / summary / full) to set that default.
That is also the honest answer for a pad-only player: nothing in the stream layer takes gamepad input, by
design, so the setting chosen before connecting is their control over the HUD.

### Rung 3 goes where the video isn't

The stream is 16:9 and the window usually is not, so there is nearly always a dead bar — already black,
already carrying nothing. **Rung 3 claims dead space first and overlays only when there is none.**

- Wider than 16:9 → a **rail** in the pillarbox (needs ~300 px; 3440×1440 leaves 440 per side, 2560×1080
  leaves 320). Nothing is covered, so it can stay up indefinitely.
- Taller than 16:9 → the **sheet** in the letterbox bar beneath the picture.
- Exactly 16:9 → the sheet **overlays**. The only case that does.

One set of facts in two arrangements, not two panels: the same groups reflow from a column into a row. The
letterbox arithmetic is already being done — the swap chain knows the video size and the panel knows its
bounds. Placement settles on resize-end, not during a drag, so the panel cannot change shape under the
user's hand.

### Colour grammar for instruments

The sparkline strokes were fixed per metric — frames always `SystemFillColorSuccessBrush`, loss always
Critical — so the loss chart was red at zero loss and the frames chart green while frames collapsed. Colour
implied meaning while carrying none, in a project whose standing rule is that colour is never the sole
carrier of it.

- **Neutral inside the threshold, semantic once crossed.** A value wears its severity and wears nothing when
  there is none. This applies to the numbers at rung 2 as well as the lines at rung 3.
- **Bitrate never colours.** It is a magnitude, not a verdict.
- **The threshold is drawn**, as a dashed rule at the warn level, so "is this bad?" is answerable without
  knowing what the number ought to be. For loss and latency that rule sits along the *ceiling*, because full
  scale already is the warn threshold; drawing it makes "touching the top means trouble" explicit rather
  than folklore. Frames is the exception — it is scaled with headroom so a stream at target is not drawn
  clipped, which puts its rule partway down.
- **Loss keeps its warn-threshold scale. Amended 2026-09-13, against this document's first draft**, which
  said to rescale to `LossBadRatio` (10 %) so that 2.1 % and 30 % would stop drawing identically. That
  reasoning does not survive contact with the code: `LossFullScalePercent` carries a comment recording that
  the scale *was* 20 %, and that a real 0.4 % blip then drew at 2 % of row height — invisible, so the row
  could not tell "no loss" from "a little loss", which is the distinction that matters most on wireless.
  At 10 % that blip is about one pixel, so the proposed fix reintroduces the bug that comment describes.
  And the case for it was weak: **a player's action is identical at 3 % loss and at 30 %** — fix the network
  — while the value and peak are printed beside the line anyway, so magnitude is never actually lost. The
  severity colour carries "past the threshold"; the scale keeps the detail below it.

Fixed scales stay fixed and numbers still never animate, both for the reasons already recorded.

**One thing moved rung:** the live pad readout (buttons, sticks, triggers at 500 ms) is a *controller*
diagnostic, not a *stream* one. It belongs on the controls page beside the bindings it helps check.

## Home

- **Nothing paired** → the first-run surface: mark, wordmark, one sentence, one primary action, and the
  same-network limit stated before it is met. Because it is not an error it is not an `InfoBar`.
- **One console** → a hero card whose wedge holds focus on load. Open the window, press A, playing. The two
  secondary actions sit below as text, never as competing buttons.
- **Two or more** → the card grid, ghost tile last.

The card's rename / details / remove flyout moves from imperative code into markup and the string
catalogue. It is currently built in `ConsolesPage.xaml.cs` with around fifteen hard-coded English strings
that bypass the catalogue entirely, which is why the app's most important page is the one page that can
neither be localised nor tested.

## Pairing

**The route is a mechanism, and the player has no stake in it.** A casual player does not know what a
pairing code is; asking them to choose between two mechanisms is the kind of question the app should answer
for them. The decision they *do* have a stake in is whether to connect a PlayStation Network account at all
— a first-run question, asked once per account and never per console, consistent with the standing rule for
the account id.

- Signed in, console on the account → pair. No question; the route is stated as a fact.
- Not signed in → the code route leads, since it needs nothing the player does not already have. Sign-in
  sits beside it, phrased as what it saves them.
- Either way the other route is one quiet link away — never weighted, never explained unless asked.

**The app does not reassure anyone about account safety.** We cannot back a claim in either direction, and a
line saying otherwise would be the app vouching for something outside its control. Both routes exist without
editorialising; account-tier caveats belong in `README.md`'s limits section, where they can be precise.

**The celebration.** Personality has three rooms — first run, a successful pair, and the wake wait. Pairing
earns the largest because it happens once per console, it is the only moment the player did something that
could have failed, and the reward is why they installed the app. The full mark assembles at hero size: the
wedge, then the trail. "Paired." with a period, never "Success!". The record is already on disk by the time
this screen appears, so **Done** is a peer of **Play now** rather than a save button.

## Settings

Visually nothing changes — a settings row must be indistinguishable from Windows, so no wedge, no gradient,
no Ripcord colour. The work is **order**, and the current order has a specific fault: **Account is first**,
and a LAN-only player never signs in.

| Section | Holds |
|---|---|
| Picture | Resolution and frame rate, max bitrate, adapt automatically, image scaling, HDR |
| Controls | Keyboard input, key and pad bindings, exit gesture, menu stick sensitivity |
| When you play | Full screen on connect, ask before disconnecting, rest on disconnect, diagnostics (off / summary / full) |
| Accessibility | Larger text and controls |
| Account | PSN sign-in, consoles on this account |
| Advanced | GPU preference, specific adapter, video codec, connection reporting, credential protection |

Every knob survives; they stop being peers. The dials an enthusiast expects move into a collapsed
**Advanced** so a casual player never scrolls past them. Accessibility gets its own heading, because burying
an accessibility control under "Display" is how it goes unfound. A **search box** is what makes collapsing
Advanced safe at roughly twenty settings.

Each row's description says what it does *for you* rather than what it sets: "4 of 4 checks passed on this
display" is the HDR readiness verdict promoted to the row, leaving the expander for people who want the four
lines.

## Accessibility renditions

**High contrast is the test this direction was chosen to pass.** The gradient collapses to a system fill,
the vendor blue goes to white because decorative colour is not permitted outside the system palette, and
what is left still reads as Ripcord — because the identity was never in the colour. The family mark survives
as shape, and the plain-text "PS5"/"PS4" label does the work colour was doing, which is why that label is
never optional.

**At 150 % the card grows with the text** — 438×282 rather than 292×188 — and the wedge grows with the card.
`ItemsWrapGrid` requires a fixed item size, so the cell must be bound to the same factor `AppScale`
computes. Two fixed numbers that have to move together is exactly the arrangement that silently stops moving
together, so it wants a test rather than a comment.

Both of these are **drawings of what should happen, not records of what does** — see the open items in
[`../ROADMAP.md`](../ROADMAP.md).

## Deliberately not redesigned

`AboutPage` and `KeyBindingsPage` are utility surfaces the position says should stay stock Windows, and
neither has an open design question. They inherit the token changes and nothing else. The account sign-in
dialog hosts a web view whose contents are not ours.
