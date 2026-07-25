"""Flatten the parchment rules-band texture in the shipped card frames.

Sam 2026-07-25: the band's grain fights the rules text — knock the texture
mostly flat (not entirely) so dark ink reads. Sources stay Sam's untouched
GIMP cuts in art-bakeoff/frames/; this script derives the shipped
Assets/Art/UI/Frames/frame_{rarity}.png, so a frame re-cut just re-runs it.

Method: inside the band (below the alpha-measured art window, above the stat
bosses), light parchment-like pixels blend toward a heavy gaussian blur of
themselves — grain vanishes, carved dark ornament stays crisp (excluded by
the luminance mask).

Usage: python flatten_parchment.py [k]   (k = flatten strength 0..1, default 0.65)
"""
import pathlib
import sys

from PIL import Image, ImageFilter

REPO = pathlib.Path(r"c:\Users\User\Documents\tcg-hearthlike")
SRC = REPO / "art-bakeoff" / "frames"
DST = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "Frames"

FRAMES = {
    "common": "CarvedStone",
    "rare": "DarkGilt",
    "epic": "IronEmber",
    "legendary": "AgedGilt",
}

K = float(sys.argv[1]) if len(sys.argv) > 1 else 0.65
BLUR_RADIUS = 10
LUMA_FLOOR = 70          # darker pixels = carved ornament/metal, left crisp
BAND_TOP_GAP = 0.010     # below the art window's bottom rail
BAND_BOTTOM = 0.875      # above the stat bosses (dark metal is masked out anyway)
BAND_X_INSET = 0.055     # inside the side rails

for rarity, frame in FRAMES.items():
    im = Image.open(SRC / f"Frame_{frame}_cut.png").convert("RGBA")
    w, h = im.size
    alpha = im.getchannel("A")
    wb = alpha.point(lambda v: 255 if v < 128 else 0).getbbox()[3]  # window bottom

    x0, x1 = int(BAND_X_INSET * w), int(w - BAND_X_INSET * w)
    y0, y1 = wb + int(BAND_TOP_GAP * h), int(BAND_BOTTOM * h)

    band = im.crop((x0, y0, x1, y1))
    flat = Image.blend(band, band.filter(ImageFilter.GaussianBlur(BLUR_RADIUS)), K)
    mask = band.convert("L").point(lambda v: 255 if v > LUMA_FLOOR else 0)
    im.paste(Image.composite(flat, band, mask), (x0, y0))

    out = DST / f"frame_{rarity}.png"
    im.save(out)
    print(f"{frame} -> {out.name}: band y {y0}-{y1}, k={K}")
