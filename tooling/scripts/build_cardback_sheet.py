"""Build the card-back bake-off sheet — at TRUE display size, plus a fanned hand.

The rule paid for twice already (mana rail, hero frames): a render is cheaper
than a theory, and a candidate judged off its full-size sheet is not judged at
all. A card back is never seen large in play. It is seen as the opponent's fanned
hand and as the deck pile, and that is the only condition that decides between
the two structures — an Emblem that reads beautifully alone can turn into a row
of identical blobs when five of them overlap, and an Allover pattern that looks
busy alone can turn into clean texture.

SCALE. Card quad is 1.17 x 1.65 world units. At ortho size 4.6 a 1080p screen
covers 9.2 world units, so 1 unit = 117.4 px and the card lands at 137 x 194 px.
The 1.5x row covers the enlarged-hand case (UI polish, 2026-07-17).

Each candidate gets a row: full-size crop, 1.5x, true 1x, then a fanned hand of
five. Output is one PNG per class so the two structures sit side by side.

Usage: build_cardback_sheet.py
"""

import pathlib
import sys

from PIL import Image, ImageDraw, ImageFont

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "card-art" / "cardbacks"

PX_PER_UNIT = 117.4
CARD_W = round(1.17 * PX_PER_UNIT)   # 137
CARD_H = round(1.65 * PX_PER_UNIT)   # 194

# MEASURED off a live match, 2026-07-25 (artifacts/cardback_board.png), not guessed.
# Opponent hand: five backs at x -1.66..1.66, step 0.83 world units, each card
# scaled to 1.93 units tall (1.17x the 1.65 card quad) so 1.37 wide.
FAN_COUNT = 5
FAN_OVERLAP = 0.83 / 1.37            # 0.61 — visible fraction of each card's width

# The half nobody predicted: the opponent's hand sits at y 3.48..5.42 while the
# camera cuts at 4.60, so the TOP 42% of every opponent card is off-screen. Any
# fan mock that shows whole cards is flattering the design. Crop it the way the
# screen does.
FAN_VISIBLE_HEIGHT = (4.60 - 3.48) / 1.93   # 0.58 — bottom fraction still on screen

# The PRIMARY surface (Sam, 2026-07-25): card backs are browsed in the
# collection as a grid of six to eight. On a 1080p page with padding that puts
# each tile near 300 px tall — far larger than the in-match hand, so ornament
# pays off here, but eight of them side by side still have to be told apart at
# a glance, so the large-scale silhouette cannot be surrendered for it.
GRID_TILE_H = 300
BG = (26, 24, 22)
INK = (208, 200, 188)
PAD = 28


def _font(size: int):
    for name in ("seguisb.ttf", "segoeui.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def fan(card: Image.Image, count: int = FAN_COUNT, crop_top: bool = True) -> Image.Image:
    """Opponent's hand as the screen actually shows it: overlapped, and cut off
    at the top edge. `crop_top=False` gives the flattering uncropped version."""
    step = round(card.width * FAN_OVERLAP)
    w = step * (count - 1) + card.width
    out = Image.new("RGBA", (w, card.height), (0, 0, 0, 0))
    for i in range(count):
        out.alpha_composite(card, (i * step, 0))
    if crop_top:
        keep = round(card.height * FAN_VISIBLE_HEIGHT)
        out = out.crop((0, card.height - keep, w, card.height))
    return out


def build(name: str, img: Image.Image) -> Image.Image:
    true1 = img.resize((CARD_W, CARD_H), Image.LANCZOS).convert("RGBA")
    true15 = img.resize((round(CARD_W * 1.5), round(CARD_H * 1.5)), Image.LANCZOS)
    big_h = 420
    big = img.resize((round(img.width * big_h / img.height), big_h), Image.LANCZOS).convert("RGBA")
    grid = img.resize((round(GRID_TILE_H * 1.17 / 1.65), GRID_TILE_H), Image.LANCZOS).convert("RGBA")
    hand = fan(true1)

    cells = [
        ("full", big),
        (f"collection tile ({GRID_TILE_H}px)", grid),
        ("1.5x", true15.convert("RGBA")),
        ("1x TRUE", true1),
        (f"opponent hand x{FAN_COUNT} (as screened)", hand),
    ]
    w = PAD + sum(c.width + PAD for _, c in cells)
    h = PAD + 26 + max(c.height for _, c in cells) + PAD + 30

    row = Image.new("RGBA", (w, h), BG + (255,))
    d = ImageDraw.Draw(row)
    d.text((PAD, 8), name, fill=INK, font=_font(20))

    x = PAD
    top = PAD + 26
    for label, cell in cells:
        row.alpha_composite(cell, (x, top))
        d.text((x, top + cell.height + 6), label, fill=(140, 132, 122), font=_font(14))
        x += cell.width + PAD
    return row


def main() -> int:
    files = sorted(SRC.glob("CardBack_*.png"))
    if not files:
        print(f"no candidates in {SRC}", file=sys.stderr)
        return 1

    groups: dict[str, list[pathlib.Path]] = {}
    for f in files:
        cls = f.stem.split("_")[1]
        groups.setdefault(cls, []).append(f)

    for cls, paths in groups.items():
        rows = [build(p.stem.replace("CardBack_", ""), Image.open(p).convert("RGB")) for p in paths]
        w = max(r.width for r in rows)
        h = sum(r.height for r in rows)
        sheet = Image.new("RGBA", (w, h), BG + (255,))
        y = 0
        for r in rows:
            sheet.alpha_composite(r, (0, y))
            y += r.height
        dest = SRC / f"SHEET_{cls}.png"
        sheet.convert("RGB").save(dest)
        print(f"{cls}: {len(paths)} candidate(s) -> {dest.name}  ({sheet.width}x{sheet.height})")

    # THE COLLECTION GRID — every candidate at tile size, all classes together.
    # This is the only view that answers whether two classes read as distinct,
    # which no per-class sheet can show. Gravekeep's grave-light green and
    # Verdant's forest green are the specific risk (2026-07-25).
    tiles = [(p.stem.replace("CardBack_", ""), Image.open(p).convert("RGB")) for p in files]
    tw = round(GRID_TILE_H * 1.17 / 1.65)
    cols = 4
    rows_n = (len(tiles) + cols - 1) // cols
    gw = PAD + cols * (tw + PAD)
    gh = PAD + 24 + rows_n * (GRID_TILE_H + PAD + 22)
    grid = Image.new("RGBA", (gw, gh), BG + (255,))
    gd = ImageDraw.Draw(grid)
    gd.text((PAD, 8), f"COLLECTION GRID — {GRID_TILE_H}px tiles", fill=INK, font=_font(20))
    for i, (label, im) in enumerate(tiles):
        cx = PAD + (i % cols) * (tw + PAD)
        cy = PAD + 24 + (i // cols) * (GRID_TILE_H + PAD + 22)
        grid.alpha_composite(im.resize((tw, GRID_TILE_H), Image.LANCZOS).convert("RGBA"), (cx, cy))
        gd.text((cx, cy + GRID_TILE_H + 4), label, fill=(140, 132, 122), font=_font(14))
    gdest = SRC / "SHEET_collection_grid.png"
    grid.convert("RGB").save(gdest)
    print(f"grid: {len(tiles)} tiles -> {gdest.name}  ({grid.width}x{grid.height})")

    print(f"\ntrue card size = {CARD_W}x{CARD_H} px (ortho 4.6, 1080p)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
