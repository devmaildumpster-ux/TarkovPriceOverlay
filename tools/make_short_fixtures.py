#!/usr/bin/env python3
"""Synthetic SHORT-NAME tooltips (FIXTURE KIND: synthetic rendered-font captures). 1.0.6 bug report: hovering a
1x1 'Chainlet' on the trader/stash screen gave no card. Tarkov drew a SMALL two-line tooltip (~68x40 px at 100%:
light-grey 'Chainlet', gold '₽ 3 689') ABOVE-right of the pointer, its bottom edge just over the cursor / near
the hovered cell's top, beside white '₽ 3689' price tags. Same 670x360 first capture as the app, cursor at
(245,125)*scale. Also other short names (Bolts, Awl, Wrench, Salewa first aid kit, TP-200 TNT brick), anchors
above/tip, 100% and 150%. Output: tests/fixtures/short/*.ppm.gz + manifest.tsv (same columns as trader/).
"""
import os, random, sys
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(__file__))
import make_trader_fixtures as T

T.OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "short")
LIGHT = (198, 198, 198)
ITEMS = {
    "chainlet": ("573474f924597738002c6174", "Chainlet", "₽ 3 689"),
    "bolts": ("57347c5b245977448d35f6e1", "Bolts", "₽ 6 120"),
    "awl": ("62a0a098de7ac8199358053b", "Awl", "₽ 812"),
    "wrench": ("590c311186f77424d1667482", "Wrench", "₽ 9 540"),
    "salewa": ("544fb45d4bdc2dee738b4568", "Salewa first aid kit", "₽ 8 455"),
    "tp200": ("60391b0fb847c71012789415", "TP-200 TNT brick", "₽ 11 250"),
}

def scene(s, seed, key, anchor, second=True):
    rng = random.Random(seed)
    W, H = int(670 * s), int(360 * s)
    P = int(round(63 * s))
    cx, cy = int(245 * s), int(125 * s)
    # 1x1 item; pointer ~46 px right / 8 px down from the cell's top-left (as in the report)
    ix, iy = cx - int(46 * s), cy - int(8 * s)
    img = Image.new("RGB", (W, H), (24, 25, 24)); d = ImageDraw.Draw(img)
    tags = {(dx, dy): f"₽ {rng.randint(900, 99999)}" for dx in (-1, 0, 1, 2) for dy in (-1, 0, 1)}
    tags.pop((0, 0))
    T.stash(W, H, P, ix, iy, rng, d, s, skip={(0, 0)}, tags=tags)
    d.rectangle([ix + 1, iy + 1, ix + P - 1, iy + P - 1], fill=(56, 54, 46))
    d.ellipse([ix + int(14 * s), iy + int(22 * s), ix + P - int(14 * s), iy + P - int(10 * s)], outline=(214, 180, 60), width=max(2, int(2 * s)))
    _, _, price = ITEMS[key]
    T.price_tag(d, ix, iy, price.replace(" ", "").replace("₽", "₽ "), ImageFont.truetype(T.BOLD, max(8, int(round(9 * s)))), s)
    for x in (ix, ix + P): d.line([x, iy, x, iy + P], fill=(74, 76, 74))
    for y in (iy, iy + P): d.line([ix, y, ix + P, y], fill=(74, 76, 74))
    _, name, price = ITEMS[key]
    lines, colors = ([name, price], [LIGHT, T.GOLD]) if second else ([name], [LIGHT])
    box = T.tooltip(d, lines, s, anchor, cx, cy, colors)
    T.arrow(d, cx, cy, s)
    return img, (cx, cy), box

def main():
    os.makedirs(T.OUT, exist_ok=True)
    rows = []
    for s in (1.0, 1.5):
        tag = int(s * 100)
        cases = [("chainlet", "above", True), ("chainlet", "tip", True), ("chainlet", "above", False),
                 ("bolts", "above", True), ("awl", "above", True), ("wrench", "above", True),
                 ("salewa", "above", True), ("tp200", "above", True), ("bolts", "tip", False)]
        for key, anchor, second in cases:
            img, (cx, cy), box = scene(s, 9000 + tag + len(rows), key, anchor, second)
            fn = f"{key}_{anchor}{'' if second else '_1line'}_{tag}.ppm.gz"; T.save(img, fn)
            rows.append("\t".join(map(str, [fn, s, cx, cy, ITEMS[key][0], 1, -1, *box])))
            if key == "chainlet" and anchor == "above" and second:
                print(f"{fn}: tooltip box (interior) {box}")
    with open(os.path.join(T.OUT, "manifest.tsv"), "w") as f:
        f.write("# file\tscale\tcursorX\tcursorY\texpectedId\tnameLines\tstackCount(-1 n/a,-2 hidden)\ttooltip x\ty\tw\th\n")
        f.write("\n".join(rows) + "\n")

if __name__ == "__main__":
    main()
