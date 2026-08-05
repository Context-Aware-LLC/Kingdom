"""Image-space mock of the versus screen's TEXT tier — the fast loop for type polish.

Placement was settled this way too (project memory `versus-screen-progress`): a PIL
composite is instant, a Unity recompile + domain reload is ~30s. Same trick here, one
tier up — the plate, portraits and orb are already final, so this script rebuilds them
verbatim and only the text is under test.

CALIBRATION — the one thing that makes the numbers transferable. TextMeshPro's
`fontSize` on a world-space text is a POINT value: at scale 1 the em box is
`fontSize / 10` world units, NOT `fontSize` (measured off versus_FINAL_placed.png:
"Sam" at fontSize 0.115*h renders an 8px cap at 1080p → em 11.6px → 0.0107*h ≈ h/10 of
the coefficient). So this script designs in EM AS A FRACTION OF CANVAS HEIGHT, the
honest unit, and prints the Unity coefficient as `10 * em_frac` for each slot. Paste
those into VersusOverlay.cs.

Renders the 16:9 crop (1920x1080) because that is what the Unity capture harness
produces and what we judge against. The crop is x 600..4440 of the 5040 master — the
render->canvas mapping is `x*2 + 600`, never a uniform scale.
"""

import pathlib

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = pathlib.Path(__file__).resolve().parents[2]
UNITY = ROOT / "unity" / "Emberfall" / "Assets"
OUT = ROOT / "art-bakeoff" / "versus" / "text-mock"

CANVAS_W, CANVAS_H = 5040, 2160
CROP_X0 = 600                      # 16:9 crop of the 21:9 master (render x*2 + 600)

FONT = UNITY / "TextMesh Pro" / "Fonts" / "LiberationSans.ttf"   # TMP default = what ships today
FONT_DISPLAY = UNITY / "Art" / "Fonts" / "AlfaSlabOne-Regular.ttf"   # the wordmark face

# ── Frosted geometry (VersusOverlay.cs, measured per-frame in GIMP — do not touch).
MEDALLION = {"left": (1480, 730), "right": (3544, 732)}
MEDALLION_DIA = 1200
ORB_CENTRE = (2520, 731)
ORB_DIA = 400

NAME_TINT = (245, 237, 219)
CLASS_TINT = (199, 209, 230)
STATUS_TINT = (235, 224, 209)

# ── UNDER TEST ────────────────────────────────────────────────────────────────
# em as a fraction of canvas height; y in canvas px. Unity coefficient = 10 * em.
SLOTS = {
    "name":   {"em": 0.046, "y": 1470, "tint": NAME_TINT,   "bold": True,  "track": 0.00},
    "class":  {"em": 0.028, "y": 1600, "tint": CLASS_TINT,  "bold": False, "track": 0.14},
    "status": {"em": 0.024, "y": 1850, "tint": STATUS_TINT, "bold": False, "track": 0.06},
}
SCRIM = None      # None, or dict(y=…, h=…, alpha=…) — a soft dark band behind the text
STATUS_TEXT = "Establishing link…"
SAMPLE = {"left": ("Sam", "Emberward", "emberward"),
          "right": ("Rival", "Tidecaller", "tidecaller")}
# ──────────────────────────────────────────────────────────────────────────────


def circular(img: Image.Image, dia: int, feather: float = 0.012) -> Image.Image:
    """Portrait masked to a feathered disc — SG_MedallionMask in one line of PIL."""
    img = img.convert("RGBA").resize((dia, dia), Image.LANCZOS)
    ss = 4
    mask = Image.new("L", (dia * ss, dia * ss), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, dia * ss - 1, dia * ss - 1), fill=255)
    mask = mask.resize((dia, dia), Image.LANCZOS).filter(
        ImageFilter.GaussianBlur(max(1.0, feather * dia * 0.5)))
    img.putalpha(mask)
    return img


