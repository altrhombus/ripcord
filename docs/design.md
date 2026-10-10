# Ripcord design

**The question this answers: what does Ripcord look like, and what decides that?**

Settled 2026-09-13. This supersedes the design sections of
[`history/app-reimagining-plan.md`](history/app-reimagining-plan.md), which is marked historical and
describes an earlier state. Most of what that plan established — the 4 px token scale, the elevation model,
the closed motion list, the tone guide — survives untouched and is restated here only where this review
changed it. Where the two disagree, this file wins.

Amended 2026-10-09 with what the showcase work decided ([`showcase-plan.md`](showcase-plan.md)): the page
frame, Home in it, first run, motion, and the pad.

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
- **Quiet, not absent — amended 2026-09-20.** This rule originally read *"absent, not disabled: a console
  that cannot be reached has no wedge at all."* It was written when "cannot be reached" was taken to be
  knowledge. It is not. A reachability probe is a single UDP datagram, and on 2026-09-19 a console that was
  powered on for an entire session was reported unreachable because that one datagram went missing; the fix
  for that was to stop letting silence remove the ability to try. Removing the wedge would rebuild the same
  dead end one layer up, in paint, where no amount of retrying reaches it.

  So an unreachable console keeps its wedge and the wedge goes neutral — it loses the family accent and
  nothing else. The original *reasoning* is what survives, and it is the part worth keeping: an offline
  console is a normal state and not a fault, so there is no red, no error glyph, and nothing that reads as
  broken. What changed is the conclusion drawn from it, because the premise turned out to be false.
- **It scales with its container, not as a fixed chip.** It is 35% of the card at every size, so the
  diagonal's angle never changes: 92 px beside a 292×188 grid card, and on the hero, which follows the window,
  whatever 35% of the hero is.

The diagonal is the loudest thing in the app. It stays on Ripcord's own surfaces and never reaches a
settings row.

**The wedge as a button.** `WedgeButton` is the card's material and wedge in a button, for the two launches
that aren't a card: first run's "Get started" and the celebration's "Play now". Focus is the system ring and
pressed is the card's press, as on the card. Everything else that's a primary action is the stock accent
button, so a player learns the two meanings once.

### Hover and focus are different kinds of mark

Added 2026-09-20, after going to photograph the card's states and finding there were none worth
photographing. `GotFocus` and `PointerEntered` both set one `IsHighlighted` flag driving one accent wash,
moving it from 0.10 to 0.22 opacity on a gradient that fades out at 90%. Hover and keyboard focus were
therefore the same picture, that picture was a tint delta invisible on a real screen, and in high contrast
the wash was suppressed to zero so neither state existed at all.

The rule that replaces it:

> **Hover is a wash. Focus is a ring. They are different kinds of mark, never two strengths of one.**

Focus uses the *system* focus visual rather than anything drawn in the template, which is what makes it
correct in high contrast without anyone having to remember — and it is set heavier than stock, 3px against
WinUI's 2px, because this project already holds itself to *"focus is legible at 2 m"* and the default weight
was drawn for a mouse at desk distance. It is set on the `GridViewItem` container, not the card inside it,
because the container is what a `GridView` actually focuses; a pad never focuses the template's own root.

## Material: the gradient rule

Cards carry a directional gradient, a hairline stroke and an inner top highlight — the treatment the app
tile has always used and the app itself never did.

> **The gradient's direction and its ratio are Ripcord's; its lightness belongs to the theme.**

