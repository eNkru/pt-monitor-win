#!/usr/bin/env python3
"""Draw the flat Windows-style app icon and write src/PTMonitor.App/app.ico.

The Windows build owns its own icon: a flat, light rounded tile carrying one
accent-blue glyph (a down/up arrow pair over a progress pill), in the visual
language of Windows 11 first-party app icons -- no gradients, no sheen, no
drop shadow, no macOS-style charcoal card.

`src/PTMonitor.App/Services/TrayIconFactory.cs` draws the *same* artwork at
runtime for the tray. The two implementations must be edited together; the
constants below and the ones in that file are deliberately identical (see
.trellis/spec/frontend/lifecycle-and-tray.md).

    python3 scripts/make-app-icon.py [path-to-ico]
    python3 scripts/make-app-icon.py --dump /tmp/icon   # + PNG previews

Stdlib only, deterministic output. Sizes 16/24/32/48 are stored as 32-bit
BMP records (widest shell compatibility); 64/128/256 as PNG records (smaller,
and supported since Vista).
"""

import pathlib
import struct
import sys
import zlib

# --- Artwork -----------------------------------------------------------------
# Every length is a fraction of the icon side, so one description covers 16 px
# through 256 px. Keep in sync with TrayIconFactory.cs.

CARD_RADIUS = 0.22          # tile corner radius
CARD_BORDER = 0.030         # flat hairline ring around the tile
ERROR_BORDER = 0.050        # error state draws a heavier ring

TILE = (0xF2, 0xF6, 0xFA)   # flat, very light cool grey-blue
BORDER = (0xD3, 0xDE, 0xE9)
ACCENT = (0x0F, 0x6C, 0xBD)  # Fluent light-theme brand blue
DANGER = (0xC4, 0x2B, 0x1C)  # Fluent light-theme critical red

DIM = 0.40                  # alpha of the up arrow / idle arrows
TRACK = 0.14                # alpha of the pill track
PILL_FILL = 0.62            # active-state pill fill

BAND_TOP = 0.32             # arrow band, top edge
BAND_BOTTOM = 0.66          # arrow band, bottom edge
SHAFT_HALF = 0.043
HEAD_HALF = 0.105
HEAD_DEPTH = 0.105
DOWN_CX = 0.37
UP_CX = 0.63
PILL_W = 0.52
PILL_H = 0.042
PILL_Y = 0.785

# Below 32 px the two-arrow composition cannot survive: at 16 px each arrow
# gets ~5 px of width, the heads merge into one blob and the pill is sub-pixel.
# Windows solves this the same way for every first-party icon -- a simplified
# single-glyph variant for the smallest entries -- so below 32 px we drop the
# up arrow and draw one chunkier down arrow over a fatter pill. Only the exe
# icon has sub-32 entries; TrayIconFactory (always 32 px) implements the full
# artwork only.
SMALL_SIDE = 32
SMALL_ARROW_CX = 0.50
SMALL_ARROW_TOP = 0.14
SMALL_ARROW_BOTTOM = 0.58
SMALL_SHAFT_HALF = 0.085
SMALL_HEAD_HALF = 0.170
SMALL_HEAD_DEPTH = 0.150
SMALL_PILL_Y = 0.700
SMALL_PILL_H = 0.110
SMALL_CARD_RADIUS = 0.19

SUPERSAMPLE = 4             # samples per axis per pixel (antialiasing)

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
BMP_SIZES = (16, 24, 32, 48)  # stored as BMP; everything else as PNG


# --- Coverage tests ----------------------------------------------------------

def inside_rounded_rect(px, py, x0, y0, x1, y1, radius):
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False
    # Clamp the sample into the inner rectangle; only corner samples end up
    # with a non-zero offset, so one distance test covers all four corners.
    cx = min(max(px, x0 + radius), x1 - radius)
    cy = min(max(py, y0 + radius), y1 - radius)
    dx = px - cx
    dy = py - cy
    return dx * dx + dy * dy <= radius * radius


def inside_polygon(px, py, points):
    """Ray casting, half-open edges so shared vertices are counted once."""
    inside = False
    count = len(points)
    for index in range(count):
        ax, ay = points[index]
        bx, by = points[(index + 1) % count]
        if (ay > py) != (by > py):
            crossing = ax + (py - ay) * (bx - ax) / (by - ay)
            if px < crossing:
                inside = not inside
    return inside


# --- Shape list for one state ------------------------------------------------

