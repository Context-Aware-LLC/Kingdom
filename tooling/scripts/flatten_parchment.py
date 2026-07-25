"""Flatten the parchment rules-band texture in the shipped card frames.

Sam 2026-07-25: the band's grain fights the rules text — knock the texture
mostly flat (not entirely) so dark ink reads. Sources stay Sam's untouched
GIMP cuts in art-bakeoff/frames/; this script derives the shipped
Assets/Art/UI/Frames/frame_{rarity}.png, so a frame re-cut just re-runs it.

Method: inside the band (below the alpha-measured art window, above the stat
bosses), light parchment-like pixels blend toward a heavy gaussian blur of
themselves — grain vanishes, carved dark ornament stays crisp (excluded by
the luminance mask).

Usage: python flatten_parchment.py [k] [brighten]
    k        = flatten strength 0..1 (default 0.85 — Sam-approved 2026-07-25)
    brighten = parchment brightness multiplier (default 1.12; >1 lifts the paper
               for more contrast under the dark rules ink — mobile legibility)
"""
import pathlib
import sys

from PIL import Image, ImageChops, ImageEnhance, ImageFilter

REPO = pathlib.Path(r"c:\Users\User\Documents\tcg-hearthlike")
SRC = REPO / "art-bakeoff" / "frames"
DST = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "Frames"

FRAMES = {
    "common": "CarvedStone",
    "rare": "DarkGilt",
    "epic": "IronEmber",
    "legendary": "AgedGilt",
}

K = float(sys.argv[1]) if len(sys.argv) > 1 else 0.85
BRIGHTEN = float(sys.argv[2]) if len(sys.argv) > 2 else 1.12
BLUR_RADIUS = 10
LUMA_FLOOR = 70          # darker pixels = carved ornament/metal, left crisp
BAND_TOP_GAP = 0.010     # below the art window's bottom rail
BAND_BOTTOM = 0.875      # above the stat bosses (dark metal is masked out anyway)
BAND_X_INSET = 0.055     # inside the side rails

# Sam 2026-07-25: DarkGilt's and IronEmber's painted beds are too dark for the
# rules ink — retint them to CarvedStone's cream (measured, not guessed). Their
# beds sit below LUMA_FLOOR, so the mask floor drops to keep the bed in scope
# while true black ornament stays crisp.
RETINT = {"rare", "epic"}
RETINT_LUMA_FLOOR = 40

for rarity, frame in FRAMES.items():
    im = Image.open(SRC / f"Frame_{frame}_cut.png").convert("RGBA")
    w, h = im.size
    alpha = im.getchannel("A")
    wb = alpha.point(lambda v: 255 if v < 128 else 0).getbbox()[3]  # window bottom

    x0, x1 = int(BAND_X_INSET * w), int(w - BAND_X_INSET * w)
    y0, y1 = wb + int(BAND_TOP_GAP * h), int(BAND_BOTTOM * h)

    band = im.crop((x0, y0, x1, y1))
    flat = Image.blend(band, band.filter(ImageFilter.GaussianBlur(BLUR_RADIUS)), K)
    if BRIGHTEN != 1.0:
        flat = ImageEnhance.Brightness(flat).enhance(BRIGHTEN)

    floor = RETINT_LUMA_FLOOR if rarity in RETINT else LUMA_FLOOR
    mask = flat.convert("L").point(lambda v, f=floor: 255 if v > f else 0)
    # Feather: a blurred mask kills the ragged bed/ornament frontier, and a vertical
    # fade-out over the band's last 20% hides the seam where the painted bed keeps
    # running darker behind the boss row.
    mask = mask.filter(ImageFilter.GaussianBlur(8))
    bw, bh = mask.size
    fade_start = int(bh * 0.80)
    grad = Image.new("L", (1, bh), 255)
    for yy in range(fade_start, bh):
        grad.putpixel((0, yy), max(0, round(255 * (bh - yy) / (bh - fade_start))))
    mask = ImageChops.multiply(mask, grad.resize((bw, bh)))

    def bed_mean(img, m):
        px = img.convert("RGB").load()
        mp = m.load()
        w2, h2 = img.size
        tot = [0, 0, 0]; n = 0
        for yy in range(0, h2, 4):
            for xx in range(0, w2, 4):
                if mp[xx, yy]:
                    p = px[xx, yy]
                    tot[0] += p[0]; tot[1] += p[1]; tot[2] += p[2]; n += 1
        return tuple(t / max(n, 1) for t in tot)

    if rarity == "common":
        target = bed_mean(flat, mask)  # dict order: common is processed first
    elif rarity in RETINT:
        mean = bed_mean(flat, mask)
        fr, fg, fb = (min(t / max(m, 1.0), 3.0) for t, m in zip(target, mean))
        r, g, b, a = flat.split()
        retinted = Image.merge("RGBA", (
            r.point(lambda v, f=fr: min(255, round(v * f))),
            g.point(lambda v, f=fg: min(255, round(v * f))),
            b.point(lambda v, f=fb: min(255, round(v * f))), a))
        flat = Image.composite(retinted, flat, mask)

    im.paste(Image.composite(flat, band, mask), (x0, y0))

    out = DST / f"frame_{rarity}.png"
    im.save(out)
    print(f"{frame} -> {out.name}: band y {y0}-{y1}, k={K}, brighten={BRIGHTEN}"
          + (f", retinted to {tuple(round(t) for t in target)}" if rarity in RETINT else ""))
