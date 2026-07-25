"""Compose a full card-front mockup: frame cut + illustration + name/digits/rules text.

Sam nudges 2026-07-25 (units: 1 unit = 1/10 of the rendered digit height):
  - attack/health digits up 1.5 units
  - cost digit right 2 units, down 1.5 units
  - rules text darker + larger (nudge list item 1)
"""
import json
import pathlib

from PIL import Image, ImageDraw, ImageFont, ImageOps

REPO = pathlib.Path(r"c:\Users\User\Documents\tcg-hearthlike")
FRAMES = REPO / "art-bakeoff" / "frames"
ALFA = str(REPO / "unity/Emberfall/Assets/Art/Fonts/AlfaSlabOne-Regular.ttf")

DIGIT_SIZE = 200

fit = json.loads((FRAMES / "Frame_IronEmber_fit.json").read_text())
w, h = fit["size"]
win = fit["art_window"]

# 1 unit = 1/10 of a digit's rendered height
_f = ImageFont.truetype(ALFA, DIGIT_SIZE)
_bb = _f.getbbox("3")
UNIT = (_bb[3] - _bb[1]) / 10.0

# per-boss pixel offsets (x, y), in units
DIGIT_NUDGE = {
    "cost": (2.0 * UNIT, 1.5 * UNIT),
    "attack": (0.0, -1.5 * UNIT),
    "health": (0.0, -1.5 * UNIT),
}

card = Image.new("RGBA", (w, h), (0, 0, 0, 255))

# illustration, cover-fitted to the window
art = Image.open(REPO / "unity/Emberfall/Assets/Art/Cards/CORE/art_bronzegate_warden.png").convert("RGBA")
wl, wt = int(win["l"] * w), int(win["t"] * h)
wr, wb = int(win["r"] * w), int(win["b"] * h)
ww, wh = wr - wl, wb - wt
fitted = ImageOps.fit(art, (ww, wh), Image.LANCZOS, centering=(0.5, 0.35))
card.paste(fitted, (wl, wt))

frame = Image.open(FRAMES / "Frame_IronEmber_cut.png").convert("RGBA")
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
    text_c((b["cx"] * w + dx, b["cy"] * h + dy), val, DIGIT_SIZE,
           (255, 255, 255, 255), stroke=12, stroke_fill=(10, 8, 6, 255))

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

out = FRAMES / "Mock_IronEmber_BronzegateWarden.png"
card.save(out)
print("ok", out, "| unit px =", round(UNIT, 2))
