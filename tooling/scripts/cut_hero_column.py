"""Cut the hero-column plates' pure-black regions to transparency.

gen_hero_column.py paints two things pure black: the two round sockets and
everything outside the plate's silhouette. Both become transparent — the weapon
illustration and the hero-power icon are drawn BEHIND the plate and show through
the sockets.

Cut by FLOOD FILL from known seeds, not by a global threshold. A global cut also
punches holes in whatever dark ornament happens to sit inside the plate's metal;
flooding from the image border and from the two socket centres takes exactly the
three regions we painted and leaves the stat bosses (which sample 17-59 grey,
well above the threshold) solid, so they keep occluding the art behind them.

Usage: cut_hero_column.py [name ...]   (default: every HeroColumn_*.png)
"""

import pathlib
import sys

from PIL import Image, ImageDraw

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "herocolumn"
UNITY = (pathlib.Path(__file__).resolve().parents[2]
         / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "HeroColumn")

THRESH = 24          # max channel value still counted as "painted black"
FILL = 128           # marker written into the mask by the flood
SOCKETS = [(0.50, 0.27), (0.50, 0.73)]   # the generation contract's socket centres


def cut(path: pathlib.Path) -> tuple[int, int]:
    im = Image.open(path).convert("RGBA")
    W, H = im.size
    rgb = im.convert("RGB")

    # 255 where the pixel is dark enough to be a candidate for cutting.
    mask = Image.new("L", (W, H), 0)
    mpx = mask.load()
    rpx = rgb.load()
    for y in range(H):
        for x in range(W):
            r, g, b = rpx[x, y]
            if r <= THRESH and g <= THRESH and b <= THRESH:
                mpx[x, y] = 255

    # Seed the flood: the four corners (outside the silhouette) + both sockets.
    seeds = [(1, 1), (W - 2, 1), (1, H - 2), (W - 2, H - 2)]
    seeds += [(int(fx * W), int(fy * H)) for fx, fy in SOCKETS]

    filled = 0
    for sx, sy in seeds:
        if mpx[sx, sy] == 255:
            ImageDraw.floodfill(mask, (sx, sy), FILL, thresh=0)
            filled += 1

    # Anything the flood reached becomes transparent; everything else is kept.
    alpha = im.getchannel("A").load()
    out = im.copy()
    opx = out.load()
    cut_px = 0
    for y in range(H):
        for x in range(W):
            if mpx[x, y] == FILL:
                r, g, b, _ = opx[x, y]
                opx[x, y] = (r, g, b, 0)
                cut_px += 1

    dest = path.with_name(path.stem + "_cut.png")
    out.save(dest)
    return filled, cut_px


def main() -> int:
    names = sys.argv[1:]
    files = ([SRC / f"{n}.png" for n in names] if names
             else sorted(p for p in SRC.glob("HeroColumn_*.png") if "_cut" not in p.stem))
    if not files:
        print(f"nothing to cut in {SRC}", file=sys.stderr)
        return 1

    UNITY.mkdir(parents=True, exist_ok=True)
    for f in files:
        if not f.exists():
            print(f"missing {f}", file=sys.stderr)
            return 2
        seeds, px = cut(f)
        dest = f.with_name(f.stem + "_cut.png")
        total = Image.open(dest).size
        pct = 100 * px / (total[0] * total[1])
        # Ship the cut version into Unity under the ORIGINAL name so the scene
        # references keep resolving; the uncut master stays in art-bakeoff.
        (UNITY / f.name).write_bytes(dest.read_bytes())
        print(f"{f.stem}: {seeds} seeds flooded, {pct:.1f}% cut -> {dest.name} (+ Unity)")

    print(f"\n-> {UNITY}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
