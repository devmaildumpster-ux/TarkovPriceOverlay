#!/usr/bin/env python3
"""Synthetic Tarkov-style stash captures for stack-count tests (FIXTURE KIND: synthetic rendered-font cells).

Each fixture is a 670x360 (x scale) hover capture with the cursor at (245,125)*scale, a periodic inventory grid
(pitch 64*scale, random phase), filled cells with icon-like blocks and white short names (top-right), neighbour
cells with their own counts, the hovered cell's count drawn small, white, black-outlined at its bottom-right, and
the name tooltip below-right of the cursor. Output: tests/fixtures/stack/<name>.ppm.gz + manifest.tsv
(file, scale, screenHeight, cursorX, cursorY, tooltip interior x,y,w,h, expected count or -1 = hidden).
"""
import gzip, io, os, random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "stack")
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
REG = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

def make(s, count, seed, covered=False):
    rng = random.Random(seed)
    W, H = int(670 * s), int(360 * s)
    P = int(round(64 * s))
    img = Image.new("RGB", (W, H), (22, 23, 22))
    d = ImageDraw.Draw(img)
    cnt_font = ImageFont.truetype(BOLD, int(round(11 * s)))
    name_font = ImageFont.truetype(REG, max(8, int(round(9 * s))))
    cx, cy = int(245 * s), int(125 * s)
    # Position the grid so the cursor sits at a chosen spot inside its cell.
    fx, fy = (0.30, 0.30) if covered else (rng.uniform(0.78, 0.9), rng.uniform(0.74, 0.86))
    ox, oy = cx - int(fx * P), cy - int(fy * P)       # hovered cell origin
    gx0, gy0 = ox % P, oy % P
    for y0 in range(gy0 - P, H, P):
        for x0 in range(gx0 - P, W, P):
            hovered = (x0 == ox and y0 == oy)
            if hovered or rng.random() < 0.6:
                base = (rng.randint(45, 90), rng.randint(45, 90), rng.randint(40, 80))
                d.rectangle([x0 + 1, y0 + 1, x0 + P - 1, y0 + P - 1], fill=base)
                # icon-ish shapes, including brass/bright highlights
                for _ in range(3):
                    a = rng.randint(x0 + 4, x0 + P // 2); b = rng.randint(y0 + P // 4, y0 + P // 2)
                    d.ellipse([a, b, a + rng.randint(6, P // 3), b + rng.randint(4, P // 4)],
                              fill=(rng.randint(150, 220), rng.randint(120, 190), rng.randint(40, 90)))
                d.text((x0 + P - int(3 * s), y0 + int(2 * s)), rng.choice(["PS", "BP", "M855", "7N1", "FMJ"]),
                       font=name_font, fill=(225, 225, 225), anchor="ra")
                n = str(count) if hovered else (str(rng.randint(2, 99)) if rng.random() < 0.7 else "")
                if n:
                    d.text((x0 + P - int(3 * s), y0 + P - int(2 * s)), n, font=cnt_font, fill=(236, 236, 236),
                           anchor="rd", stroke_width=1, stroke_fill=(0, 0, 0))
    for x in range(gx0, W, P): d.line([x, 0, x, H], fill=(74, 76, 74))
    for y in range(gy0, H, P): d.line([0, y, W, y], fill=(74, 76, 74))
    # tooltip below-right of the cursor
    tx, ty = cx + int(14 * s), cy + int(20 * s)
    font = ImageFont.truetype(REG, int(round(13 * s)))
    text = "5.45x39mm BP gs"
    tw = int(d.textlength(text, font=font)) + int(14 * s); th = int(26 * s)
    b = max(1, int(round(s)))
    d.rectangle([tx - b, ty - b, tx + tw - 1 + b, ty + th - 1 + b], fill=(118, 120, 118))
    d.rectangle([tx, ty, tx + tw - 1, ty + th - 1], fill=(9, 10, 10))
    d.text((tx + int(7 * s), ty + int(5 * s)), text, font=font, fill=(236, 236, 236))
    return img, (cx, cy), (tx, ty, tw, th), int(round(1080 * s))

def main():
    os.makedirs(OUT, exist_ok=True)
    rows = []
    for s in (1.0, 1.25, 1.5):
        cases = [(c, False) for c in (70, 43, 7, 120, 318)] + [(60, True)]
        for i, (count, covered) in enumerate(cases):
            img, (cx, cy), (tx, ty, tw, th), sh = make(s, count, 1000 + i * 7 + int(s * 100), covered)
            fn = f"stack_{'hidden' if covered else count}_{int(s * 100)}.ppm.gz"
            buf = io.BytesIO(); img.save(buf, format="PPM")
            with gzip.open(os.path.join(OUT, fn), "wb", compresslevel=9) as g: g.write(buf.getvalue())
            rows.append("\t".join(map(str, [fn, s, sh, cx, cy, tx, ty, tw, th, -1 if covered else count])))
    with open(os.path.join(OUT, "manifest.tsv"), "w") as f: f.write("\n".join(rows) + "\n")

if __name__ == "__main__":
    main()
