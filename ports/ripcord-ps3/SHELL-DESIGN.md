# The shell, reimagined

*A design plan for what `source/ui/rc_shell.c` should become. Nothing here is built yet.*

## The brief, and the tension in it

This runs on a PlayStation 3. The thing it does — streaming a PS5, a PS4, and one day something
from another vendor entirely, onto a 2006 console — is genuinely remarkable, and the current shell
does not feel like it. It is a grey list on a grey panel. It works, and that was the point of
getting it built; it is not what this machine's software looks like.

The tension is that **nobody is here for the menu**. This is a landing zone. Every second somebody
spends admiring it is a second they wanted to spend in a game. So the target is not "impressive" —
it is *confident*: a screen that looks authored rather than laid out, that moves when you touch it,
and that gets out of the way in two button presses.

The measure of success is that somebody who has used this twice stops noticing it, and somebody
seeing it for the first time smiles.

## Three things worth taking from the PS3

**The wave.** The XMB's undulating background is the single most recognisable thing about this
console's software, and it is recognisable precisely because it is *restrained* — low contrast,
very slow, mostly empty. It also shifts hue with the month, which is the kind of detail that makes
somebody launching in October notice something they cannot name. That is the target register for
everything here.

**The focal point.** The XMB puts exactly one thing in focus and lets everything else recede. It
does not highlight a row in a list; it *enlarges* the thing you are on and dims its neighbours. On a
television ten feet away that is not decoration, it is legibility.

**Rodin.** Already in, already working, read out of `/dev_flash`. It is the voice of this machine and
we get it for free.

The Atlus reference in the brief is worth naming precisely, because the obvious reading of it is
wrong. Persona's menus are loud, and loud is the opposite of what this needs. What is worth stealing
is not the volume — it is that somebody clearly *designed* them, frame by frame, rather than
arranging controls in a box. A menu can have a point of view. It does not have to shout to have one.

## The screen

A horizontal row of console cards over a full-bleed wave.

```
 ╭──────────────────────────────────────────────────────────────────────────╮
 │                                                                          │
 │   RIPCORD                                                          b341   │
 │                                                                          │
 │                                                                          │
 │       ╭────────────╮     ╭────────────╮     ╭┄┄┄┄┄┄┄┄┄┄┄┄╮               │
 │       │    PS5     │     │    PS4     │     ┊             ┊               │
 │       │            │     │            │     ┊      +      ┊               │
 │       │ Front room │     │  Upstairs  │     ┊             ┊               │
 │       │            │     │            │     ┊   Pair a    ┊               │
 │       │  ● Ready   │     │ ● Standby  │     ┊   console   ┊               │
 │       ╰────────────╯     ╰────────────╯     ╰┄┄┄┄┄┄┄┄┄┄┄┄╯               │
 │         ▲ selected                                                        │
 │                                                                          │
 │   Press ✕ to stream from Front room                                      │
 │                                                                          │
 │   ✕ stream      △ forget      (START) options                            │
 ╰──────────────────────────────────────────────────────────────────────────╯
```

**Why cards and not the list.** Two rows of text on a 16:9 television is empty space used badly. The
horizontal axis is the axis this console's own software uses, cards give a status that reads at ten
feet, and — the practical part — the common case is one or two consoles, which is exactly the count
a card row is good at and a scrolling list is silly for.

**What is on a card.** The family tag (`PS5`, `PS4`) small at the top in the accent colour; the
console's own name large and centred, because that is the word its owner thinks in; a status pip and
one word below. A console this PS3 has no keys for gets a *dashed* card rather than a filled one —
present, clearly different, obviously actionable.

**One description line, not one per card.** The XMB does this and it is right: the explanation lives
in a fixed place and changes as the focus moves, so the eye learns where to look once.

**Everything else goes behind `options`.** Settings, search the network, pair by address, quit. The
front screen is for the one decision somebody came to make.

## Motion

Nothing snaps. Everything settles.

| What | How | Why |
|---|---|---|
| Cursor between cards | ~200 ms, ease-out-cubic | The eye follows a moving thing; it has to re-find a jumping one |
| Card on selection | scale to 1.08 with a 4% overshoot | The overshoot is the whole character — it is the difference between "highlighted" and "picked up" |
| Cards on first draw | fade and rise, 60 ms stagger | Makes the screen feel dealt rather than displayed |
| Standby pip | slow breathe, ~2 s | A console you must wake looks asleep |
| Wave | one phase per ~40 s | Ambient. If it is noticeable it is too fast |

