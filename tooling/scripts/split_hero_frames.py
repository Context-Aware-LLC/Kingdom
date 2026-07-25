"""Cut and split the landscape hero-power + weapon frame pair.

Input is one 2048x1024 piece from gen_hero_frames.py: two independent closed
rings side by side. Output is two square 1024x1024 frames, each drawn at
1.35 x 1.35 world units (3/4 of the 1.8 hero plate).

  HeroPower_<Material>.png   left half  — clean rim, no bosses
  Weapon_<Material>.png      right half — attack boss top, durability boss bottom

CUT FIRST, THEN SPLIT. The alpha cut floods from the image border and from each
socket centre, so it needs the whole image: the border region is one connected
area that reaches around both rings. Splitting first would strand the seam-side
background of each half with no border seed touching it.

Seeds come from the generation contract in gen_hero_frames.py:
sockets at (0.25, 0.50) and (0.75, 0.50). The stat bosses are NOT seeded and
their centres sit well above the black threshold, so they stay solid and keep
occluding the weapon art behind them.

Unlike the first pass (split_hero_column.py) there is NO rotation and NO shared
strut, so each half comes out already finished on its own edge — which is what
lets the hero-power frame stand alone when no weapon is equipped.

Usage: split_hero_frames.py [Material ...]
"""

import pathlib
import sys

from PIL import Image, ImageDraw

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "heroframes"
UNITY = (pathlib.Path(__file__).resolve().parents[2]
         / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "HeroColumn")

THRESH = 24
FILL = 128
SOCKETS = [(0.25, 0.50), (0.75, 0.50)]


def cut(im: Image.Image) -> Image.Image:
    W, H = im.size
    rgb = im.convert("RGB")
    rpx = rgb.load()

    mask = Image.new("L", (W, H), 0)
    mpx = mask.load()
    for y in range(H):
        for x in range(W):
            r, g, b = rpx[x, y]
            if r <= THRESH and g <= THRESH and b <= THRESH:
                mpx[x, y] = 255

    seeds = [(1, 1), (W - 2, 1), (1, H - 2), (W - 2, H - 2)]
    seeds += [(int(fx * W), int(fy * H)) for fx, fy in SOCKETS]
    flooded = 0
    for sx, sy in seeds:
        if mpx[sx, sy] == 255:
            ImageDraw.floodfill(mask, (sx, sy), FILL, thresh=0)
            flooded += 1

    out = im.copy()
    opx = out.load()
    n = 0
    for y in range(H):
        for x in range(W):
            if mpx[x, y] == FILL:
                r, g, b, _ = opx[x, y]
                opx[x, y] = (r, g, b, 0)
                n += 1
    print(f"    {flooded} seeds flooded, {100*n/(W*H):.1f}% cut")
    return out


def main() -> int:
    names = sys.argv[1:]
    files = ([SRC / f"HeroFrames_{n}.png" for n in names] if names
             else sorted(p for p in SRC.glob("HeroFrames_*.png") if "_cut" not in p.stem))
    if not files:
        print(f"nothing in {SRC} — run gen_hero_frames.py first", file=sys.stderr)
        return 1

    UNITY.mkdir(parents=True, exist_ok=True)
    for f in files:
        if not f.exists():
            print(f"missing {f}", file=sys.stderr)
            return 2
        mat = f.stem.replace("HeroFrames_", "")
        print(f"{f.name}:")

        im = Image.open(f).convert("RGBA")
        piece = cut(im)
        piece.save(f.with_name(f.stem + "_cut.png"))

        W, H = piece.size
        mid = W // 2
        halves = {
            f"HeroPower_{mat}.png": piece.crop((0, 0, mid, H)),
            f"Weapon_{mat}.png":    piece.crop((mid, 0, W, H)),
        }
        for name, img in halves.items():
            img.save(SRC / name)
            (UNITY / name).write_bytes((SRC / name).read_bytes())
            print(f"    {name}  {img.size}")

    print(f"\n-> {UNITY}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
