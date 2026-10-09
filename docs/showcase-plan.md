# The showcase plan

**The question this answers: what does each of Ripcord's own screens look like when it's finished, and how
will we know?**

Written 2026-10-09, after the visual audit of 2026-10-08 and the fixes it led to. [`design.md`](design.md)
holds the rules; this file turns them into screens, sizes and checks. When a part of this lands, what it
decided moves into `design.md` and the part here is marked done.

## The brief

A premier Windows 11 Fluent interface, and an experience that's intuitive, consistent and delightful, that
feels at home to a Windows 11 user and to an Xbox or PlayStation player, with a keyboard or a pad.

Ripcord is a stop on the way to a game. Most visits are a few seconds long. So:

- **The fast path is the product.** Launch, one press, playing. Every screen answers "what do I press?"
  before anything else: focus is already on that thing, and with a pad the hint bar names the button.
- **At home in two places.** Windows' behaviour everywhere (Mica, stock controls, the title bar and back
  model, keyboard conventions), and a console's legibility where the screen is ours: big, focusable targets,
  A to go and B to go back on every screen, readable from the couch.
- **Consistent.** One page frame, one way to say "this launches something" (the wedge), one way to say
  everything else (the stock accent button). A player learns the app once.
- **Delight in small doses.** Short motion that never stands between the player and the press, and is gone
  entirely when Windows' animation effects are off. Personality in the three rooms `design.md` names (first
  run, a successful pair, the wake wait) and nowhere else.

## The order

