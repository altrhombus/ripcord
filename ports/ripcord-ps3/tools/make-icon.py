#!/usr/bin/env python3
"""
Draws this port's XMB icon - build/ICON0.PNG - from brand/ripcord-tile.svg.

WHY THIS EXISTS RATHER THAN brand/generate-assets.ps1. That script rasterises with headless Edge, which
is a reasonable dependency for the Windows app and is not available to a cross-compile that otherwise
needs nothing but the ps3dev toolchain and a shell. This draws the same five shapes with arithmetic: a
gradient, a triangle and three rounded rectangles, all of which have exact distance functions, so the
edges are antialiased from geometry rather than from supersampling and there is nothing to install.

THE GEOMETRY IS READ OUT OF THE SVG, NOT COPIED FROM IT. Every number below comes from parsing
brand/ripcord-tile.svg, so editing the mark updates this icon too and the two cannot drift. A parse that
does not find what it expects fails loudly - a silently different icon is worse than no icon, because it
looks like the build worked.

The PS3 wants 320x176. That is a landscape frame for a square mark, so the tile's own rounded corners are
dropped and the gradient goes full bleed: the XMB draws this inside its own frame and a rounded rectangle
floating in a rectangle reads as a mistake.
"""
import re
import struct
import sys
import zlib
from pathlib import Path

W, H = 320, 176
# How much of the icon's height the mark itself occupies. Measured against the MARK's own extent rather
# than the SVG's 64-unit square, because the square is mostly the padding an adaptive-icon crop wants and
# scaling to it leaves the mark small and adrift in a frame the XMB has already cropped for us.
MARK_FILL = 0.58


def fail(why):
    sys.stderr.write("make-icon: %s\n" % why)
    sys.exit(1)


def parse(svg_text):
    """The five shapes, as the SVG states them. Anything missing is a failure, not a default."""
    out = {}

    stops = re.findall(r'<stop[^>]*stop-color="#([0-9A-Fa-f]{6})"', svg_text)
    if len(stops) != 2:
        fail("expected two gradient stops in ripcord-tile.svg, found %d" % len(stops))
    out["grad"] = [tuple(int(s[i:i + 2], 16) for i in (0, 2, 4)) for s in stops]

    m = re.search(r'<g transform="translate\(([-\d.]+),([-\d.]+)\) scale\(([-\d.]+)\) '
                  r'translate\(([-\d.]+),([-\d.]+)\)"', svg_text)
    if m is None:
        fail("could not read the mark group's transform from ripcord-tile.svg")
    out["xform"] = [float(v) for v in m.groups()]

    m = re.search(r'<path d="M([\d.]+) ([\d.]+)L([\d.]+) ([\d.]+)L([\d.]+) ([\d.]+)Z"'
                  r'[^>]*fill="#([0-9A-Fa-f]{6})"[^>]*stroke-width="([\d.]+)"', svg_text)
    if m is None:
        fail("could not read the play wedge from ripcord-tile.svg")
    g = m.groups()
    out["wedge"] = [(float(g[0]), float(g[1])), (float(g[2]), float(g[3])),
                    (float(g[4]), float(g[5]))]
    out["wedge_rgb"] = tuple(int(g[6][i:i + 2], 16) for i in (0, 2, 4))
    out["wedge_stroke"] = float(g[7])

    dashes = re.findall(r'<rect x="([\d.]+)" y="([\d.]+)" width="([\d.]+)" height="([\d.]+)" '
                        r'rx="([\d.]+)" fill="#([0-9A-Fa-f]{6})"', svg_text)
    if len(dashes) != 3:
        fail("expected three dashes in ripcord-tile.svg, found %d" % len(dashes))
    out["dashes"] = [(float(x), float(y), float(w), float(h), float(r),
                      tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))) for x, y, w, h, r, c in dashes]
    return out


