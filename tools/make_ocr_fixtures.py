#!/usr/bin/env python3
"""Generates synthetic Tarkov-style tooltip captures for OCR tests (FIXTURE KIND: synthetic rendered-font captures).

Each fixture is a 670x360 (x DPI scale) capture with the cursor at (245,125)*scale, an inventory-like
background, and a near-black, gray-framed tooltip with white title text rendered in DejaVu Sans.
Output: tests/fixtures/ocr/<name>.ppm.gz plus manifest.tsv (file, scale, expected item id, lines).
"""
import gzip, io, os, random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "ocr")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

CASES = [
    ("m882", "64b7bbb74b75259c590fa897", ["9x19mm FMJ M882"]),
    ("pab9", "61962d879bb3d20b0946d385", ["9x39mm PAB-9 gs"]),
    ("vpo209", "59e6687d86f77411d949b251", ["Molot Arms VPO-209 .366 TKM carbine"]),
    ("pmag20", "64b9cf0ac12b9c38db26923a", ["AK 7.62x39 Magpul PMAG 20 GEN M3 20-round magazine"]),
    ("tv109", "59e7643b86f7742cbf2c109a", ["WARTECH TV-109 + TV-106 chest rig (A-TACS FG)"]),
    ("rpk95", "5bed625c0db834001c062946", ["RPK-16 5.45x39 95-round drum magazine"]),
    ("6l31", "55d482194bdc2d1d4e8b456b", ["AK-74 5.45x39 6L31 60-round magazine"]),
    ("mag5_60_wrapped", "544a37c44bdc2d25388b4567", ["AR-15 5.56x45 SureFire MAG5-60 STANAG", "60-round magazine"]),
    ("tango6t", "6567e7681265c8a131069b0f", ["SIG Sauer TANGO6T 1-6x24 30mm riflescope"]),
]

def inventory(w, h, s, rng):
    img = Image.new("RGB", (w, h), (34, 35, 34))
    d = ImageDraw.Draw(img)
    cell = int(64 * s)
    small = ImageFont.truetype(FONT, max(8, int(10 * s)))
    for cy in range(0, h, cell):
        for cx in range(0, w, cell):
            k = rng.randint(0, 5)
            if k == 0:
                d.rectangle([cx + 1, cy + 1, cx + cell - 1, cy + cell - 1], fill=(12, 12, 12))
            elif k <= 3:
                d.rectangle([cx + int(10 * s), cy + int(14 * s), cx + cell - int(10 * s), cy + cell - int(12 * s)],
                            fill=(rng.randint(60, 140), rng.randint(60, 140), rng.randint(50, 110)))
                d.text((cx + cell - int(26 * s), cy + int(2 * s)), rng.choice(["M882", "PAB", "TV", "6L31"]), font=small, fill=(230, 230, 230))
                if k == 3:
                    d.text((cx + cell - int(16 * s), cy + cell - int(13 * s)), str(rng.randint(2, 60)), font=small, fill=(220, 220, 220))
            d.rectangle([cx, cy, cx + cell, cy + cell], outline=(62, 64, 62))
    return img

def tooltip(img, x, y, lines, s):
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype(FONT, int(round(13 * s)))
    pad_x, pad_y, gap = int(7 * s), int(5 * s), int(2 * s)
    widths = [d.textlength(l, font=font) for l in lines]
    asc, desc = font.getmetrics()
    lh = asc + desc
    w = int(max(widths)) + 2 * pad_x
    h = len(lines) * lh + (len(lines) - 1) * gap + 2 * pad_y
    b = max(1, int(round(s)))
    d.rectangle([x - b, y - b, x + w - 1 + b, y + h - 1 + b], fill=(118, 120, 118))
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=(9, 10, 10))
    for i, l in enumerate(lines):
        d.text((x + pad_x, y + pad_y + i * (lh + gap)), l, font=font, fill=(236, 236, 236))
    return (x, y, w, h)

def main():
    os.makedirs(OUT, exist_ok=True)
    rows = []
    for s in (1.0, 1.5):
        for i, (name, iid, lines) in enumerate(CASES):
            rng = random.Random(100 + i)
            W, H = int(670 * s), int(360 * s)
            img = inventory(W, H, s, rng)
            cx, cy = int(245 * s), int(125 * s)
            tooltip(img, cx + int(14 * s), cy + int(20 * s), lines, s)
            fn = f"{name}_{int(s * 100)}.ppm.gz"
            buf = io.BytesIO(); img.save(buf, format="PPM")
            with gzip.open(os.path.join(OUT, fn), "wb", compresslevel=9) as g: g.write(buf.getvalue())
            rows.append(f"{fn}\t{s}\t{iid}\t{' | '.join(lines)}")
    with open(os.path.join(OUT, "manifest.tsv"), "w") as f:
        f.write("\n".join(rows) + "\n")

if __name__ == "__main__":
    main()
