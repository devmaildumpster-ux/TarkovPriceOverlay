#!/usr/bin/env python3
"""Synthetic Tarkov TRADER SELL SCREEN captures (FIXTURE KIND: synthetic rendered-font captures).

Reproduces a user's 1.0.3 bug report layout: stash panel with ~63 px cells, white price tags ("₽ 7578") at item
top-lefts, check-mark icons at some cells' bottom-right, near-black empty cells, and Tarkov's trader-screen
tooltip: ~#1a1a1a box with a thin grey frame whose top-left sits at the pointer tip (box opens right, barely
down), line 1 = item name (light grey/white), line 2 = red "The trader cannot buy this item." or a price.
A small white mouse arrow is drawn at the pointer tip, as some capture setups include it.

Capture = 670x360 (x scale) with the cursor at (245,125)*scale, like the app's first capture; the
"left" case is a 1060x360 wide capture with the cursor centred. Output: tests/fixtures/trader/*.ppm.gz and
manifest.tsv: file, scale, cursorX, cursorY, expectedId, expectedLines(name), stackCount (-1 none, -2 hidden)
"""
import gzip, io, os, random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "trader")
REG = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
ULACH = ("5b40e1525acfc4771e1c6611", "HighCom Striker ULACH IIIA helmet (Black)")
BP = ("56dfef82d2720bbd668b4567", "5.45x39mm BP gs")
NF = ("5aa66be6e5b5b0214e506e97", "Nightforce ATACR 7-35x56 34mm riflescope")
RED, GREY, NAME, GOLD = (224, 16, 16), (196, 196, 196), (222, 222, 222), (212, 176, 0)