In dark theme the card runs deeper than the Mica ground (`#23272E → #12151A`, the tile's values until it was lifted on 2026-09-24; the card stays deeper so the wedge facet, which is the card lifted, keeps its contrast). In
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
- **The trail tells the phase and breathes while it waits.** Waking lights the first dash, connecting the
  second, the handshake the third. The current dash pulses slowly so a long wait looks alive; with animation
  effects off it's lit and still. The headline keeps one style in every state and is dimmed only on failure.
- **One headline per phase, and no time promised.** "Waking your console…" stays put. If the wait runs long,
  one line is added, once: a console in rest mode can take a little longer. No figure. Amended 2026-10-09: this
  bullet reserved "Usually about ten seconds" for long waits, but a wake over the internet has taken more than
  forty, and a number the wait overruns is a broken promise (owner).

**Reconnect and failure reuse this composition exactly**, so there is no second place to look when things go
wrong. Reconnect draws it over the frozen last frame, on an acrylic plate. Verdict first, then what to do, then the buttons. The mark *dims* on failure rather than turning red,
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
- **The player can move it.** Dragged by its header and resized from any edge, it stays wholly inside the
  window and is where it was put next stream, kept as a fraction of the room beside it so a corner stays a
  corner when the window changes size. Wide and short, it lays its groups out as the sheet does. "Put back", or
  a double-click on the header, returns it to the rule above. Pointer only: nothing on the stream layer takes
  the pad. Settled 2026-10-09, after a draft that raised the sheet out of a 16:10 display's 60 px bar covered
  too much picture (owner); a bar that thin keeps the overlay on the left.

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

## The frame

Settled 2026-10-09. Every page sits in one frame, so flipping between them moves nothing but the content.

| Part | Rule |
|---|---|
| Window | A minimum of 640 × 480 effective pixels. Below it the hero's play mark was cut off, and 640 is where the grid drops to one column. |
| Column | Centred, `MaxWidth` 820 (`RipcordPageColumnWidth`), on every page, Home included. |
| Title | One style, one top offset, the column's left edge. |
| Footer actions | Inside the column, primary at the trailing end, as Windows 11's own setup does. A step-back button only when there's a previous step; leaving a flow is the title bar's Back, so no page shows two. |
| Page transitions | The platform's: entrance for a new page, drill-in for a detail. |

**Focus is placed on arrival on every page**, on the thing the page is for, and it survives the page or step
it replaces collapsing: Windows moves focus out of a collapsing control after any queued focus call, so it is
set at once and checked again after the next layout.

**The title bar is the stock one.** At 200% scale it leaves too wide a gap before the window buttons. That is a
known WinUI bug ([microsoft-ui-xaml#10344](https://github.com/microsoft/microsoft-ui-xaml/issues/10344): the
caption-button inset is read in physical pixels as effective ones), so it stays until a Windows App SDK release
fixes it. A workaround now would collide with the window buttons the day it does.

## Home

- **Nothing paired** → mark, one sentence, one primary action, and the same-network limit stated before it is
  met. No wordmark: this is a task screen, and Welcome and About are where the wordmark lives (2026-09-24,
  asked again 2026-10-09). Because it is not an error it is not an `InfoBar`. At large text in a small window it
  scrolls rather than clip, and stays centred when it fits.
- **One console** → a hero card whose wedge holds focus on load. Open the window, press A, playing. The two
  secondary actions sit below as text, never as competing buttons.
- **Two or more** → the card grid, ghost tile last.

**Home sits in the page column** (decision E, 2026-10-09). Its title was at a different place for one, three
and no consoles, and never where the other pages put theirs. The grid fills the column from its left edge, two
cards a row; the empty state is centred in it.

**The hero follows the window.** Half the page, held between 440 and 880 px and never past the column, at the
card's 2.95 : 1 plus the text-size allowance. A fixed card was a strip in a large window and clipped in a small
one. Its text steps up the platform's type ramp rather than scaling: the name is Title below 720 px of card and
Title Large above, and the status line is Body rather than Caption (decision A). A card twice as wide with the
same small text reads as empty, and stepping the ramp at breakpoints is how Fluent pages respond to room; it is
not a second text-size setting.

**What a card says:** the name, the family ("PS5" with its mark), the status and when it was last played. **No
address**: it's noise to a player and it's in every screenshot anyone posts; Details keeps it. The "…" sits in
the card's top-trailing corner, and hover is a wash that can be seen, fading over 150 ms.

The card's rename / details / remove flyout takes its strings from the catalogue (`ConsoleCardCopy`). It is
still built in `ConsolesPage.xaml.cs` rather than markup.

## First run

Settled 2026-10-09. **Welcome is the identity room**, and the one introduction:

- Centred. The mark assembles (the wedge, then the three dashes in turn), then the wordmark beside it, then one
  sentence (decision B).
- One primary, `WedgeButton` "Get started", focused on arrival; "Skip setup" beside it as a quiet text button.
- The step dashes appear once you're past Welcome: Welcome is the door, not step zero.

**The setup steps don't make anyone wait or press twice.**

- Picking a picture mode moves on to the next step, so a pad press is the whole step.
- Checking what the PC can decode happens while the choices are on screen, and they stay live. Pressing on
  before the check is done goes on when it finishes. If the check takes away the choice made, it stays on the
  step with the other one picked.
- The last step lists what was set as three settings rows, not a paragraph.

## Pairing

**The route is a mechanism, and the player has no stake in it.** A casual player does not know what a
pairing code is; asking them to choose between two mechanisms is the kind of question the app should answer
for them. The decision they *do* have a stake in is whether to connect a PlayStation Network account at all
— a first-run question, asked once per account and never per console, consistent with the standing rule for
the account id.

- Signed in, console on the account → pair. No question; the route is stated as a fact.
- Not signed in → **sign-in leads**, and the code form waits until it is asked for. Amended 2026-09-22; see
  below.
- Either way the other route is one press away — never weighted, never explained unless asked.

> **Amended 2026-09-22: sign-in leads, not the code route.** This section had the code route leading "since
> it needs nothing the player does not already have". That is false, and it was false when it was written.
> The code route needs them at the console, through its menus, reading an 8-digit code, **and holding their
> numeric account id** — which almost nobody knows, which this app's own field caption sends them to a
> third-party lookup tool to find, and which the vendor's own client never asks for at all. Signing in needs
> a password they already have.
>
> So the low-friction route is the account one, and the app should default to it. The code form is not drawn
> until somebody asks for it, and asking is one unweighted press — because pairing locally without
> connecting an account is a legitimate choice, and somebody making it is entitled to do so without being
> argued with.
>
> The account question was also being asked **once per console**, which this section already forbids ("asked
> once per account and never per console"). Leading with it does not fix that on its own: signing in once
> means every later console pairs with no code and no id, so the question stops arising rather than being
> suppressed.

**The app does not reassure anyone about account safety.** We cannot back a claim in either direction, and a
line saying otherwise would be the app vouching for something outside its control. Both routes exist without
editorialising; account-tier caveats belong in `README.md`'s limits section, where they can be precise.

**The celebration.** Personality has three rooms — first run, a successful pair, and the wake wait. Pairing
earns the largest because it happens once per console, it is the only moment the player did something that
could have failed, and the reward is why they installed the app. The full mark assembles at hero size: the
wedge, then the trail. "Paired." with a period, never "Success!". The record is already on disk by the time
this screen appears, so **Done** is a peer of **Play now** rather than a save button. "Play now" is a
`WedgeButton` and has focus.

## Settings

Visually nothing changes — a settings row must be indistinguishable from Windows, so no wedge, no gradient,
no Ripcord colour. The work is **order**, and the current order has a specific fault: **Account is first**,
and a LAN-only player never signs in.

| Section | Holds |
|---|---|
| Picture | Resolution and frame rate, max bitrate, adapt automatically, image scaling, **video codec**, HDR |
| Controls | Keyboard input, key and pad bindings, exit gesture, menu stick sensitivity |
| When you play | Full screen on connect, ask before disconnecting, rest on disconnect, diagnostics (off / summary / full) |
| Account | PSN sign-in, consoles on this account |
| Advanced | GPU preference, specific adapter, connection reporting, credential protection |

**Codec stays with Picture, against this table’s first draft.** It was listed under Advanced as a
technician’s dial, which it is — but HDR is *gated* on HEVC, and the toggle is disabled until the codec
allows it. Filing the two in different sections would leave a casual player looking at a dead HDR switch
whose reason sits behind a collapsed heading somewhere else. A control that explains why another control
is unavailable belongs beside it.

Every knob survives; they stop being peers. The dials an enthusiast expects move into a collapsed
**Advanced** so a casual player never scrolls past them.

> **Amended 2026-09-21: no search box.** This section prescribed one, on the reasoning that search is what
> makes collapsing Advanced safe. At roughly twenty settings on a single scrolling page it is not needed —
> and it is a *Windows Settings mannerism* rather than a Fluent behaviour, which is precisely the line
> [the position](#the-position) draws between what this app borrows and what it invents. It would also be
> the only control on the page that has to be taught what every setting is called, which is a maintenance
> cost paid forever against a problem twenty rows do not have.

There is no Accessibility section, and that is the point rather than an omission — see below.

Each row's description says what it does *for you* rather than what it sets: "4 of 4 checks passed on this
display" is the HDR readiness verdict promoted to the row, leaving the expander for people who want the four
lines.

## Motion

Settled 2026-10-09. **Short, never between the player and the press, and gone when Windows' animation effects
are off.** Every animation takes its timing from the tokens in `Ripcord.Motion.xaml`, and every one started from
code checks `AppMotion.Enabled` when it runs; the platform's theme transitions check the setting themselves.
Each one is garnish, never the mechanism: with motion off the screen is the same, arrived at at once.

| Where | What | Timing |
|---|---|---|
| Pages | The platform's entrance and drill-in | the system's |
| Card hover | The wash fades in and out | `RipcordDurationStateChange` |
| Card press | Settles inwards, instantly: felt, not watched | none |
| Connect | The mark travels from the card to the connect screen's mark | the system's (`ConnectedAnimation`) |
| Trail | The current dash breathes | `RipcordDurationBreath`, twice a cycle |
| Welcome, Paired | The mark assembles, then the wordmark fades in | `RipcordDurationStateChange` a part, staggered by half |
| HUD rungs | Fade in and out, by the compositor | `RipcordDurationStateChange` |
| Status over the stream | Fades out on the first frame | `RipcordDurationStateChange` |

**Over the stream, opacity only:** nothing moves across the picture.

## Pad and keyboard

Settled 2026-10-09. A and B do the same thing on every screen, and the screen says what A will do.

- **The hint bar names the verb.** A console card says its own action ("Play", "Wake & play"), a switch or
  checkbox "Toggle", a text field "Type", a drop-down "Open", anything else "Select". Only the verb changes,
  so the bar keeps its shape as focus moves.
- **Moving with a pad clicks.** Windows' own navigation sounds, on while a pad is in use and off for the mouse
  and keyboard, as the Xbox shell does (decision D, kept by ear). A switch under Controls turns them off.
- **Left and Right stay in the row** when there's somewhere in it to go, rather than jumping to whatever is
  nearest, such as the title bar's gear.
- **Keyboard:** Ctrl+, opens Settings, F11 toggles full screen, Esc steps back a level, and Tab follows the
  reading order.

## Accessibility renditions

**High contrast is the test this direction was chosen to pass.** The gradient collapses to a system fill,
the vendor blue goes to white because decorative colour is not permitted outside the system palette, and
what is left still reads as Ripcord — because the identity was never in the colour. The family mark survives
as shape, and the plain-text "PS5"/"PS4" label does the work colour was doing, which is why that label is
never optional.

**Text size belongs to Windows, and Ripcord does not compete with it.** The app carried its own "larger text
and controls" switch and an `AppScale` that rewrote the size tokens at startup; both are gone, along with the
`UiScale` policy that composed them with the OS factor. Settled 2026-09-19.

The reason is a position rather than a cleanup: **an app should be a good citizen of the desktop it runs on,**
and a second scale control competing with the system one is the opposite of that. It also asked the user to
solve in Ripcord a problem they had already solved in Windows, and it is the kind of feature that looks like
care and behaves like a second setting to get wrong.

Two things fall out, and both are improvements. The unverified premise underneath it — whether WinUI applies
`UISettings.TextScaleFactor` itself, where being wrong meant rendering everything at 225% — stops mattering,
because nothing multiplies anything now. And the console card's fixed `ItemsWrapGrid` cell height stops being
a blocker for the card redesign: the cell only had to grow because the app was growing the text inside it.

**What Ripcord still owes the user's environment:** theme, accent, high contrast, transparency, reduced
motion, and whatever the platform does with text scale. The one place it is deliberately more than a good
citizen is **controller input**, because nothing in the desktop environment does that for it.

**Checked on screen, 2026-10-09** (the showcase review, kept in the captures folder), for every screen except
a stream: High Contrast (Night sky), 200% scale, 225% text and animation effects off. What it
settled:

- **Under a contrast theme the mark is shape alone.** The dashes take the wedge's colour, as the app icon goes
  one-colour, because the three accents are brand colours with no contrast variant.
- **The wedge button's label has no backplate.** Windows draws one behind text in a contrast theme, and on the
  wedge it put a black box inside the button. Stock accent buttons keep theirs when focused: that is Windows',
  and stays (owner).
- **A settings row's header is the theme's button-text colour** (yellow in Night sky),
  because the row is a button. That is
  Windows', and stays.
- **At 225% text in the smallest window, a row that doesn't fit stacks** (`StackWhenTight`) rather than cutting
  its last item off mid-word.

The stream screens in High Contrast are still a drawing of what should happen, not a record of what does — see
[`../ROADMAP.md`](../ROADMAP.md).

## Deliberately not redesigned

`AboutPage` and `KeyBindingsPage` are utility surfaces the position says should stay stock Windows, and
neither has an open design question. They inherit the token changes and the page frame. About carries the
wordmark as its title, being the page about Ripcord itself; Keyboard controls groups its keys under headings in
settings rows. The account sign-in
dialog hosts a web view whose contents are not ours.
