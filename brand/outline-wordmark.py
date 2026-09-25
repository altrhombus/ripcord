#!/usr/bin/env python3
"""
Draws the Ripcord wordmark and lockups as plain SVG paths.

    python brand/outline-wordmark.py        # needs: pip install fonttools uharfbuzz

The word is set in Outfit SemiBold, lowercase, then converted to outlines. The committed SVGs therefore
contain no font and no text. They are artwork made with the font, and they render the same everywhere,
including in a headless Edge that has never seen Outfit and in a PS3 port that has no font engine.

The font itself is never committed. It is fetched from a pinned google/fonts commit into brand/third-party/
(gitignored), and its SHA-256 is checked before it is used, which is the same rule the console ports
follow for their dependencies. Run this only when the face, weight, tracking or lockup geometry changes.
Nothing in the normal build calls it, and generate-assets.ps1 consumes its output rather than rerunning it.

Outputs, all in brand/:
    ripcord-wordmark.svg         the word alone, in currentColor
    ripcord-lockup.svg           mark + word for light grounds: graphite wedge and word
    ripcord-lockup-ondark.svg    mark + word for dark grounds: white wedge and word

The lockup geometry is defined by the type, so it scales without a separate spec:
    - The mark's ink runs from the baseline to the cap height.
    - The gap from the wedge's tip to the r's stem is one x-height.
"""

import hashlib
import sys
import urllib.request
from pathlib import Path

import uharfbuzz as hb
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

BRAND = Path(__file__).resolve().parent
CACHE = BRAND / "third-party" / "outfit"

# google/fonts at 8b0a1d0f (2026-03-12), Outfit 1.100. Both digests are checked; a mismatch is fatal.
FONT_COMMIT = "8b0a1d0f5983c89bc2b93f1b5fb55f9e252744b5"
FONT_BASE = f"https://raw.githubusercontent.com/google/fonts/{FONT_COMMIT}/ofl/outfit"
FETCH = {
    "Outfit[wght].ttf": ("Outfit%5Bwght%5D.ttf", "fc7287273e66929776e2ba54f144fe699080bec29f61bf649d70d871468aeade"),
    "OFL.txt": ("OFL.txt", "c676351bf8576b9aba743cd5eaa8c0e7ee0d51f805d720447b4df4ddb6a2e416"),
}

TEXT = "ripcord"
WEIGHT = 600
TRACKING = -15  # font units per glyph gap, at 1000 upm: -0.015 em, the value approved on the options page

# The mark, in its own 64-unit box, exactly as ripcord-mark.svg draws it. Its ink bounds include the
# wedge's 6-unit round-joined stroke, which reaches 3 units past each vertex.
MARK_INK = (5.0, 14.0, 56.0, 50.0)  # left, top, right, bottom
WEDGE = "M29 17L53 32L29 47Z"
DASHES = (
    (12, 18, 9, "#2FBF5B"),
    (5, 28.5, 16, "#2D7DF6"),
    (9, 39, 12, "#F0433A"),
)

GRAPHITE = "#17191D"
WHITE = "#FFFFFF"


def fetch_font() -> Path:
    CACHE.mkdir(parents=True, exist_ok=True)
    for name, (remote, digest) in FETCH.items():
        target = CACHE / name
        if not target.exists():
            print(f"fetching {name}")
            with urllib.request.urlopen(f"{FONT_BASE}/{remote}") as response:
                target.write_bytes(response.read())
        actual = hashlib.sha256(target.read_bytes()).hexdigest()
        if actual != digest:
            target.unlink()
            sys.exit(f"{name}: sha256 {actual} does not match the pin {digest}; refusing to use it")
    return CACHE / "Outfit[wght].ttf"


def fmt(value: float) -> str:
    text = f"{value:.1f}"
    return text[:-2] if text.endswith(".0") else text


