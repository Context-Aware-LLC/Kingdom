"""Compose a full card-front mockup: frame cut + illustration + name/digits/rules text.

Usage: python mock_card_front.py [FrameName]   (default IronEmber)

Type spec + digit nudges are the Sam-approved 2026-07-25 IronEmber calibration
(docs techart/card-rendering.md §3.1); digits land on the named frame's own
measured boss centers from its fit JSON.
"""
import json
import pathlib
import sys

from PIL import Image, ImageDraw, ImageFont, ImageOps

REPO = pathlib.Path(r"c:\Users\User\Documents\tcg-hearthlike")
FRAMES = REPO / "art-bakeoff" / "frames"
ALFA = str(REPO / "unity/Emberfall/Assets/Art/Fonts/AlfaSlabOne-Regular.ttf")

DIGIT_SIZE = 200
FRAME = sys.argv[1] if len(sys.argv) > 1 else "IronEmber"

fit = json.loads((FRAMES / f"Frame_{FRAME}_fit.json").read_text())
w, h = fit["size"]
win = fit["art_window"]

# 1 unit = 1/10 of a digit's rendered height
_f = ImageFont.truetype(ALFA, DIGIT_SIZE)
_bb = _f.getbbox("3")
UNIT = (_bb[3] - _bb[1]) / 10.0

# per-frame, per-boss offsets (x, y) in units; IronEmber's are Sam-calibrated,
# the others start from IronEmber's values and get dialed in per frame
NUDGES = {
    "IronEmber": {"cost": (2.0, 1.5), "attack": (0.0, -1.5), "health": (0.0, -1.5)},
    "DarkGilt": {"cost": (-0.374, 0.313), "attack": (0.0, -3.5), "health": (0.0, -3.5)},
    "AgedGilt": {"cost": (2.0, 1.5), "attack": (0.5, -3.0), "health": (-0.5, -4.5)},
    "CarvedStone": {"cost": (1.209, 0.709), "attack": (0.0, -3.0), "health": (0.0, -3.0)},
}
DIGIT_NUDGE = {k: (x * UNIT, y * UNIT) for k, (x, y) in NUDGES[FRAME].items()}

# per-frame digit font sizes (AgedGilt's bosses are smaller: cost -10%, atk/hp -5%)
DIGIT_SIZES = {
    "AgedGilt": {"cost": 180, "attack": 180, "health": 180},
}

card = Image.new("RGBA", (w, h), (0, 0, 0, 255))

# illustration, cover-fitted to the window
art = Image.open(REPO / "unity/Emberfall/Assets/Art/Cards/CORE/art_bronzegate_warden.png").convert("RGBA")
wl, wt = int(win["l"] * w), int(win["t"] * h)
wr, wb = int(win["r"] * w), int(win["b"] * h)
ww, wh = wr - wl, wb - wt
fitted = ImageOps.fit(art, (ww, wh), Image.LANCZOS, centering=(0.5, 0.35))
card.paste(fitted, (wl, wt))

frame = Image.open(FRAMES / f"Frame_{FRAME}_cut.png").convert("RGBA")
card.alpha_composite(frame)

d = ImageDraw.Draw(card)

def text_c(xy, s, size, fill, stroke=0, stroke_fill=None, font_path=ALFA):
    f = ImageFont.truetype(font_path, size)
    d.text(xy, s, font=f, fill=fill, anchor="mm",
           stroke_width=stroke, stroke_fill=stroke_fill)

# name on the plaque (plaque band ~3%..10.5% height)
# centered at 0.56w, but the left edge may never cross NAME_LEFT_CLAMP
# (Sam 2026-07-25: keeps long names off the cost boss)
NAME_LEFT_CLAMP = 0.232 * w
name = "Bronzegate Warden"
f_name = ImageFont.truetype(ALFA, 89)
name_w = f_name.getbbox(name)[2] - f_name.getbbox(name)[0]
name_cx = max(w * 0.56, NAME_LEFT_CLAMP + name_w / 2)
text_c((name_cx, h * 0.068), name, 89, (232, 220, 200, 255))

# digits on the bosses — white with black rim, like the in-game TMP outline
for key, val in (("cost", "3"), ("attack", "3"), ("health", "4")):
    b = fit["bosses"][key]
    dx, dy = DIGIT_NUDGE[key]
    size = DIGIT_SIZES.get(FRAME, {}).get(key, DIGIT_SIZE)
    text_c((b["cx"] * w + dx, b["cy"] * h + dy), val, size,
           (255, 255, 255, 255), stroke=round(12 * size / DIGIT_SIZE),
           stroke_fill=(10, 8, 6, 255))

# rules text on the parchment band (window bottom ~.589 to boss top ~.81)
band_cy = h * 0.70
ink = (28, 18, 12, 255)
# single-weight font: same-color stroke fakes a bolder keyword
f_kw = ImageFont.truetype(ALFA, 97)
d.text((w * 0.5, band_cy - 88), "Taunt", font=f_kw, fill=ink, anchor="mm",
       stroke_width=2, stroke_fill=ink)
f_body = ImageFont.truetype(ALFA, 68)
d.text((w * 0.5, band_cy + 42), "When this survives damage,", font=f_body,
       fill=ink, anchor="mm")
d.text((w * 0.5, band_cy + 128), "it gains +1 Attack.", font=f_body,
       fill=ink, anchor="mm")

out = FRAMES / f"Mock_{FRAME}_BronzegateWarden.png"
card.save(out)
print("ok", out, "| unit px =", round(UNIT, 2))