def dist_triangle(px, py, tri):
    """Signed distance to a triangle, negative inside. The stroke's round join is this dilated."""
    best = 1e30
    for i in range(3):
        ax, ay = tri[i]
        bx, by = tri[(i + 1) % 3]
        ex, ey = bx - ax, by - ay
        wx, wy = px - ax, py - ay
        t = (wx * ex + wy * ey) / (ex * ex + ey * ey)
        t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
        dx, dy = wx - ex * t, wy - ey * t
        best = min(best, (dx * dx + dy * dy) ** 0.5)

    # Inside when the point is on the same side of all three edges - either side, so the winding the
    # path happens to be written in does not matter.
    cross = [(tri[(i + 1) % 3][0] - tri[i][0]) * (py - tri[i][1]) -
             (tri[(i + 1) % 3][1] - tri[i][1]) * (px - tri[i][0]) for i in range(3)]
    inside = all(c >= 0.0 for c in cross) or all(c <= 0.0 for c in cross)
    return -best if inside else best


def dist_rrect(px, py, x, y, w, h, r):
    cx, cy = x + w / 2.0, y + h / 2.0
    dx = abs(px - cx) - (w / 2.0 - r)
    dy = abs(py - cy) - (h / 2.0 - r)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    return (ox * ox + oy * oy) ** 0.5 + min(max(dx, dy), 0.0) - r


def coverage(d):
    """A one-pixel ramp centred on the edge - the same rule the shell's shapes use."""
    c = 0.5 - d
    return 0.0 if c <= 0.0 else (1.0 if c >= 1.0 else c)


def over(dst, rgb, a):
    return tuple(int(round(rgb[i] * a + dst[i] * (1.0 - a))) for i in range(3))


def main():
    root = Path(__file__).resolve().parents[3]
    svg = root / "brand" / "ripcord-tile.svg"
    if not svg.is_file():
        fail("cannot find %s" % svg)
    art = parse(svg.read_text(encoding="utf-8"))

    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("build/ICON0.PNG")
    out.parent.mkdir(parents=True, exist_ok=True)

    tri = art["wedge"]
    halo = art["wedge_stroke"] / 2.0
    g0, g1 = art["grad"]

    # The group's transform is translate(tx,ty) . scale(s) . translate(ix,iy), applied right to left -
    # so the inverse SUBTRACTS the inner translate. Getting that sign wrong put the mark off the corner
    # of the icon, which is the sort of mistake that is obvious in a picture and invisible in a diff.
    tx, ty, scale, ix, iy = art["xform"]

    # The mark's own bounding box, in its own coordinates, stroke included.
    lo_x = min(min(p[0] for p in tri) - halo, min(d[0] for d in art["dashes"]))
    hi_x = max(max(p[0] for p in tri) + halo, max(d[0] + d[2] for d in art["dashes"]))
    lo_y = min(min(p[1] for p in tri) - halo, min(d[1] for d in art["dashes"]))
    hi_y = max(max(p[1] for p in tri) + halo, max(d[1] + d[3] for d in art["dashes"]))

    # Mark units to icon pixels, sized so the mark fills MARK_FILL of the height, and centred on its
    # own box rather than on the square's - the two are not the same point.
    per_px = (H * MARK_FILL) / (hi_y - lo_y)
    cx, cy = (lo_x + hi_x) / 2.0, (lo_y + hi_y) / 2.0

    def to_mark(px, py):
        """An icon pixel centre in the mark's own coordinates."""
        return (cx + (px - W / 2.0) / per_px, cy + (py - H / 2.0) / per_px)

    _ = (tx, ty, scale, ix, iy)   # read and checked above; the mark is placed by its own box

    rows = []
    for py in range(H):
        row = bytearray()
        row.append(0)                                   # PNG filter: none
        for px in range(W):
            # The tile's gradient runs corner to corner; here the tile IS the icon.
            t = ((px + 0.5) / W + (py + 0.5) / H) / 2.0
            c = tuple(int(round(g0[i] + (g1[i] - g0[i]) * t)) for i in range(3))

            mx, my = to_mark(px + 0.5, py + 0.5)
            a = coverage((dist_triangle(mx, my, tri) - halo) * per_px)
            if a > 0.0:
                c = over(c, art["wedge_rgb"], a)
            for dx, dy, dw, dh, dr, drgb in art["dashes"]:
                a = coverage(dist_rrect(mx, my, dx, dy, dw, dh, dr) * per_px)
                if a > 0.0:
                    c = over(c, drgb, a)
            row += bytes(c)
        rows.append(bytes(row))

    raw = b"".join(rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    out.write_bytes(png)
    sys.stderr.write("make-icon: wrote %s (%dx%d, %d bytes)\n" % (out, W, H, len(png)))


if __name__ == "__main__":
    main()
