#!/usr/bin/env python3
"""1.2.4: synthetic wrapped tooltip for "AR-10 KAC QDC 7.62x51 Flash Suppressor Kit" (a user's PvE report).
Same style as make_ocr_fixtures.py but light-grey lettering (Tarkov's tooltip text), wrapped on two lines as in game.
Output: tests/fixtures/qdc/qdc_wrapped_<scale>.ppm.gz (cursor at (245,125)*scale)."""
import gzip, io, os, random, sys
sys.path.insert(0, os.path.dirname(__file__))
from PIL import ImageDraw, ImageFont
import make_ocr_fixtures as m

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "qdc")
LINES = ["AR-10 KAC QDC 7.62x51 Flash", "Suppressor Kit"]

def tooltip(img, x, y, lines, s, fill=(205, 205, 200)):
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype(m.FONT, int(round(13 * s)))
    pad_x, pad_y, gap = int(7 * s), int(5 * s), int(2 * s)
    widths = [d.textlength(l, font=font) for l in lines]
    asc, desc = font.getmetrics(); lh = asc + desc
    w = int(max(widths)) + 2 * pad_x
    h = len(lines) * lh + (len(lines) - 1) * gap + 2 * pad_y
    b = max(1, int(round(s)))
    d.rectangle([x - b, y - b, x + w - 1 + b, y + h - 1 + b], fill=(118, 120, 118))
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=(9, 10, 10))
    for i, l in enumerate(lines):
        d.text((x + pad_x, y + pad_y + i * (lh + gap)), l, font=font, fill=fill)

def main():
    os.makedirs(OUT, exist_ok=True)
    for s in (1.0, 1.5):
        rng = random.Random(424)
        W, H = int(670 * s), int(360 * s)
        img = m.inventory(W, H, s, rng)
        cx, cy = int(245 * s), int(125 * s)
        tooltip(img, cx + int(14 * s), cy + int(20 * s), LINES, s)
        buf = io.BytesIO(); img.save(buf, format="PPM")
        with gzip.open(os.path.join(OUT, f"qdc_wrapped_{int(s * 100)}.ppm.gz"), "wb", compresslevel=9) as g: g.write(buf.getvalue())
        if s == 1.5: img.save(os.path.join(__import__("tempfile").gettempdir(), "qdc-synthetic-150.png"))

if __name__ == "__main__":
    main()
