# Ripcord brand assets

Everything the app ships as an icon is generated from the SVGs in this folder. **Nothing under
`src/Ripcord.App/Assets/` should be edited by hand** — change a source here and run the generator.

```powershell
# From anywhere; writes into src/Ripcord.App/Assets/
pwsh brand/generate-assets.ps1
```

Rasterising uses headless Edge, which exists on every machine that can build this project, so there is no
Inkscape/ImageMagick/Node dependency. The `.ico` is assembled by the script itself — the format is a header,
one 16-byte directory entry per frame, then PNG payloads.

## The mark

A play wedge with three trailing dashes. It says two things at once, which is why it was chosen over four
sheets of alternatives:

- **Pull it and it's away.** The wedge is launched; the dashes are the distance it has already covered. That
  is the name (a ripcord is what you pull) and the act of starting a stream, in one shape.
- **Three dashes, one per console family.** In green, blue and red — so the trail is not just speed, it is
  where the picture came from. Today the client speaks PS5; the mark is drawn for where it is going.

The dashes are deliberately **uneven and right-aligned**. Uneven, because a tidy trio is a fast-forward
button. Right-aligned, because that keeps the gap to the wedge constant and puts the raggedness on the
outside edge, where it reads as a trail rather than as a mistake.

## Palette

| Role | Value | Notes |
|---|---|---|
| Ground | `#303743` → `#1B1F26` | Linear, top-left to bottom-right. Lifted from `#23272E` → `#12151A` on 2026-09-24 |
| Edge (Windows) | white, 16% → 0 highlight; 14% hairline | Top half only; hairline 0.75 of 64 units, inset |
| Wedge on tile | `#FFFFFF` | `#17191D` on light grounds |
| Dash 1 | `#2FBF5B` | Xbox |
| Dash 2 | `#2D7DF6` | PlayStation |
| Dash 3 | `#F0433A` | Nintendo |

### Which dash is which

| Vendor | Value | Families |
|---|---|---|
| PlayStation | `#2D7DF6` blue | PS5, PS4 |
| Xbox | `#2FBF5B` green | Xbox |
| Nintendo | `#F0433A` red | none yet — reserved |

**The mapping is by vendor, not by family**, and the app depends on it: every console card, family mark and
accent wash resolves its colour this way (`ConsoleVendor` in `src/Ripcord.Presentation/Consoles/ConsoleFamily.cs`,
against the brushes in `src/Ripcord.App/Styles/Ripcord.xaml`). PS4 and PS5 deliberately share the blue —
they are the same vendor, and a second blue invented to separate them would make the palette say something
the mark does not. What tells those two apart in the UI is the plain-text "PS5"/"PS4" label, which is why
that label is never optional.

This table was written down after the mapping had to be guessed once and was guessed wrong. It lived only in
the designer's head, and the palette section above named the three colours without saying which was which.

**The association is nominative; the values are ours.** Colour association with a vendor is intended and is
the same latitude the project takes with the word "PS5" (see `NOTICE`). What must not happen is anyone
nudging `#2D7DF6` toward an official blue because it looks close — that is the line the trademark note below
draws, and it is about the values, not the association.

**The tile is deep, not warm, and that is a constraint rather than a preference.** A red dash on an
amber-to-coral ground disappears; the colour has to live in the mark, so the ground has to stay out of its way.

**And it is dark, not light, for a measured reason.** On white the green dash is 2.4:1, under the 3:1 a
non-text graphic needs, and on a light taskbar it is 2.2:1. On the ground every dash clears 3:1. The lift on
2026-09-24 went as far as that allows: at the ground's light end blue is 3.1:1 and red 3.2:1. The lift was
made because the old values read as a black square on a light taskbar, and as nothing at all on a dark
one (1.07:1 against `#1C1C1C`), which is also why the edge exists.

**Trademark hygiene.** The green, blue and red are our own values, chosen to read as "three platforms". They
are not sampled from any console maker's brand guide, and the mark resembles no vendor logo. Colour
association here is nominative — the same latitude the project takes with the word "PS5" (see `NOTICE`) — and
it only holds while the values stay ours. Do not "correct" them toward anyone's official brand colours.

**Accessibility.** Green and red as neighbours is the weak point of this trio. The differing dash lengths do
most of the differentiating work, which is another reason the lengths are not up for tidying. Worth a
deuteranopia check before any colour is changed.

## The wordmark

**Outfit SemiBold (600), lowercase**, set beside the mark. Settled 2026-09-24 against Poppins and Plus
Jakarta Sans. Outfit's near-circular bowls echo the wedge's round joins and the dashes' pill ends, and it
was already the face in the design drawings. Poppins was ruled out as too common to read as a choice.
Plus Jakarta Sans sat too close to Segoe UI Variable, where the wordmark would look like UI text.

- **Lowercase in artwork only.** In running text the name is "Ripcord". Title case puts a tall *R* beside
  the wedge and the two tallest shapes compete for the left edge.
- **Never the word without the mark** in branded art. Without the mark the colour disappears, and it is
  just a word in a nice font.
- **The lockup is horizontal**, and its geometry is defined by the type, so it scales without a spec:
  - The mark's ink runs from the baseline to the cap height.
  - The gap from the wedge's tip to the *r* is one x-height.
  - Clear space around the lockup is one blue dash on every side.
- **A stacked lockup does not exist yet**, on purpose. Draw it when a square surface actually needs one
  (a social preview, a square store hero), not before.