def stash(W, H, P, ox, oy, rng, d, s, skip, tags=None):
    """Grid aligned so a cell starts at (ox, oy). Cells in `skip` (col,row offsets from ox,oy) are left for the caller."""
    tag = ImageFont.truetype(BOLD, max(8, int(round(9 * s))))
    gx0, gy0 = ox % P, oy % P
    for y0 in range(gy0 - P, H, P):
        for x0 in range(gx0 - P, W, P):
            key = ((x0 - ox) // P, (y0 - oy) // P)
            if key in skip: continue
            k = rng.random()
            if tags and key in tags: k = 1.0
            if k < 0.3:
                d.rectangle([x0 + 1, y0 + 1, x0 + P - 1, y0 + P - 1], fill=(20, 21, 21))   # empty, near-black
                continue
            d.rectangle([x0 + 1, y0 + 1, x0 + P - 1, y0 + P - 1], fill=(rng.randint(40, 80), rng.randint(40, 80), rng.randint(36, 70)))
            a = x0 + rng.randint(8, 20); b = y0 + rng.randint(18, 26)
            d.rectangle([a, b, a + rng.randint(18, P - 26), b + rng.randint(10, P - 34)], fill=(rng.randint(90, 160), rng.randint(80, 150), rng.randint(60, 120)))
            price_tag(d, x0, y0, (tags or {}).get(key, f"₽ {rng.randint(900, 99999)}"), tag, s)
            if rng.random() < 0.4: check(d, x0 + P - int(13 * s), y0 + P - int(12 * s), s, white=rng.random() < 0.5)
    for x in range(gx0, W, P): d.line([x, 0, x, H], fill=(74, 76, 74))
    for y in range(gy0, H, P): d.line([0, y, W, y], fill=(74, 76, 74))

def price_tag(d, x0, y0, text, font, s):
    w = int(d.textlength(text, font=font)) + int(6 * s)
    d.rectangle([x0 + 2, y0 + 2, x0 + 2 + w, y0 + 2 + int(12 * s)], fill=(14, 14, 14))
    d.text((x0 + 2 + int(3 * s), y0 + 2 + int(1 * s)), text, font=font, fill=(236, 236, 236))

def check(d, x, y, s, white):
    c = (232, 232, 232) if white else (70, 200, 80)
    wdt = max(2, int(round(2 * s)))
    d.line([x, y + int(5 * s), x + int(3 * s), y + int(8 * s), x + int(9 * s), y], fill=c, width=wdt)

def arrow(d, x, y, s):
    pts = [(x, y), (x, y + int(16 * s)), (x + int(4 * s), y + int(12 * s)), (x + int(7 * s), y + int(18 * s)),
           (x + int(9 * s), y + int(17 * s)), (x + int(6 * s), y + int(11 * s)), (x + int(11 * s), y + int(11 * s))]
    d.polygon(pts, fill=(245, 245, 245), outline=(0, 0, 0))

def tooltip(d, lines, s, anchor, cx, cy, colors):
    font = ImageFont.truetype(REG, int(round(13 * s)))
    padx, pady, gap = int(6 * s), int(4 * s), int(2 * s)
    asc, desc = font.getmetrics(); lh = asc + desc
    w = int(max(d.textlength(l, font=font) for l in lines)) + 2 * padx
    h = len(lines) * lh + (len(lines) - 1) * gap + 2 * pady
    if anchor == "tip": x, y = cx + 1, cy + 2                  # opens right, barely down (trader screen)
    elif anchor == "above": x, y = cx + 2, cy - h - 3         # above-right of the tip
    else: x, y = cx - w - 3, cy + 2                           # opens left (right screen edge)
    b = max(1, int(round(s)))
    d.rectangle([x - b, y - b, x + w - 1 + b, y + h - 1 + b], fill=(92, 94, 92))
    d.rectangle([x, y, x + w - 1, y + h - 1], fill=(26, 26, 26))
    for i, (l, c) in enumerate(zip(lines, colors)):
        d.text((x + padx, y + pady + i * (lh + gap)), l, font=font, fill=c)
    return (x, y, w, h)

def helmet_scene(s, seed, lines, colors, anchor, wide=False, tags=None):
    rng = random.Random(seed)
    W, H = (int(1060 * s) if wide else int(670 * s)), int(360 * s)
    P = int(round(63 * s))
    cx, cy = (W // 2, int(125 * s)) if wide else (int(245 * s), int(125 * s))
    img = Image.new("RGB", (W, H), (24, 25, 24)); d = ImageDraw.Draw(img)
    # 2x2 helmet: cursor in its top-right cell, ~73,35 px from the item's top-left at 100% (as in the report)
    ix, iy = cx - int(73 * s), cy - int(35 * s)
    stash(W, H, P, ix, iy, rng, d, s, skip={(0, 0), (1, 0), (0, 1), (1, 1)}, tags=tags)
    d.rectangle([ix + 1, iy + 1, ix + 2 * P - 1, iy + 2 * P - 1], fill=(48, 50, 46))
    d.ellipse([ix + int(14 * s), iy + int(20 * s), ix + 2 * P - int(14 * s), iy + 2 * P - int(10 * s)], fill=(30, 31, 30), outline=(70, 72, 66))
    price_tag(d, ix, iy, "₽ 7578", ImageFont.truetype(BOLD, max(8, int(round(9 * s)))), s)
    for x in (ix, ix + P, ix + 2 * P): d.line([x, iy, x, iy + 2 * P], fill=(74, 76, 74)) if x != ix + P else None
    for y in (iy, iy + 2 * P): d.line([ix, y, ix + 2 * P, y], fill=(74, 76, 74))
    box = tooltip(d, lines, s, anchor, cx, cy, colors)
    arrow(d, cx, cy, s)
    return img, (cx, cy), box

def ammo_scene(s, seed, count, fy, with_tooltip):
    rng = random.Random(seed)
    W, H = int(670 * s), int(360 * s)
    P = int(round(63 * s))
    cx, cy = int(245 * s), int(125 * s)
    ox, oy = cx - int(0.5 * P), cy - int(fy * P)
    img = Image.new("RGB", (W, H), (24, 25, 24)); d = ImageDraw.Draw(img)
    stash(W, H, P, ox, oy, rng, d, s, skip={(0, 0)})
    d.rectangle([ox + 1, oy + 1, ox + P - 1, oy + P - 1], fill=(62, 58, 44))
    for _ in range(3):
        a = ox + rng.randint(6, P // 2); b = oy + rng.randint(P // 4, P // 2)
        d.ellipse([a, b, a + P // 4, b + P // 6], fill=(rng.randint(160, 210), rng.randint(130, 170), 60))
    price_tag(d, ox, oy, "₽ 1250", ImageFont.truetype(BOLD, max(8, int(round(9 * s)))), s)
    cf = ImageFont.truetype(BOLD, int(round(11 * s)))
    d.text((ox + P - int(3 * s), oy + P - int(2 * s)), str(count), font=cf, fill=(236, 236, 236), anchor="rd", stroke_width=1, stroke_fill=(0, 0, 0))
    d.line([ox, oy, ox + P, oy], fill=(74, 76, 74)); d.line([ox, oy + P, ox + P, oy + P], fill=(74, 76, 74))
    d.line([ox, oy, ox, oy + P], fill=(74, 76, 74)); d.line([ox + P, oy, ox + P, oy + P], fill=(74, 76, 74))
    box = tooltip(d, [BP[1], "₽ 98"], s, "tip", cx, cy, [NAME, GREY]) if with_tooltip else None
    return img, (cx, cy), box

def save(img, fn):
    buf = io.BytesIO(); img.save(buf, format="PPM")
    with gzip.open(os.path.join(OUT, fn), "wb", compresslevel=9) as g: g.write(buf.getvalue())

def main():
    os.makedirs(OUT, exist_ok=True)
    rows = []
    for s in (1.0, 1.5):
        tag = int(s * 100)
        for name, lines, colors, anchor, wide in [
            ("ulach_cannot", [ULACH[1], "The trader cannot buy this item."], [NAME, RED], "tip", False),
            ("ulach_price", [ULACH[1], "₽ 12 450"], [NAME, GREY], "tip", False),
            ("ulach_above", [ULACH[1], "The trader cannot buy this item."], [NAME, RED], "above", False),
            ("ulach_left_wide", [ULACH[1], "The trader cannot buy this item."], [NAME, RED], "left", True),
        ]:
            img, (cx, cy), box = helmet_scene(s, 4000 + len(rows), lines, colors, anchor, wide)
            fn = f"{name}_{tag}.ppm.gz"; save(img, fn)
            rows.append("\t".join(map(str, [fn, s, cx, cy, ULACH[0], 1, -1, *box])))
        # second report: sellable item, gold offer line, neighbour cells (under/next to the box) with price tags
        img, (cx, cy), box = helmet_scene(s, 4500 + tag, [NF[1], "₽ 35 118"], [NAME, GOLD], "tip",
                                          tags={(2, 1): "₽ 142 317", (3, 1): "₽ 27 440", (2, 0): "₽ 142 317", (3, 0): "₽ 27 440", (4, 1): "₽ 9 870"})
        fn = f"nightforce_offer_{tag}.ppm.gz"; save(img, fn)
        rows.append("\t".join(map(str, [fn, s, cx, cy, NF[0], 1, -1, *box])))
        # ammo: cursor near the top of the cell -> count visible below the tooltip
        img, (cx, cy), box = ammo_scene(s, 5000 + tag, 43, 0.07, True)
        fn = f"bp_top_{tag}.ppm.gz"; save(img, fn); rows.append("\t".join(map(str, [fn, s, cx, cy, BP[0], 1, 43, *box])))
        # ammo: cursor mid-cell -> tooltip covers the count; the pre-tooltip frame still has it
        img, (cx, cy), box = ammo_scene(s, 6000 + tag, 120, 0.55, True)
        fn = f"bp_mid_{tag}.ppm.gz"; save(img, fn); rows.append("\t".join(map(str, [fn, s, cx, cy, BP[0], 1, -2, *box])))
        img, (cx, cy), _ = ammo_scene(s, 6000 + tag, 120, 0.55, False)
        fn = f"bp_mid_pre_{tag}.ppm.gz"; save(img, fn); rows.append("\t".join(map(str, [fn, s, cx, cy, "-", 0, 120, 0, 0, 0, 0])))
        # no tooltip at all: price tags, check marks and empty cells must not look like a tooltip
        img, (cx, cy), _ = ammo_scene(s, 7000 + tag, 7, 0.4, False)
        fn = f"no_tooltip_{tag}.ppm.gz"; save(img, fn); rows.append("\t".join(map(str, [fn, s, cx, cy, "-", 0, -1, 0, 0, 0, 0])))
    with open(os.path.join(OUT, "manifest.tsv"), "w") as f:
        f.write("# file\tscale\tcursorX\tcursorY\texpectedId\tnameLines\tstackCount(-1 n/a,-2 hidden)\ttooltip x\ty\tw\th\n")
        f.write("\n".join(rows) + "\n")

if __name__ == "__main__":
    main()