def draw_tracked(draw: ImageDraw.ImageDraw, cx: float, cy: float, text: str,
                 font: ImageFont.FreeTypeFont, fill, track_px: float, stroke: int):
    """Centred text with letter-spacing. PIL has no tracking, so glyphs are placed
    one at a time; the whole run is measured first to keep it centred."""
    widths = [draw.textlength(ch, font=font) for ch in text]
    total = sum(widths) + track_px * max(0, len(text) - 1)
    x = cx - total / 2
    for ch, w in zip(text, widths):
        draw.text((x, cy), ch, font=font, fill=fill, anchor="lm",
                  stroke_width=stroke, stroke_fill=(0, 0, 0, 230))
        x += w + track_px


def build() -> Image.Image:
    plate = Image.open(UNITY / "Art" / "Backgrounds" / "VersusPlate_Causeway.png").convert("RGBA")
    if plate.size != (CANVAS_W, CANVAS_H):
        plate = plate.resize((CANVAS_W, CANVAS_H), Image.LANCZOS)

    for side, (cx, cy) in MEDALLION.items():
        hero = Image.open(UNITY / "Art" / "Hero" / f"art_hero_{SAMPLE[side][2]}.png")
        disc = circular(hero, MEDALLION_DIA)
        plate.alpha_composite(disc, (cx - MEDALLION_DIA // 2, cy - MEDALLION_DIA // 2))

    orb = Image.open(UNITY / "Art" / "Loading" / "Orb_StormGlass.png").convert("RGBA")
    orb = orb.resize((ORB_DIA, ORB_DIA), Image.LANCZOS)
    plate.alpha_composite(orb, (ORB_CENTRE[0] - ORB_DIA // 2, ORB_CENTRE[1] - ORB_DIA // 2))

    if SCRIM:
        band = Image.new("RGBA", (CANVAS_W, SCRIM["h"]), (0, 0, 0, 0))
        bd = ImageDraw.Draw(band)
        for i in range(SCRIM["h"]):          # vertical cosine falloff, no hard edges
            t = i / (SCRIM["h"] - 1)
            a = int(SCRIM["alpha"] * (0.5 - 0.5 * __import__("math").cos(2 * 3.14159 * t)) ** 0.5)
            bd.line([(0, i), (CANVAS_W, i)], fill=(0, 0, 0, a))
        plate.alpha_composite(band, (0, SCRIM["y"] - SCRIM["h"] // 2))

    draw = ImageDraw.Draw(plate)
    for key, s in SLOTS.items():
        px = int(round(s["em"] * CANVAS_H))
        font = ImageFont.truetype(str(FONT), px)
        stroke = max(1, round(px * 0.05))
        track = s["track"] * px
        if key == "status":
            draw_tracked(draw, CANVAS_W / 2, s["y"], STATUS_TEXT, font, s["tint"], track, stroke)
            continue
        for side, (cx, _) in MEDALLION.items():
            text = SAMPLE[side][0] if key == "name" else SAMPLE[side][1]
            # faux-bold: TMP's Bold on a default font asset is synthetic too
            reps = [(0, 0), (px * 0.018, 0), (-px * 0.018, 0)] if s["bold"] else [(0, 0)]
            for dx, dy in reps:
                draw_tracked(draw, cx + dx, s["y"] + dy, text, font, s["tint"], track, stroke)

    return plate


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    master = build()
    master.convert("RGB").save(OUT / "mock_master.png")
    crop = master.crop((CROP_X0, 0, CROP_X0 + CANVAS_W - 2 * CROP_X0, CANVAS_H))
    crop.resize((1920, 1080), Image.LANCZOS).convert("RGB").save(OUT / "mock_16x9.png")

    print("Unity constants for VersusOverlay.cs (fontSize = coefficient * h):")
    for key, s in SLOTS.items():
        print(f"  {key:7s} fontSize {10 * s['em']:.3f}f * h   "
              f"(em {s['em'] * CANVAS_H:.0f}px canvas / {s['em'] * 1080:.0f}px @1080)   "
              f"y {(1080 - s['y']) / CANVAS_H:+.4f}f * h")
    print(f"-> {OUT / 'mock_16x9.png'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
