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
| Tile | `#23272E` → `#12151A` | Linear, top-left to bottom-right |
| Wedge on tile | `#FFFFFF` | `#17191D` on light grounds |
| Dash 1 | `#2FBF5B` | |
| Dash 2 | `#2D7DF6` | |
| Dash 3 | `#F0433A` | |

**The tile is deep, not warm, and that is a constraint rather than a preference.** A red dash on an
amber-to-coral ground disappears; the colour has to live in the mark, so the ground has to stay out of its way.

**Trademark hygiene.** The green, blue and red are our own values, chosen to read as "three platforms". They
are not sampled from any console maker's brand guide, and the mark resembles no vendor logo. Colour
association here is nominative — the same latitude the project takes with the word "PS5" (see `NOTICE`) — and
it only holds while the values stay ours. Do not "correct" them toward anyone's official brand colours.

**Accessibility.** Green and red as neighbours is the weak point of this trio. The differing dash lengths do
most of the differentiating work, which is another reason the lengths are not up for tidying. Worth a
deuteranopia check before any colour is changed.

## Sources

| File | Use |
|---|---|
| `ripcord-tile.svg` | The app icon. Tile + mark, mark at 72% and centred |
| `ripcord-tile-small.svg` | The same icon redrawn for ≤32 px — **not** a scaled copy |
| `ripcord-mark.svg` | Mark alone, graphite wedge, for light grounds |
| `ripcord-mark-ondark.svg` | Mark alone, white wedge, for dark grounds |
| `ripcord-mono.svg` | One-colour cut (`currentColor`) for tray, menu bar, stencil contexts |

### Why there is a separate small cut

At 16 px, one pixel is four units of the 64-unit box. The master's dashes and gaps do not land on that grid,
so scaling it down produces a soft, muddy icon at exactly the size most people see most often. Every
coordinate in `ripcord-tile-small.svg` is a multiple of 4: dashes are 2 px tall with 1 px gaps, and the wedge
is sharp-cornered because a pixel of rounding is invisible at that size and costs a crisp edge.

## Geometry rules

- 64-unit box. The mark lives inside a **centred ø58 circle** so Android's adaptive-icon crop — which may be
  a circle, a squircle, a teardrop or a rounded square depending on the launcher — cannot clip it.
- Tile corner radius is 23% of the tile (15 units), which approximates the macOS/iOS squircle and survives
  being re-cropped by anything squarer.
- The mark is also drawn as vectors in `AboutPage.xaml` (the hero tile). That is the one duplicate of this
  geometry in the tree; keep it in step with `ripcord-tile.svg`.

## Not done yet

- **Wordmark.** No typeface is chosen, so nothing shipped contains the name as artwork. A rounded geometric
  sans (Poppins, Outfit, Plus Jakarta Sans — all open-licensed) suits the wedge.
- **macOS `.icns` and Android adaptive layers.** The sources are ready for both; the generator only emits what
  the Windows build consumes today. Add targets to `generate-assets.ps1` when those platforms land.
