#!/usr/bin/env python3
"""Synthetic Tarkov-style stash captures for icon short-name tests (FIXTURE KIND: synthetic rendered-font cells).

Each fixture is a 670x360 (x scale) hover capture with the cursor at (245,125)*scale, a periodic inventory
grid (pitch 64*scale), the hovered cell's short name in white (bottom of the cell; some also at the top),
optional stack count, and a name tooltip below-right of the cursor. Output: tests/fixtures/icon-name/<name>.ppm.gz
+ manifest.tsv (file, scale, screenHeight, cursorX, cursorY, tooltip x,y,w,h, shortName, covered).
"""
import gzip, io, os, random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "icon-name")
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
REG = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

def make(s, short, tooltip, seed, covered=False, top=False, count=None):
    rng = random.Random(seed)
    W, H = int(670 * s), int(360 * s)
    P = int(round(64 * s))
    img = Image.new("RGB", (W, H), (22, 23, 22))
    d = ImageDraw.Draw(img)
    cnt_font = ImageFont.truetype(BOLD, int(round(11 * s)))
    name_font = ImageFont.truetype(BOLD, max(11, int(round(12 * s))))
    cx, cy = int(245 * s), int(125 * s)
    fx, fy = (0.30, 0.30) if covered else (rng.uniform(0.35, 0.65), rng.uniform(0.35, 0.65))
    ox, oy = cx - int(fx * P), cy - int(fy * P)
    gx0, gy0 = ox % P, oy % P
    for y0 in range(gy0 - P, H, P):
        for x0 in range(gx0 - P, W, P):
            hovered = (x0 == ox and y0 == oy)
            if hovered or rng.random() < 0.65:
                base = (rng.randint(45, 90), rng.randint(45, 90), rng.randint(40, 80))
                d.rectangle([x0 + 1, y0 + 1, x0 + P - 1, y0 + P - 1], fill=base)
                for _ in range(3):
                    a = rng.randint(x0 + 4, x0 + P // 2); b = rng.randint(y0 + P // 5, y0 + P // 2)
                    d.ellipse([a, b, a + rng.randint(6, P // 3), b + rng.randint(4, P // 4)],
                              fill=(rng.randint(80, 160), rng.randint(80, 160), rng.randint(40, 90)))
                label = short if hovered else rng.choice(["PS", "BP", "FMJ", "7N1", "HP"])
                # Dark caption strip + white short name along the bottom (left). Fits inside a 64px cell.
                bar_h = int(16 * s)
                d.rectangle([x0 + 2, y0 + P - bar_h - 1, x0 + P - 2, y0 + P - 2], fill=(8, 8, 8))
                d.text((x0 + int(3 * s), y0 + P - int(3 * s)), label, font=name_font,
                       fill=(245, 245, 245), anchor="ld")
                if top and hovered:
                    d.rectangle([x0 + 2, y0 + 2, x0 + P - 2, y0 + bar_h], fill=(8, 8, 8))
                    d.text((x0 + P - int(3 * s), y0 + int(3 * s)), label, font=name_font,
                           fill=(245, 245, 245), anchor="ra")
                n = (str(count) if count is not None else None) if hovered else (str(rng.randint(2, 99)) if rng.random() < 0.5 else None)
                if n:
                    d.text((x0 + P - int(3 * s), y0 + P - int(2 * s)), n, font=cnt_font, fill=(236, 236, 236),
                           anchor="rd", stroke_width=1, stroke_fill=(0, 0, 0))
    for x in range(gx0, W, P): d.line([x, 0, x, H], fill=(74, 76, 74))
    for y in range(gy0, H, P): d.line([0, y, W, y], fill=(74, 76, 74))
    # tooltip below-right of the cursor
    tx, ty = cx + int(14 * s), cy + int(20 * s)
    font = ImageFont.truetype(REG, int(round(13 * s)))
    tw = int(d.textlength(tooltip, font=font)) + int(14 * s); th = int(26 * s)
    if covered:
        # Cover the hovered cell (and its short name) with the tooltip, like a name that opens over the icon.
        tx, ty = ox - int(4 * s), oy - int(4 * s)
        tw, th = P + int(80 * s), P + int(16 * s)
    b = max(1, int(round(s)))
    d.rectangle([tx - b, ty - b, tx + tw - 1 + b, ty + th - 1 + b], fill=(118, 120, 118))
    d.rectangle([tx, ty, tx + tw - 1, ty + th - 1], fill=(9, 10, 10))
    d.text((tx + int(7 * s), ty + int(5 * s)), tooltip, font=font, fill=(236, 236, 236))
    return img, (cx, cy), (tx, ty, tw, th), int(round(1080 * s))

def write(name, img):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    buf = io.BytesIO()
    # PPM via raw — Pillow can save PPM
    img.save(buf, format="PPM")
    with gzip.open(path, "wb") as f:
        f.write(buf.getvalue())

def main():
    os.makedirs(OUT, exist_ok=True)
    rows = []
    cases = [
        # scale, short, tooltip, seed, covered, top, count, filename
        (1.0, "Salewa", "Saleva", 11, False, False, None, "salewa_100.ppm.gz"),
        (1.5, "Salewa", "Saleva", 12, False, False, None, "salewa_150.ppm.gz"),
        (1.0, "Splint", "Splnt", 13, False, True, None, "splint_100.ppm.gz"),
        (1.0, "M995", "M995", 14, False, False, 40, "m995_100.ppm.gz"),
        (1.0, "M855", "M995", 15, False, False, None, "m855_icon_m995_tip_100.ppm.gz"),
        (1.0, "Salewa", "Salewa", 16, True, False, None, "salewa_covered_100.ppm.gz"),
    ]
    for s, short, tip, seed, covered, top, count, fn in cases:
        img, (cx, cy), (tx, ty, tw, th), sh = make(s, short, tip, seed, covered, top, count)
        write(fn, img)
        rows.append(f"{fn}\t{s}\t{sh}\t{cx}\t{cy}\t{tx}\t{ty}\t{tw}\t{th}\t{short}\t{int(covered)}")
        print("wrote", fn, img.size, "short", short, "covered", covered)
    with open(os.path.join(OUT, "manifest.tsv"), "w") as f:
        f.write("file\tscale\tscreenHeight\tcursorX\tcursorY\ttipX\ttipY\ttipW\ttipH\tshortName\tcovered\n")
        f.write("\n".join(rows) + "\n")
    print("manifest", len(rows))

if __name__ == "__main__":
    main()