def metrics(side):
    """Proportions for one render size; sub-32 entries use the simplified set."""
    if side < SMALL_SIDE:
        # One centred arrow: down_cx and up_cx collapse onto the axis.
        return {
            "card_radius": SMALL_CARD_RADIUS,
            "card_border": CARD_BORDER,
            "single_glyph": True,
            "down_cx": SMALL_ARROW_CX,
            "up_cx": SMALL_ARROW_CX,
            "band_top": SMALL_ARROW_TOP,
            "band_bottom": SMALL_ARROW_BOTTOM,
            "shaft_half": SMALL_SHAFT_HALF,
            "head_half": SMALL_HEAD_HALF,
            "head_depth": SMALL_HEAD_DEPTH,
            "pill_h": SMALL_PILL_H,
            "pill_y": SMALL_PILL_Y,
        }
    return {
        "card_radius": CARD_RADIUS,
        "card_border": CARD_BORDER,
        "single_glyph": False,
        "down_cx": DOWN_CX,
        "up_cx": UP_CX,
        "band_top": BAND_TOP,
        "band_bottom": BAND_BOTTOM,
        "shaft_half": SHAFT_HALF,
        "head_half": HEAD_HALF,
        "head_depth": HEAD_DEPTH,
        "pill_h": PILL_H,
        "pill_y": PILL_Y,
    }


def arrow_points(side, down, m):
    """Polygon for one arrow, in device pixels; the up arrow points up."""
    cx = (m["down_cx"] if down else m["up_cx"]) * side
    shaft = m["shaft_half"] * side
    head = m["head_half"] * side
    depth = m["head_depth"] * side
    band_top = m["band_top"] * side
    band_bottom = m["band_bottom"] * side

    tip = band_bottom if down else band_top
    base = band_bottom - depth if down else band_top + depth
    far = band_top if down else band_bottom

    return [
        (cx - shaft, far),
        (cx + shaft, far),
        (cx + shaft, base),
        (cx + head, base),
        (cx, tip),
        (cx - head, base),
        (cx - shaft, base),
    ]


def shapes_for(state, side):
    """Ordered (coverage, colour, alpha) layers for one tray state.

    The tile ring is modelled as a *stroke*: a pen of width `w` centred on a
    path inset by `w/2` from the tile edge, so its coverage is the tile shape
    minus that shape inset by `w`. TrayIconFactory.cs strokes it with an
    Avalonia pen, and both renderers must agree or the corners drift apart.
    """
    m = metrics(side)
    accent = DANGER if state == "error" else ACCENT
    ring = (ERROR_BORDER if state == "error" else m["card_border"]) * side
    ring_color = DANGER if state == "error" else BORDER

    radius = m["card_radius"] * side

    def tile(x, y):
        return inside_rounded_rect(x, y, 0, 0, side, side, radius)

    def ring_inner(x, y):
        if ring <= 0.0:
            return False
        return inside_rounded_rect(
            x, y, ring, ring, side - ring, side - ring, max(radius - ring, 0.0))

    layers = [
        (tile, TILE, 1.0),
        (lambda x, y: tile(x, y) and not ring_inner(x, y), ring_color, 1.0),
    ]

    down = arrow_points(side, down=True, m=m)
    layers.append((lambda x, y: inside_polygon(x, y, down), accent,
                   1.0 if state != "idle" else DIM))
    if not m["single_glyph"]:
        up = arrow_points(side, down=False, m=m)
        layers.append((lambda x, y: inside_polygon(x, y, up), accent, DIM))

    pill_x = (0.5 - PILL_W / 2) * side
    pill_y = m["pill_y"] * side
    pill_w = PILL_W * side
    pill_h = m["pill_h"] * side
    pill_radius = pill_h / 2
    layers.append((lambda x, y: inside_rounded_rect(
        x, y, pill_x, pill_y, pill_x + pill_w, pill_y + pill_h, pill_radius),
        accent, TRACK))

    fraction = {"active": PILL_FILL, "error": 1.0}.get(state)
    if fraction:
        layers.append((lambda x, y: inside_rounded_rect(
            x, y, pill_x, pill_y,
            pill_x + pill_w * fraction, pill_y + pill_h, pill_radius),
            accent, 1.0))
    return layers


# --- Rasteriser --------------------------------------------------------------

def composite(dst, src_rgb, src_alpha):
    """Straight-alpha source-over of one layer at a given coverage."""
    if src_alpha <= 0.0:
        return dst
    dst_alpha = dst[3]
    out_alpha = src_alpha + dst_alpha * (1.0 - src_alpha)
    if out_alpha <= 0.0:
        return (0.0, 0.0, 0.0, 0.0)
    weight_dst = dst_alpha * (1.0 - src_alpha)
    channels = tuple(
        (src_rgb[i] * src_alpha + dst[i] * weight_dst) / out_alpha
        for i in range(3)
    )
    return channels + (out_alpha,)


def render(state, side, supersample=SUPERSAMPLE):
    """Render one state; returns rows of (r, g, b, a) bytes, top-down.

    Coverage is counted per layer over the supersample grid and each layer is
    then composited *once* at its coverage. Compositing the individual samples
    instead would compound the 1/n alphas (1-(1-1/n)^n, never 1.0) and wash
    every colour out.
    """
    layers = shapes_for(state, float(side))
    step = 1.0 / supersample
    total = float(supersample * supersample)
    rows = []

    for py in range(side):
        row = []
        for px in range(side):
            hits = [0] * len(layers)
            for sy in range(supersample):
                y = py + (sy + 0.5) * step
                for sx in range(supersample):
                    x = px + (sx + 0.5) * step
                    for index, (covers, _, _) in enumerate(layers):
                        if covers(x, y):
                            hits[index] += 1

            pixel = (0.0, 0.0, 0.0, 0.0)
            for (_, colour, alpha), hit in zip(layers, hits):
                if hit:
                    pixel = composite(
                        pixel,
                        (colour[0] / 255.0, colour[1] / 255.0, colour[2] / 255.0),
                        alpha * hit / total,
                    )
            row.append(tuple(
                max(0, min(255, round(channel * 255.0))) for channel in pixel
            ))
        rows.append(row)
    return rows