1. [The frame](#1-the-frame): window minimum and one page geometry.
2. [Home](#2-home): a hero that scales with the window.
3. [Wake, connect and reconnect](#3-wake-connect-and-reconnect): one composition.
4. [First run and the pairing celebration](#4-first-run-and-the-pairing-celebration), with `WedgeButton`.
5. [Motion](#5-motion): the tokens put to work.
6. [Pad and keyboard polish](#6-pad-and-keyboard-polish).
7. [Leftovers](#7-leftovers).
8. [The showcase review](#8-the-showcase-review): the acceptance pass.

Each part is its own commit series on `design/showcase`, checked against its "done when" before the next
starts.

## 1. The frame

**Window.** A minimum size of 640 × 480 effective pixels, through the presenter's preferred minimum. Below
that the hero's play mark was cut off, the one thing the page exists for (audit item 3). 640 is also where the
grid drops to one column, so the two rules meet.

**One page geometry.** Every chrome page uses the same frame:

| Part | Rule |
|---|---|
| Column | Centred, `MaxWidth` 820 for every page. Settings, About and Keyboard controls already are; Add a console and Setup hug the left edge at 720 and move. |
| Title | Same style, same top offset, same left edge as the column. |
| Footer actions | Inside the column, not pinned to the window's corner. Primary at the trailing end, the way Windows 11's own setup does it. A step-back button only when there's a previous step; leaving the flow is the title bar's back, so Add a console stops showing two Backs. |
| Page transitions | The platform's: entrance for a new page, drill-in for a detail. |

**Done when:** every page's title and column line up when you flip between them at 100% and 200%; nothing is
cut off at 640 × 480; Add a console shows one Back.

## 2. Home

**The hero (one console).** Today it's a fixed 520 × 176 card at every window size: a small strip in a large
window and clipped in a small one.

- **Width** is half the page, held between 440 and 880. **Height** keeps the card's proportion (about 2.9 : 1),
  plus the text-size allowance `CardMetrics` already adds. The wedge stays 35% of the card at every size, so the
  diagonal's angle never changes.
- **Text steps up the platform's type ramp, not by scaling.** The name is Title below 720 px of card and Title
  Large above it; the status line is Body rather than Caption on the big card. A card twice as wide with the
  same small text reads as empty, and stepping the ramp at breakpoints is how Fluent pages respond to space.
  (Decision A.)
- **Placement:** centred, sitting a little above the middle of the space under the title, with "Add another
  console" 16 px beneath the card rather than at the window's bottom edge.
- **What the card says:** the name, the family ("PS5" with its mark, drawn large enough to read on the hero),
  the status and when it was last played. **No IP address**: it's noise to a player and it's in every
  screenshot anyone posts. Details keeps it.
- **The "…" button** goes in the card's top-trailing corner, not floating at the wedge's edge.
- **Hover** has to be visible. The dark wash is `#16FFFFFF`, which the audit measured as identical to rest. It
  goes up until it reads, and fades in over 150 ms.

**The grid (two or more)** keeps its structure and densities; it gains the same card changes (no IP, the
"…" in the corner, a visible hover).

**Words:** "Rest mode" on the card and "In rest mode" in pairing become one phrase; "1080p60", "1080p 60 fps"
and "1080p at 60 fps" become one form.

**Done when:** at 640 × 480, 1280 × 800, 1920 × 1080 and 3440 × 1440 the hero is whole, proportioned and
centred; at 225% text nothing is cut; with a console paired, launch → playing is one press (A or Enter).

## 3. Wake, connect and reconnect

Today the wake wait and the connect are two different compositions (audit C2): a dim headline that reads as
disabled, a still mark, and a headline that changes to say nothing new; then a different layout for connect.

**One composition for all of them**, as `design.md` says:

- The console's identity at top-left (mark and name), where the card's mark lands.
- Low-left: one headline, one detail line, the trail. The headline uses the same style in every state, and is
  never dimmed unless it's failed.
- **The trail tells the phase and breathes while it waits.** Waking: the first dash. Connecting: the second.
  The handshake: the third. The current dash breathes (a slow opacity pulse, about 1.2 s) so a long wait looks
  alive. With animation effects off it's lit and still.
- **One headline per phase.** "Waking your console…" stays put; it doesn't become "Waiting for the console to
  wake…" after seven seconds. If the wait runs long, one line is added, once: rest mode can take about twenty
  seconds.
- **Reconnect** is the same screen over the frozen frame, on the acrylic plate (done 2026-10-09).
- **Failure:** the mark dims, verdict first, then what to do, then the buttons. No red.
- **The way out:** B, Esc, or the link. It reads "Back to consoles" while connecting. While reconnecting,
  `design.md` says "Stop trying" and the app says "Back to consoles". (Decision C: "Stop trying".)

**Done when:** a wake from rest, a connect, a Wi-Fi drop and a failure are screenshots of one layout; the wait
visibly moves; reduced motion leaves it still and readable.

## 4. First run and the pairing celebration

**`WedgeButton`**, a new control: the card's material and its wedge, as a button. Used only where `design.md`
allows a wedge (a launch): first run's primary action and the celebration's "Play now". Focus is the system
ring, like the card; pressed is the card's press. In High Contrast it's a system button with the play mark.

**Welcome** (Setup's first step) is the barest screen in the app today: a title, an 88 px mark at the top-left,
a paragraph, and stock buttons at the window's corner. It becomes the identity room:

- Centred. **The mark assembles** (the wedge, then the three dashes in turn, about 600 ms all told), then the
  wordmark beside it, then one sentence. (Decision B.)
- One primary, `WedgeButton` "Get started", focused on arrival. "Skip setup" beside it as a quiet text button.
- The step dashes appear once you're past Welcome, not on it: Welcome is the door, not step zero.

**The other setup steps and Add a console** keep their content and take the frame from part 1.

**Paired.** The mark assembles at hero size, then "Paired." (with the period). The name field stays a
flourish. "Play now" is a `WedgeButton` and focused; "Done" is its peer, not a save button, because the
console is already on disk.

**The empty home** (setup skipped, nothing paired) borrows Welcome's composition without the assembly: mark,
one sentence, one primary action. That's a task screen, so it stays without the wordmark, as decided on
2026-09-24.

**Done when:** first run, pad only, from launch to "Paired." to playing, never needs a pointer and never
leaves you guessing what A does; the assembly is skipped under reduced motion; High Contrast shows the mark
as shape alone.

## 5. Motion

`Ripcord.Motion.xaml` defines three durations (83, 150 and 250 ms) and two easings, and the review of
2026-10-05 found nothing using them. Every animation in the app takes its timing from these, and every one is
gated on `AppMotion.Enabled`.

| Where | What | Token |
|---|---|---|
| Pages | The platform's entrance and drill-in | (system) |
| Card hover | Wash fades in and out | 150 ms |
| Card press | Settles inwards (exists) | 83 ms |
| Connect | The mark travels from the card (exists, now mark to mark) | 250 ms |
| Trail | The current dash breathes | 1.2 s cycle |
| Welcome, Paired | The mark assembles | 3 × 150 ms, staggered |
| HUD rungs | Fade between rungs | 150 ms |
| Status over the stream | Fades out on the first frame (exists) | 150 ms |

**The rule over the stream stays:** opacity only, nothing that moves across the picture.

**Done when:** no hard-coded duration is left in the app's XAML or code-behind; with animation effects off,
every screen works and nothing moves.

## 6. Pad and keyboard polish

- **The hint bar says what A does here.** Today it's always "A Select · B Back · Y Options". On a console card
  it should be "A Play" (or "A Wake & play"); on a toggle, "A Toggle"; on a text field, "A Type". Only the verb
  changes; the bar's shape doesn't.
- **The first press is never lost.** Focus is placed on arrival on every page (mostly done); the remaining
  case is a pad press right after the window regains focus.
- **Sounds, maybe.** WinUI can play the system's navigation sounds (`ElementSoundPlayer`), which is what makes
  the Xbox shell feel like a console. On while a pad is in use and off for the mouse is the console
  convention. (Decision D: try it.)
- **Keyboard:** Ctrl+, opens Settings, F11 toggles full screen, Esc steps back a level, and Tab order follows
  the reading order on every page. Nothing new, but it gets checked.

**Done when:** a pad-only pass through every screen needs no pointer; the hint bar's verb matches what A does
on each focused control.

## 7. Leftovers

- **Rung 3 on 16:10** (audit C6): the letterbox bars are 60 px, too small for the sheet. A compact one-row
  sheet that fits a 60 px bar, so the overlay stays the 16:9-only case.
- **The High Contrast app icon:** contrast-qualified tile assets for the Store build, and a runtime icon swap
  for the zip build.
- **The title-bar buttons at 200%** (audit S1): compare against the WinUI Gallery before touching it.

## 8. The showcase review

The acceptance pass, after part 7, and recorded in the captures folder like the audit.

- **The matrix**, for every screen: Dark, Light and High Contrast; 100% and 200% scale; 225% text; 640 × 480,
  1280 × 800, 1920 × 1080 and 3440 × 1440; mouse, keyboard and pad.
- **Beside Windows itself:** Ripcord next to Settings, the Xbox app and Media Player at the same size and
  theme. Anything that looks foreign beside them is a finding.
- **The stopwatch:** presses and seconds from launch to playing, with one console and with three.
- **Fresh eyes:** a first run on a clean data folder (`RIPCORD_DATA_DIR`), pad only.

## Decisions

Asked of the owner and answered on 2026-10-09:

- **A. The hero's text steps up with the card** (Title to Title Large): **yes.** The alternative was a bigger
  card with the same text, which is what `design.md`'s "Windows owns text size" reads as literally. Stepping
  the platform's own ramp at breakpoints is responsive layout, not a second text-size setting.
- **B. Welcome gets the wordmark: yes, on Welcome only.** The 2026-09-24 decision keeps it off the empty home,
  a task screen; Welcome is the one introduction.
- **C. While reconnecting, the way out reads "Stop trying".** "Back to consoles" everywhere else. The app
  changes to match `design.md`.
- **D. Navigation sounds with a pad: try it**, on while a pad is in use, and decide by ear.