def shape(path: Path):
    """Glyph ids and pen positions for TEXT at WEIGHT, with the font's own kerning plus TRACKING."""
    font = hb.Font(hb.Face(hb.Blob.from_file_path(str(path))))
    font.set_variations({"wght": WEIGHT})
    buffer = hb.Buffer()
    buffer.add_str(TEXT)
    buffer.guess_segment_properties()
    hb.shape(font, buffer, {"kern": True, "liga": False})

    x = 0
    placed = []
    for index, (info, position) in enumerate(zip(buffer.glyph_infos, buffer.glyph_positions)):
        placed.append((info.codepoint, x + position.x_offset))
        x += position.x_advance
        if index < len(buffer.glyph_infos) - 1:
            x += TRACKING
    return placed


def outline(path: Path):
    """The word as one SVG path in font units, y flipped to point down, baseline at y = 0."""
    varfont = TTFont(path)
    static = instancer.instantiateVariableFont(varfont, {"wght": WEIGHT})
    glyphs = static.getGlyphSet()
    order = static.getGlyphOrder()

    svg_pen = SVGPathPen(glyphs, ntos=fmt)
    bounds_pen = BoundsPen(glyphs)
    for glyph_id, x in shape(path):
        name = order[glyph_id]
        flip = (1, 0, 0, -1, x, 0)
        glyphs[name].draw(TransformPen(svg_pen, flip))
        glyphs[name].draw(TransformPen(bounds_pen, flip))

    os2 = static["OS/2"]
    return svg_pen.getCommands(), bounds_pen.bounds, os2.sCapHeight, os2.sxHeight


def mark_group(wedge_fill: str, scale: float, tx: float, ty: float) -> str:
    dashes = "\n".join(
        f'    <rect x="{x}" y="{y}" width="{w}" height="7" rx="3.5" fill="{colour}" />'
        for x, y, w, colour in DASHES
    )
    return (
        f'  <g transform="translate({fmt(tx)} {fmt(ty)}) scale({scale:.4f})">\n'
        f'    <path d="{WEDGE}" fill="{wedge_fill}" stroke="{wedge_fill}" stroke-width="6" stroke-linejoin="round" />\n'
        f"{dashes}\n"
        "  </g>"
    )


HEADER = """<!--
  {what}
  GENERATED by brand/outline-wordmark.py from Outfit SemiBold (SIL OFL 1.1). Do not edit by hand:
  change the script and rerun it. This file contains outlines, not the font.
-->
"""


def main() -> None:
    font_path = fetch_font()
    word, (x0, y0, x1, y1), cap_height, x_height = outline(font_path)

    # The word alone, tight to its ink.
    (BRAND / "ripcord-wordmark.svg").write_text(
        HEADER.format(what="Ripcord wordmark, word only, in currentColor. Never used without the mark in branded art.")
        + f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{fmt(x0)} {fmt(y0)} {fmt(x1 - x0)} {fmt(y1 - y0)}">\n'
        + f'  <path d="{word}" fill="currentColor" />\n</svg>\n',
        encoding="utf-8",
    )

    # The lockup: the mark's ink scaled to span baseline to cap height, left edge at x = 0.
    left, top, right, bottom = MARK_INK
    scale = cap_height / (bottom - top)
    tx, ty = -left * scale, -bottom * scale
    tip = (right - left) * scale
    shift = tip + x_height - x0  # the word moves so its first ink sits one x-height past the tip

    view_top = min(-cap_height, y0)
    view_bottom = max(0.0, y1)
    view_right = x1 + shift
    viewbox = f"0 {fmt(view_top)} {fmt(view_right)} {fmt(view_bottom - view_top)}"

    for filename, fill, what in (
        ("ripcord-lockup.svg", GRAPHITE, "Ripcord lockup for light grounds: graphite wedge and word."),
        ("ripcord-lockup-ondark.svg", WHITE, "Ripcord lockup for dark grounds: white wedge and word."),
    ):
        (BRAND / filename).write_text(
            HEADER.format(what=what)
            + f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{viewbox}">\n'
            + mark_group(fill, scale, tx, ty)
            + "\n"
            + f'  <path transform="translate({fmt(shift)} 0)" d="{word}" fill="{fill}" />\n</svg>\n',
            encoding="utf-8",
        )

    print(f"cap height {cap_height}, x-height {x_height}, mark scale {scale:.4f}, lockup {viewbox}")


if __name__ == "__main__":
    main()
