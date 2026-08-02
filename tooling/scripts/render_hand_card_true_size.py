"""EF-062 · Render a card in hand at TRUE device pixel size, then look at it.

The 87 px weapon socket and the board-scale art detail budget were both settled by
rendering at true size rather than by reasoning about it (project memory, 2026-07).
device_layout.py gives the arithmetic; this gives the thing you can actually judge.

It composites the SHIPPING frame art and the SHIPPING typefaces at the exact geometry
CardPresenter.ApplySockets uses — every fraction below is read out of
FrameRarityCatalog.cs, not retyped — and rasterises the card at:

  * 137 x 194 px   the real size on a 1080p phone, at rest
  * 129 x 182 px   the same card once a 24dp gesture bar is respected (SafeAreaCamera)
  * 121 x 170 px   ... a 48dp navigation bar
  * 265 x 375 px   the EF-050 / Appendix A collection-wall reference, for contrast
  * 4x zooms of each, so the failure is visible on a desktop monitor too

Writes artifacts/ef-062/hand_card_true_size.png. Nothing here touches Unity.

Usage:  python tooling/scripts/render_hand_card_true_size.py
"""

import math
import pathlib
import re
import sys

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")

REPO = pathlib.Path(__file__).resolve().parents[2]
UNITY = REPO / "unity" / "Emberfall"
SCRIPTS = UNITY / "Assets" / "Scripts" / "Presentation"
ART = UNITY / "Assets" / "Art"
OUT = REPO / "artifacts" / "ef-062"

FRAME_PNG = ART / "UI" / "Frames" / "frame_epic.png"    # IronEmber, the calibration master
DISPLAY_TTF = ART / "Fonts" / "AlfaSlabOne-Regular.ttf"  # CardFont()  — card name
BODY_TTF = ART / "Fonts" / "GoogleSans-Regular.ttf"      # BodyFont()  — rules box


def const(path, name):
    num = r"-?[\d.]+f?"
    pat = re.compile(r"\bconst\s+(?:float|int)\s+" + re.escape(name)
                     + rf"\s*=\s*({num})(?:\s*/\s*({num}))?\s*;")
    for line in path.read_text(encoding="utf-8").splitlines():
        m = pat.search(line)
        if m:
            v = float(m.group(1).rstrip("f"))
            return v / float(m.group(2).rstrip("f")) if m.group(2) else v
    raise SystemExit(f"render_hand_card: const {name} not found in {path.name}")


def vec2(path, name):
    """Read a `public static readonly Vector2 NAME = new Vector2(a, b)` style pair."""
    pat = re.compile(re.escape(name) + r"[^\n]*?new Vector2\(\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?")
    m = pat.search(path.read_text(encoding="utf-8"))
    if not m:
        raise SystemExit(f"render_hand_card: {name} not found in {path.name}")
    return float(m.group(1)), float(m.group(2))


frames = SCRIPTS / "FrameRarityCatalog.cs"
CARD_W = const(SCRIPTS / "BoardController.cs", "CardW")
CARD_H = const(SCRIPTS / "BoardController.cs", "CardH")
BODY_EM = const(frames, "BodyEm")
BODY_FLOOR = const(frames, "BodyEmFloor")
BODY_CY = const(frames, "BodyCenterY")
NAME_EM = const(frames, "NameEm")
NAME_CX = const(frames, "NameCenterX")
NAME_CY = const(frames, "NameCenterY")
KEYWORD_PCT = const(frames, "KeywordLeadPct")

# IronEmber's socket row: cost / attack / health digit centres, digit em, art window.
_iron = re.search(
    r"IronEmber = new\([\s\S]*?"
    r"new Vector2\(([\d.]+)f,\s*([\d.]+)f\),[\s\S]*?"
    r"new Vector2\(([\d.]+)f,\s*([\d.]+)f\),[\s\S]*?"
    r"new Vector2\(([\d.]+)f,\s*([\d.]+)f\),[\s\S]*?"
    r"new Vector4\(([\d.]+)f,\s*([\d.]+)f,\s*([\d.]+)f,\s*([\d.]+)f\)",
    frames.read_text(encoding="utf-8"))