# --- Encoders ----------------------------------------------------------------

def png_bytes(side, rows):
    raw = bytearray()
    for row in rows:
        raw.append(0)  # filter: none
        for pixel in row:
            raw += bytes(pixel)

    def chunk(tag, payload):
        body = tag + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", side, side, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def bmp_bytes(side, rows):
    """32-bit BGRA bitmap plus the legacy 1-bit AND mask, as ICO expects."""
    xor = bytearray()
    for y in range(side - 1, -1, -1):  # bottom-up
        for r, g, b, a in rows[y]:
            xor += bytes((b, g, r, a))

    stride = ((side + 31) // 32) * 4
    mask = bytearray()
    for y in range(side - 1, -1, -1):
        bits = bytearray(stride)
        for x in range(side):
            if rows[y][x][3] < 128:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits

    info = struct.pack("<IiiHHIIiiII", 40, side, side * 2, 1, 32, 0,
                       len(xor) + len(mask), 0, 0, 0, 0)
    return bytes(info) + bytes(xor) + bytes(mask)


def write_ico(entries, out_path):
    count = len(entries)
    directory = b""
    payload = b""
    offset = 6 + 16 * count
    for side, data in sorted(entries):
        dimension = 0 if side >= 256 else side  # 256 does not fit in a byte
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32,
                                 len(data), offset)
        payload += data
        offset += len(data)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(struct.pack("<HHH", 0, 1, count) + directory + payload)
    print(f"wrote {out_path} ({offset} bytes, sizes {sorted(s for s, _ in entries)})")


# --- Previews ----------------------------------------------------------------

def upscale(rows, factor):
    out = []
    for row in rows:
        scaled = [pixel for pixel in row for _ in range(factor)]
        for _ in range(factor):
            out.append(scaled)
    return out


def contact_sheet(background):
    """256 px render plus the small sizes blown up, for eyeballing."""
    canvas_w, canvas_h = 568, 296
    canvas = [[background + (255,)] * canvas_w for _ in range(canvas_h)]

    big = render("active", 256)
    for y, row in enumerate(big):
        for x, pixel in enumerate(row):
            alpha = pixel[3] / 255.0
            blended = tuple(
                round(pixel[i] * alpha + background[i] * (1.0 - alpha))
                for i in range(3)
            )
            canvas[8 + y][8 + x] = blended + (255,)

    y = 8
    for side in (16, 24, 32, 48):
        small = upscale(render("active", side), 64 // side)
        for dy, row in enumerate(small):
            for dx, pixel in enumerate(row):
                alpha = pixel[3] / 255.0
                blended = tuple(
                    round(pixel[i] * alpha + background[i] * (1.0 - alpha))
                    for i in range(3)
                )
                canvas[y + dy][280 + dx] = blended + (255,)
        y += 64 + 8
    return canvas


def dump_previews(directory):
    directory.mkdir(parents=True, exist_ok=True)
    for side in ICO_SIZES:
        (directory / f"icon-{side}.png").write_bytes(png_bytes(side, render("active", side)))
    for state in ("idle", "error"):
        (directory / f"tray-{state}-32.png").write_bytes(
            png_bytes(32, render(state, 32)))
    for name, background in (("light", (0xF3, 0xF3, 0xF3)),
                             ("dark", (0x20, 0x20, 0x20))):
        (directory / f"preview-{name}.png").write_bytes(
            png_bytes(568, contact_sheet(background)))
    print(f"wrote previews to {directory}")


def main(argv):
    root = pathlib.Path(__file__).resolve().parent.parent

    # --dump takes a value, so peel it off before reading the positional ICO path.
    dump = None
    positional = []
    rest = list(argv[1:])
    while rest:
        arg = rest.pop(0)
        if arg == "--dump":
            if not rest:
                raise SystemExit("--dump needs a directory")
            dump = pathlib.Path(rest.pop(0))
        elif arg.startswith("--"):
            raise SystemExit(f"unknown option: {arg}")
        else:
            positional.append(arg)

    ico = pathlib.Path(positional[0]) if positional else root / "src" / "PTMonitor.App" / "app.ico"

    entries = []
    for side in ICO_SIZES:
        rows = render("active", side)
        data = bmp_bytes(side, rows) if side in BMP_SIZES else png_bytes(side, rows)
        entries.append((side, data))
    write_ico(entries, ico)

    if dump is not None:
        dump_previews(dump)


if __name__ == "__main__":
    main(sys.argv)
