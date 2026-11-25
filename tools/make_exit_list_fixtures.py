#!/usr/bin/env python3
"""Generates synthetic 1920x1080 top-right exit-list captures for Shoreline OCR tests.

Matches the 38% x 45% capture region (729x486 at 1080p) with green header, timed V-Ex row,
white/green/orange text, right-column timers ("0:00:28", "?????"), and outdoor sky + treeline
or dark indoor backgrounds.
Output: tests/fixtures/exit-list/<name>.ppm.gz + manifest.tsv.
"""
import gzip, io, os, random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "fixtures", "exit-list")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def make_scene(mode: str) -> Image.Image:
    W, H = 729, 486
    img = Image.new("RGB", (W, H))
    px = img.load()
    rng = random.Random(1337)
    if mode == "dark":
        for y in range(H):
            for x in range(W):
                v = 28 + rng.randint(-6, 6)
                px[x, y] = (v, v + 2, v)
    else:
        for y in range(H):
            for x in range(W):
                if y < 230:
                    v = 185 + int(45 * (y / 230.0)) + rng.randint(-8, 8)
                    px[x, y] = (min(255, v - 10), min(255, v), min(255, v + 10))
                else:
                    g = 45 + rng.randint(-15, 25)
                    px[x, y] = (g - 5, g + 8, g - 10)

    img = img.convert("RGBA")
    overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)

    f_hdr = ImageFont.truetype(FONT_BOLD, 12)
    f_row = ImageFont.truetype(FONT_BOLD, 12)
    f_tag = ImageFont.truetype(FONT_BOLD, 10)
    f_sub = ImageFont.truetype(FONT, 10)

    x0, x1 = W - 360, W - 12
    y = 20
    a = 215 if mode == "dark" else 155
    d.rectangle([x0, y, x1, y + 22], fill=(15, 35, 15, 200))
    d.text((x0 + 8, y + 4), "Stay in the extraction point", font=f_hdr, fill=(135, 225, 95, 255))
    d.text((x1 - 56, y + 4), "0:34:07", font=f_hdr, fill=(135, 225, 95, 255))
    y += 25

    if mode == "sky":
        ex1_fg, ex1_bg = (130, 225, 95, 255), (16, 18, 20, a)
    elif mode == "green_bar":
        ex1_fg, ex1_bg = (235, 235, 235, 255), (30, 95, 30, 175)
    else:
        ex1_fg, ex1_bg = (235, 235, 235, 255), (16, 18, 20, a)

    rows = [
        ("EXFIL01", "Road to North V-Ex", "0:00:28", ex1_fg, ex1_bg),
        ("EXFIL02", "Road to Customs", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("EXFIL03", "Railway Bridge", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("EXFIL04", "Path to Lighthouse", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("EXFIL05", "Smugglers' Path (Co-op)", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("EXFIL06", "Pier Boat", "?????", (235, 235, 235, 255), (16, 18, 20, a)),
        ("EXFIL07", "Climber's Trail", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("TRANSIT01", "Transit to Lighthouse", "", (235, 235, 235, 255), (16, 18, 20, a)),
        ("TRANSIT04", "Transit to Icebreaker", "", (235, 145, 55, 255), (16, 18, 20, a)),
    ]
    for tag, name, right, fg, bg in rows:
        rh = 22 if tag != "TRANSIT04" else 36
        d.rectangle([x0, y, x1, y + rh], fill=bg)
        d.text((x0 + 8, y + 5), tag, font=f_tag, fill=(180, 180, 180, 255) if bg[:3] == (16, 18, 20) else fg)
        d.text((x0 + 76, y + 4), name, font=f_row, fill=fg)
        if right:
            d.text((x1 - 54, y + 4), right, font=f_row, fill=fg)
        if tag == "TRANSIT04":
            d.text((x0 + 76, y + 20), "Ssssh-Tuuuu tool set required", font=f_sub, fill=(205, 135, 65, 255))
        y += rh + 2

    return Image.alpha_composite(img, overlay).convert("RGB")


def main():
    os.makedirs(OUT, exist_ok=True)
    cases = [
        ("shoreline_vex_sky_100.ppm.gz", "sky"),
        ("shoreline_vex_green_bar_100.ppm.gz", "green_bar"),
        ("shoreline_vex_dark_100.ppm.gz", "dark"),
    ]
    manifest = []
    for fn, mode in cases:
        im = make_scene(mode)
        buf = io.BytesIO()
        im.save(buf, format="PPM")
        with gzip.open(os.path.join(OUT, fn), "wb", compresslevel=9) as g:
            g.write(buf.getvalue())
        manifest.append(f"{fn}\t{mode}\tRoad to North V-Ex|Road to Customs|Railway Bridge|Path to Lighthouse|Smugglers' Path (Co-op)|Pier Boat|Climber's Trail|Transit to Lighthouse|Transit to Icebreaker")
    with open(os.path.join(OUT, "manifest.tsv"), "w") as f:
        f.write("\n".join(manifest) + "\n")
    print(f"Wrote {len(cases)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