if not _iron:
    raise SystemExit("render_hand_card: IronEmber socket row not found in FrameRarityCatalog.cs")
_g = [float(x) for x in _iron.groups()]
COST_C, ATK_C, HP_C = (_g[0], _g[1]), (_g[2], _g[3]), (_g[4], _g[5])
ART_WIN = tuple(_g[6:10])
DIGIT_EM = const(frames, "DigitEmStd")

# The card under the lens: the set's kind of worst case for the rules band — long
# enough that TMP's auto-fit is on the shrink floor rather than at BodyEm.
NAME = "Graven Bellringer"
RULES_KEYWORD = "Deathrattle:"
RULES_BODY = " Summon two 1/1 Wisps and give them Rush until end of turn."
COST, ATTACK, HEALTH = "6", "4", "5"

INK = (36, 26, 18, 255)
PARCHMENT_NAME = (238, 226, 200, 255)
LABEL = (150, 158, 172, 255)
SURFACE = (20, 25, 34, 255)


def render_card(px_w: int, on_floor: bool) -> Image.Image:
    """One card front at exactly px_w wide, at the real card aspect."""
    px_h = round(px_w * CARD_H / CARD_W)

    # Supersample 8x, then box-filter down. That is what the GPU does to an SDF glyph
    # at this size; rasterising directly at 8 px would flatter us with hinting the
    # device will not have.
    ss = 8
    W, H = px_w * ss, px_h * ss
    card = Image.new("RGBA", (W, H), (0, 0, 0, 0))

    frame = Image.open(FRAME_PNG).convert("RGBA").resize((W, H), Image.LANCZOS)

    # Art window: a flat plate stands in for illustration — this render is judging
    # TEXT, and a busy illustration behind the rules band would only flatter it.
    l, t, r, b = ART_WIN
    art = Image.new("RGBA", (round((r - l) * W), round((b - t) * H)), (62, 54, 46, 255))
    card.alpha_composite(art, (round(l * W), round(t * H)))
    card.alpha_composite(frame)

    d = ImageDraw.Draw(card)

    # Name — display face, fixed size, no auto-fit (CardPresenter.ApplySockets).
    name_px = NAME_EM * H
    fname = ImageFont.truetype(str(DISPLAY_TTF), round(name_px))
    d.text((NAME_CX * W, NAME_CY * H), NAME, font=fname, fill=PARCHMENT_NAME,
           anchor="mm")

    # Rules band — text face, bold-ish, wrapped inside 0.74 CardW as CardPresenter does.
    em = (BODY_FLOOR if on_floor else BODY_EM) * H
    fbody = ImageFont.truetype(str(BODY_TTF), round(em))
    fkey = ImageFont.truetype(str(BODY_TTF), round(em * KEYWORD_PCT / 100.0))
    wrap_w = 0.74 * W

    words = (RULES_KEYWORD + RULES_BODY).split()
    lines, cur = [], ""
    for word in words:
        trial = (cur + " " + word).strip()
        if d.textlength(trial, font=fbody) > wrap_w and cur:
            lines.append(cur)
            cur = word
        else:
            cur = trial
    lines.append(cur)

    # CardPresenter wraps inside a 0.74 CardW x 0.21 CardH rect and lets TMP auto-fit.
    # This mock has no auto-fit, so clamp the leading instead — otherwise a third line
    # escapes the parchment bed and reads as a layout defect that is only in the mock.
    # WHAT THIS SHEET IS FOR IS GLYPH SIZE, which is exact; vertical placement is
    # indicative.
    leading = min(em * 1.18, 0.21 * H / max(len(lines), 1))
    y = BODY_CY * H - (len(lines) - 1) * leading / 2
    for i, line in enumerate(lines):
        if i == 0 and line.startswith(RULES_KEYWORD):
            kw, rest = RULES_KEYWORD, line[len(RULES_KEYWORD):]
            wk = d.textlength(kw, font=fkey)
            wr = d.textlength(rest, font=fbody)
            x = W / 2 - (wk + wr) / 2
            d.text((x, y), kw, font=fkey, fill=INK, anchor="lm")
            d.text((x + wk, y), rest, font=fbody, fill=INK, anchor="lm")
        else:
            d.text((W / 2, y), line, font=fbody, fill=INK, anchor="mm")
        y += leading

    # Digits on their measured boss centres.
    fdig = ImageFont.truetype(str(DISPLAY_TTF), round(DIGIT_EM * H))
    for (cx, cy), text in ((COST_C, COST), (ATK_C, ATTACK), (HP_C, HEALTH)):
        d.text((cx * W, cy * H), text, font=fdig, fill=PARCHMENT_NAME, anchor="mm")

    return card.resize((px_w, px_h), Image.BOX)


