"""Composite a collection-plate candidate under the REAL card grid at TRUE size.

hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/collection-plate.md S8 step 2: a candidate is judged here, never on the
bare sheet. The layout is the 2560x1440 row of hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md S2.9:

    gutter 48 . sidebar 380 . tabs+toolbar 144 . card 276x387 . gap 20
    wall width = 2560 - 2*48 - 380 = 2084 ; grid = 7*276 + 6*20 = 2052 (centered)
    wall band  = y 192..1392 ; rows = 3*387 + 2*20

Two states per candidate, because the plate has to work as both:
  populated - 21 card faces, the plate is only the 24% of screen that is edge
  sparse    - 4 card faces, the plate is most of the screen

Card faces come from mock_card_front.py's output (all four rarity frames).
Usage: python mock_collection_screen.py [Candidate ...]
"""

import pathlib
import sys

from PIL import Image, ImageDraw

REPO = pathlib.Path(__file__).resolve().parents[2]
SRC = REPO / "art-bakeoff" / "collection"
FRAMES = REPO / "art-bakeoff" / "frames"
OUT = SRC / "mocks"

SCREEN = (2560, 1440)
GUTTER = 48
SIDEBAR = 380
CHROME = 144           # class tab row + toolbar
SPINE = 48
CARD_W, CARD_H = 276, 387
GAP = 20
COLS, ROWS = 7, 3

WALL_X = GUTTER + SIDEBAR
WALL_W = SCREEN[0] - 2 * GUTTER - SIDEBAR
WALL_Y = GUTTER + CHROME
WALL_H = SCREEN[1] - GUTTER - WALL_Y
GRID_W = COLS * CARD_W + (COLS - 1) * GAP
GRID_X = WALL_X + (WALL_W - GRID_W) // 2

MOCKS = ["CarvedStone", "DarkGilt", "IronEmber", "AgedGilt"]


def load_cards():
    faces = []
    for name in MOCKS:
        p = FRAMES / f"Mock_{name}_BronzegateWarden.png"
        if not p.exists():
            print(f"missing {p.name} - run: python tooling/scripts/mock_card_front.py {name}",
                  file=sys.stderr)
            continue
        faces.append(Image.open(p).convert("RGBA").resize((CARD_W, CARD_H), Image.LANCZOS))
    return faces


def plate_to_screen(path: pathlib.Path) -> Image.Image:
    """Height-anchored: full height always shown, width cropped to the 16:9 core."""
    im = Image.open(path).convert("RGB")
    scale = SCREEN[1] / im.height
    im = im.resize((round(im.width * scale), SCREEN[1]), Image.LANCZOS)
    left = (im.width - SCREEN[0]) // 2
    return im.crop((left, 0, left + SCREEN[0], SCREEN[1]))


def compose(path: pathlib.Path, faces, n_cards: int, label: str) -> pathlib.Path:
    screen = plate_to_screen(path)
    ov = Image.new("RGBA", SCREEN, (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)

    # sidebar panel - an opaque UI surface, not plate
    d.rectangle([GUTTER, GUTTER, GUTTER + SIDEBAR, SCREEN[1] - GUTTER],
                fill=(20, 16, 14, 236))
    # book spine strip at the sidebar's inner edge
    d.rectangle([GUTTER + SIDEBAR - SPINE, GUTTER, GUTTER + SIDEBAR, SCREEN[1] - GUTTER],
                fill=(10, 8, 7, 255))
    # class tab row + toolbar: plate shows through behind the controls
    d.rectangle([WALL_X, GUTTER, SCREEN[0] - GUTTER, GUTTER + CHROME],
                fill=(0, 0, 0, 56))

    screen = Image.alpha_composite(screen.convert("RGBA"), ov)

    for i in range(min(n_cards, COLS * ROWS)):
        c, r = i % COLS, i // COLS
        x = GRID_X + c * (CARD_W + GAP)
        y = WALL_Y + r * (CARD_H + GAP)
        face = faces[i % len(faces)]
        screen.alpha_composite(face, (x, y))

    OUT.mkdir(parents=True, exist_ok=True)
    dest = OUT / f"{path.stem}_{label}.jpg"
    screen.convert("RGB").save(dest, quality=92)
    return dest


def main() -> int:
    faces = load_cards()
    if not faces:
        return 1

    plates = sorted(SRC.glob("Collection_*.png"))
    if wanted := sys.argv[1:]:
        plates = [p for p in plates if any(w.lower() in p.stem.lower() for w in wanted)]
    if not plates:
        print(f"no candidates in {SRC}", file=sys.stderr)
        return 1

    for p in plates:
        for n, label in ((COLS * ROWS, "populated"), (4, "sparse")):
            print("ok", compose(p, faces, n, label).name)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