**Response is instant; only the picture settles.** A press registers on the frame it arrives — it is
the *drawing* that eases over 200 ms. A menu that makes you wait for an animation before it accepts
the next press is the single worst thing a console menu does, and this must never do it.

## The moment at the start

The wave is already moving when the screen appears. `RIPCORD` draws in centred and large, holds a
beat, then rises into its header position as the cards deal in below it. About 1.5 seconds, and
**any button skips it**.

This is the highest delight-per-line item in the whole plan, and it is also the one most likely to
become annoying, so: once per launch, skippable, and short enough that the skip is rarely worth
reaching for.

## Two small nods

**Seasonal hue.** The wave's base colour comes from the month, as the XMB's did. Roughly five lines,
and somebody who launches this in December and again in July sees something they will not be able to
name. This is the best delight-to-effort ratio available anywhere in this document.

**The trophy card.** A panel that slides in from the top right, holds, and slides out — for "Found 2
consoles", "Paired with Front room", "Could not reach Upstairs". It is an instantly recognisable
piece of this console's vocabulary, we already have events worth announcing, and it reuses
`rc_status_screen`'s machinery rather than adding any.

**And the button glyphs.** `✕ ○ △ □` drawn as shapes rather than spelled out as letters. Four
primitives, and they say "console" faster than any amount of layout. Whether Rodin carries them is
worth a probe; if not they are a circle, two crossed bars, a triangle and a square.

`SELECT` and `START` are the other two, and they are **words in a rounded pill**, which is how PS3-era
games drew them. There is no hamburger on a DualShock 3 — that is a phone idiom, and putting one on a
console screen is exactly the sort of thing that marks software as not from here. `START` opens the
options, which is the convention of the era; `SELECT` is free, and is the obvious home for the
diagnostics toggle if it ever wants one outside a session. Both are already mapped —
`HALYARD_PAD_OPTIONS` is START and `HALYARD_PAD_CREATE` is SELECT, named for what the PS5 calls them
because that is what goes on the wire.

## Which button means "enter" is the console's decision

Japanese PlayStation hardware confirms with ○ and cancels with ✕. Western hardware is the other way
round. The PS3 exposes which one this machine uses as a documented system parameter —
`SYSUTIL_SYSTEMPARAM_ID_ENTER_BUTTON_ASSIGN` (0x0112), right beside the two parameters this port
already reads for the account id — so hardcoding ✕ is a choice to be wrong for some of the people
using it.

Read it once at start-up; every prompt, every glyph and every handler follows it. It is a handful of
lines, almost nothing gets it right, and the people it is wrong for notice immediately and
permanently.

## Sound

Three synthesised tones, no assets to ship, about thirty lines:

- **Navigate** — ~1 kHz sine, 12 ms exponential decay, quiet.
- **Confirm** — two-tone rising, ~80 ms.
- **Back** — the same falling.

Sound is a large part of why a console menu feels like one, and a soft tick under a cursor is most of
the effect. `rc_audio_init` has only ever run inside the stream path, so opening it at the menu is
unproven and belongs in its own stage.

## What makes this affordable

This is the part that decides whether any of the above is real, and it turns on one fact: **at the
menu, the entire machine is idle.** Six SPEs, the RSX, and all the bandwidth the video path uses are
doing nothing. The current shell draws a static panel four times a second.

**The wave is low-frequency, so render it small.** A soft gradient has nothing above a few cycles per
screen in it, so a 480x270 buffer upscaled bilinearly is indistinguishable from one drawn at 1080p.
`rsxSetTransferScaleSurface` does that upscale with an interpolator that is wired rather than
executed — it is free, and 480 is comfortably inside the 1024-pixel source width this hardware's
scaled blit is documented to limit us to.

**All blending stays in main memory. This is not negotiable.** b232 queued a premultiplied blend to
the RSX, its command processor stopped, and every flip after the first stayed pending forever — "no
video just audio". Antialiased text over a moving background means reading what is underneath, and
reads from RSX memory are about a hundred times slower than writes. So the composite happens in the
overlay surface, exactly as it does today, and exactly one blit reaches the screen.

**Two routes, and the cheap one comes first.**