**The artwork is outlines, not text, and the font is never committed.** `outline-wordmark.py` fetches Outfit
from a pinned google/fonts commit into `brand/third-party/` (gitignored), checks its SHA-256, and writes
the three SVGs below as plain paths. This is the ports' rule for third-party code, applied here: nothing
is vendored, and `NOTICE` lists the font under "Brand artwork". Outfit is SIL OFL 1.1 with no Reserved
Font Name. OFL governs the font software, not artwork made with it, so the SVGs carry no licence
obligation. The fetched copy comes with its `OFL.txt`. Rerun the script only when the face, weight,
tracking or lockup rules change (`pip install fonttools uharfbuzz`). The normal build never calls it.

## The tile is layers

There is no tile SVG. `generate-assets.ps1` composes each tile from:

- **Ground** (`ripcord-ground.svg`): the material, full-bleed, with **no shape**.
- **Mark** (`ripcord-mark-ondark.svg`, or `ripcord-mark-small.svg` at ≤32 px).
- **Shape and edge**, added by the generator: a rounded square, plus a top highlight and a hairline.

The split exists for the platforms still to come. Android masks an adaptive icon to a shape of the
launcher's choosing, so a drawn edge ends up as slivers on a circle and one corner on a teardrop.
macOS 26 draws its own rounded square and glass edge, so a drawn one doubles up. Those targets get the
layers and nothing else:

| Platform | Gets | Status |
|---|---|---|
| Windows | ground + mark + shape + edge | Generated |
| Linux (freedesktop `hicolor`) | the same composite as Windows | When the build lands |
| Android adaptive | background = ground (108-unit), foreground = mark, monochrome = `ripcord-mono.svg` | When the build lands |
| macOS | ground + mark on Apple's rounded square with Apple's margin; no hairline. Icon Composer layers for 26 | When the build lands. Read Apple's current grid first: none of it was checked when this was written |

The edge is therefore a per-target setting, not part of the drawing. It can be dropped from one platform,
or all of them, without redrawing anything.

## Sources

| File | Use |
|---|---|
| `ripcord-ground.svg` | The tile's material, no shape. Every tile is composed on it |
| `ripcord-mark.svg` | Mark alone, graphite wedge, for light grounds |
| `ripcord-mark-ondark.svg` | Mark alone, white wedge, for dark grounds. The tile's mark |
| `ripcord-mark-small.svg` | The tile's mark redrawn for ≤32 px, in tile coordinates — **not** a scaled copy |
| `ripcord-mono.svg` | One-colour cut (`currentColor`) for tray, menu bar, stencil contexts |
| `ripcord-lockup.svg` | Mark + wordmark, graphite, for light grounds. **Generated** by `outline-wordmark.py` |
| `ripcord-lockup-ondark.svg` | Mark + wordmark, white, for dark grounds. **Generated**. The wide tile and splash |
| `ripcord-wordmark.svg` | The word alone, `currentColor`. **Generated** |

### Why there is a separate small cut

At 16 px, one pixel is four units of the 64-unit box. The master's dashes and gaps do not land on that grid,
so scaling it down produces a soft, muddy icon at exactly the size most people see most often. Every
coordinate in `ripcord-mark-small.svg` is a multiple of 4: dashes are 2 px tall with 1 px gaps, and the wedge
is sharp-cornered because a pixel of rounding is invisible at that size and costs a crisp edge. The small
tile also goes without the edge: a sub-pixel hairline at 16 px is mud.

## Geometry rules

- 64-unit box. The mark lives inside a **centred ø58 circle** so Android's adaptive-icon crop — which may be
  a circle, a squircle, a teardrop or a rounded square depending on the launcher — cannot clip it.
- Tile corner radius is 23% of the tile (15 units), which approximates the macOS/iOS squircle and survives
  being re-cropped by anything squarer.
- The tile is also drawn as vectors in `AboutPage.xaml` (the hero tile), edge included. That is the one
  duplicate of the composed tile in the tree; keep it in step with the ground, the mark and the edge values
  in `generate-assets.ps1`. `RipcordMark` and `ConnectMark` redraw the mark alone.
- **The wordmark has a second copy too**, in `src/Ripcord.App/Controls/RipcordWordmark.xaml`: the single
  path from `ripcord-wordmark.svg`, pasted. It is drawn rather than loaded for the reason the tile is — an
  asset would need a raster per scale factor, and soft type is the one thing a wordmark cannot afford. So
  **rerunning `outline-wordmark.py` means copying that path across again**, and a change to the type, the
  tracking or the lockup rules that stops at the SVG will leave the app showing the old cut.
- The console card's dark gradient is **not** the ground. It kept the pre-lift values, because the wedge
  facet is defined as the card lifted and would disappear against a card at the new values. See
  `Ripcord.Card.xaml`.

## Not done yet

- **macOS, Linux and Android targets.** The layers are ready for all three (see "The tile is layers"); the
  generator only emits what the Windows build consumes today. Add targets to `generate-assets.ps1` when
  those platforms land.
- **A README header.** The lockups exist in both cuts. The repository README does not use them yet.
- **Truly unplated taskbar icons.** `Square44x44Logo.targetsize-*_altform-unplated` and `-lightunplated`
  are rendered from the plated tile, so the unplated slots carry a plate. Windows would accept a plateless
  mark there, with a graphite wedge for light taskbars. This was offered on 2026-09-24 as option C and not
  taken. The `.ico` cannot follow in any case: it has one image for both taskbar themes.