def main() -> int:
    ortho = 4.6
    ppu_1080 = 1080 / (2 * ortho)
    variants = [
        (round(CARD_W * ppu_1080), "1080p, no insets\n137 px — SHIPS TODAY"),
        (round(CARD_W * 1080 / (2 * ortho * 1080 / (1080 - 66))), "24dp gesture bar\n129 px"),
        (round(CARD_W * 1080 / (2 * ortho * 1080 / (1080 - 132))), "48dp nav bar\n121 px"),
        (265, "EF-050 reference\n265 px collection tile"),
    ]

    ZOOM = 4
    PAD, GAP, CAPTION = 28, 34, 46
    # Two rows: BodyEm on top, the auto-shrink floor beneath — long rules text is the
    # case the band is actually contested in, and it is not the exception.
    rows = [
        ("BodyEm (short rules text)", [(render_card(w, False), w, cap) for w, cap in variants]),
        ("BodyEmFloor (long rules text — the common case)",
         [(render_card(w, True), w, cap) for w, cap in variants]),
    ]

    fcap = ImageFont.truetype(str(BODY_TTF), 15)
    fhead = ImageFont.truetype(str(BODY_TTF), 20)

    # Column width holds the 4x zoom; the true-size original sits beneath it with the
    # caption under that. Row height must account for BOTH or the tallest column runs
    # off the sheet.
    col_w = [w * ZOOM for w, _ in variants]
    total_w = PAD * 2 + sum(col_w) + GAP * (len(variants) - 1)
    block_h = [max(c.height for c, _, _ in r[1]) * (ZOOM + 1) + 8 + CAPTION for r in rows]
    row_h = [30 + b + 40 for b in block_h]
    total_h = PAD * 2 + sum(row_h) + 30

    sheet = Image.new("RGBA", (total_w, total_h), SURFACE)
    d = ImageDraw.Draw(sheet)

    y = PAD
    for (title, items), rh in zip(rows, row_h):
        d.text((PAD, y), title, font=fhead, fill=(226, 232, 240, 255))
        y += 30
        x = PAD
        for (card, w, cap), cw in zip(items, col_w):
            big = card.resize((card.width * ZOOM, card.height * ZOOM), Image.NEAREST)
            sheet.alpha_composite(big, (x, y))
            sheet.alpha_composite(card, (x, y + big.height + 8))
            ty = y + big.height + 8 + card.height + 6
            for line in cap.split("\n"):
                d.text((x, ty), line, font=fcap, fill=LABEL)
                ty += 17
            x += cw + GAP
        y += rh - 30

    d.text((PAD, total_h - 24),
           "Each column: 4x nearest-neighbour zoom above, TRUE device pixels below. "
           "Shipping frame art, shipping typefaces, CardPresenter geometry.",
           font=fcap, fill=LABEL)

    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / "hand_card_true_size.png"
    sheet.convert("RGB").save(path)
    print(f"wrote {path.relative_to(REPO).as_posix()}  ({sheet.width} x {sheet.height})")
    for title, items in rows:
        for card, w, cap in items:
            print(f"  {title:<48} {card.width:>4} x {card.height:<4} px  "
                  f"{cap.splitlines()[0]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