*Route A — two blits.* The wave is blitted full-screen from its own small buffer; the menu surface
is blitted over it as an opaque card. The menu rebuilds only when the cursor moves, exactly as now.
The wave animates at 60 fps for almost nothing. What it cannot do is float text directly on the
wave — the card is a solid rectangle.

*Route B — one composite.* The SPEs generate and upscale the wave straight into the overlay surface
each frame, the PPE draws over it, one blit goes out. Text floats on the wave properly. The cost is a
per-frame surface rebuild: 576k pixels written in main memory plus 2.3 MB to video memory, which is
in the direction the Cell is fast at and is roughly 276 MB/s all told — affordable, but only if the
**text is cached as sprites rather than re-rasterised.** Rasterising glyph runs sixty times a second
is what made b256 run at 2 fps.

Start at A, measure, and go to B only if the flat card turns out to be the thing holding the design
back. A is most of the win.

## Risks, named up front

1. **The per-frame rebuild may not fit.** Unmeasured. Route A does not depend on it; Route B does
   entirely. Measure before committing.
2. **b232 is a standing hazard.** Any blending that reaches the RSX can stop the GPU. Main memory
   only.
3. **A third atlas size costs startup time.** The display-size wordmark wants one, and both existing
   sizes are built at open. Measure; a wordmark drawn as shapes is the fallback.
4. **Audio at menu time is unproven.** Its own stage, and cheap to abandon.
5. **The title moment is the thing most likely to be regretted.** Skippable from the first frame, or
   do not build it.
6. **Do not slow the common path.** One console paired, already selected: launch, skip or wait 1.5 s,
   press ✕. If a change makes that longer, the change is wrong.

## What the hardware said — 2026-09-17, builds b360-b367

**Stage 1 is in, and the measurement that mattered was not the one this document expected.** Costs per
frame at 1920x1080, measured as the interval between successive draws rather than as frames over wall
time (which is not a frame rate — the shell blocks inside discovery and the pairing prompts, and an
earlier version of this number reported 15 fps and 10 fps for runs whose drawing was identical):

```
99,472 us a frame = 10 fps
of which: background 13,904   drawing 67,272   to video memory 10,875   flip wait 7,300
```

**The drawing is 68 percent of the frame**, and every plan above aims at the background. The background
is now a quarter-scale buffer blown up 4x (29,388 us to 13,904, and closer to the original than the
original was to itself — mean channel error 0.41/255 against a full-resolution render). That was worth
doing and it was not the problem.

Three things this settles:

- **`dcbz` does not help.** The background and the copy to video memory cost nearly the same for the
  same 8 MB, which looked like both paying to fetch lines they were about to overwrite. One `dcbz` per
  128 bytes measured *slightly worse*. The ceiling is not one a store pattern can be arranged around.
- **Route A versus Route B is the wrong question.** Neither addresses the drawing, which is where the
  time is. What addresses it is caching the UI as a layer so a frame composites it instead of
  re-rasterising it — which this document already says under "What makes this affordable", and which
  turns out to be the *first* thing to do rather than a condition on Route B.
- **The SPEs are not speculative here.** `rc_spu_yuv` already runs three threads that do nothing but
  bilinear scaling since the decoder began producing RGB, measured at 2,992 us for a full-screen
  picture. The wave's upscale is that same operation.

The open question worth a probe is whether an SPE can DMA straight into RSX memory, which would delete
the 10,875 us copy rather than reduce it. Note this is **not** the b232 hazard: b232 asked the RSX's 2D
blitter to blend. A DMA to mapped memory is what the MFC is for.

**And on the fan.** A full streaming session — cellVdec on the SPEs, three more scaling, network, audio
— moves the Cell +1.0 C. The menu's present state is arguably worse than anything proposed, because the
PPE is saturated for 99 ms and then spins in a wait loop. The shell now samples the Cell and RSX either
side of itself, so every change from here has a before and after instead of an argument.

## Stages

Each is shippable on its own and testable on hardware, which is how everything else here has been
built.

1. **Foundation** — the wave (Route A), the card row, the single description line, options tucked
   away. Static. This alone is most of the transformation. **Landed b360-b367.**
2. **Motion** — eased cursor, card spring, entry stagger, breathing pip.
3. **The moment** — title sequence and the trophy card.
4. **Sound** — three tones, and the settings toggle for them.
5. **Polish** — button glyphs, the seasonal hue checked across a year by moving the clock, and a
   decision on Route B.
