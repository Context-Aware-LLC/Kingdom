"""Cut and split the landscape hero-power + weapon frame pair into two frames.

Input is one 2048x1024 piece from gen_hero_frames.py: two independent closed
rings side by side, the right one carrying the attack and durability bosses.
Output is two square frames that both draw at the same world size:

  HeroPower_<Material>.png   left ring  — clean rim, no bosses
  Weapon_<Material>.png      right ring — attack and durability bosses

plus HeroFrames_<Material>.json recording the ring geometry in pixels.

THREE THINGS THIS GETS RIGHT, EACH LEARNED THE HARD WAY.

1. DO NOT SPLIT AT 50%. Measuring the generated pieces showed the only empty
   column near centre is ONE PIXEL wide at x=1009 on IronEmber and a 22 px gap at
   x=995..1016 on CarvedStone. Neither contains x=1024. A midline cut lands on the
   weapon's inboard boss. The model does not put the rings where the prompt asks,
   so the seam is discovered as a connected component, never assumed.

2. THE BLACK THRESHOLD MUST BE TIGHT. The painted regions measure (0,0,0) outside
   and (1,1,0) in the sockets. At a threshold of 24 the flood ate IronEmber: dark
   forged iron genuinely samples below 24 in shadow, so it chewed speckled holes
   along the rings and ate the contact shadow between each boss and the rim,
   detaching the bosses and breaking the ring into unassemblable pieces. A
   near-black material needs a near-zero threshold.

3. NORMALISE ON THE RING, NOT THE BOUNDING BOX. This is what made the first
   split render wrong. The hero-power piece cropped to 948x943 and the weapon to
   1014x944 — the weapon's box is wider because the bosses stick out past the rim.
   Padding each to its own square and drawing both at 1.35 world units gave the
   hero ring 945/948 of that and the weapon ring only 944/1014, so the weapon
   rendered ~7% small and the two would not butt together.

   So the ring is measured directly. Its centre is the centroid of the SOCKET —
   found as a transparent region enclosed by metal, i.e. one that does not touch
   the image border, which is exact regardless of where the bosses sit. Its radius
   is the MEDIAN over 360 rays of the farthest opaque pixel: bosses perturb only a
   few sectors, so the median ignores them where a max or a bbox cannot.

   Each frame is then emitted on a canvas of side ring_diameter * CANVAS_PAD,
   centred on the ring. Because that ratio is identical for every frame, drawing
   them all at one world size makes the rings match exactly and lets the bosses
   overhang without inflating anything.

Usage: split_hero_frames.py [Material ...]
"""

import json
import math
import pathlib
import statistics
import sys

from PIL import Image, ImageDraw

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "heroframes"
UNITY = (pathlib.Path(__file__).resolve().parents[2]
         / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "HeroColumn")

THRESH = 8            # see note 2 above — keep tight
SOCKETS = [(0.25, 0.50), (0.75, 0.50)]
MIN_COMPONENT = 5000  # px; ignore speckle when picking the rings
MIN_HOLE = 2000       # px; ignore pinholes when looking for the socket
CANVAS_PAD = 1.34     # canvas side as a multiple of ring diameter (room for bosses)
RAYS = 360


def cut(im: Image.Image) -> Image.Image:
    """Flood the painted-black regions (border + both sockets) to transparency."""
    W, H = im.size
    rpx = im.convert("RGB").load()

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
            ImageDraw.floodfill(mask, (sx, sy), 128, thresh=0)
            flooded += 1

    out = im.copy()
    opx = out.load()
    n = 0
    for y in range(H):
        for x in range(W):
            if mpx[x, y] == 128:
                r, g, b, _ = opx[x, y]
                opx[x, y] = (r, g, b, 0)
                n += 1
    print(f"    cut: {flooded} seeds flooded, {100*n/(W*H):.1f}% transparent")
    return out


def components(im: Image.Image):
    """Label connected runs of opaque pixels. Returns [(area, bbox), ...] left to right."""
    W, H = im.size
    apx = im.getchannel("A").load()

    lab = Image.new("L", (W, H), 0)
    lpx = lab.load()
    for y in range(H):
        for x in range(W):
            if apx[x, y] > 0:
                lpx[x, y] = 255

    found, ident = [], 1
    for y in range(0, H, 2):
        for x in range(0, W, 2):
            if lpx[x, y] != 255 or ident > 40:
                continue
            ImageDraw.floodfill(lab, (x, y), ident, thresh=0)
            x0, y0, x1, y1, area = W, H, -1, -1, 0
            for yy in range(H):
                for xx in range(W):
                    if lpx[xx, yy] == ident:
                        area += 1
                        if xx < x0: x0 = xx
                        if xx > x1: x1 = xx
                        if yy < y0: y0 = yy
                        if yy > y1: y1 = yy
            if area >= MIN_COMPONENT:
                found.append((area, (x0, y0, x1 + 1, y1 + 1)))
            ident += 1

    found.sort(key=lambda t: t[1][0])
    return found


def ring_geometry(im: Image.Image, bbox):
    """Centre of the enclosed socket, and the ring's median outer radius.

    The socket is a transparent region that does NOT touch this crop's border —
    that is what distinguishes the hole inside the ring from the background
    around it, and it holds no matter where the bosses are placed.
    """
    crop = im.crop(bbox)
    W, H = crop.size
    a = crop.getchannel("A").load()

    # Mark every transparent pixel, then flood from the border to erase the
    # background. Whatever transparent area survives is enclosed by metal.
    holes = Image.new("L", (W, H), 0)
    hpx = holes.load()
    for y in range(H):
        for x in range(W):
            if a[x, y] == 0:
                hpx[x, y] = 255
    for x in range(W):
        for y in (0, H - 1):
            if hpx[x, y] == 255:
                ImageDraw.floodfill(holes, (x, y), 64, thresh=0)
    for y in range(H):
        for x in (0, W - 1):
            if hpx[x, y] == 255:
                ImageDraw.floodfill(holes, (x, y), 64, thresh=0)

    xs, ys, n = 0, 0, 0
    for y in range(H):
        for x in range(W):
            if hpx[x, y] == 255:
                xs += x; ys += y; n += 1
    if n < MIN_HOLE:
        # No enclosed socket found (open ring?). Fall back to the bbox centre.
        cx, cy = W / 2.0, H / 2.0
        print(f"      warn: no enclosed socket ({n} px) — using bbox centre")
    else:
        cx, cy = xs / n, ys / n

    # Median outer radius over RAYS directions; bosses skew only a few of them.
    radii = []
    maxr = int(math.hypot(W, H))
    for i in range(RAYS):
        th = 2 * math.pi * i / RAYS
        dx, dy = math.cos(th), math.sin(th)
        last = 0
        for r in range(1, maxr):
            x, y = int(cx + dx * r), int(cy + dy * r)
            if x < 0 or y < 0 or x >= W or y >= H:
                break
            if a[x, y] > 0:
                last = r
        radii.append(last)
    radius = statistics.median([r for r in radii if r > 0])

    # Back to full-image coordinates.
    return (bbox[0] + cx, bbox[1] + cy), radius


def emit(im: Image.Image, centre, radius: float, bbox) -> Image.Image:
    """Square canvas of side diameter*CANVAS_PAD, centred on the ring.

    MASK TO THE COMPONENT'S OWN BOX. The canvas is wider than the ring (it has to
    be, to hold the bosses), so a naive crop of the full image reaches past this
    ring into its neighbour and drags a slice of it along — which showed up
    on the board as a dark sliver floating beside the hero-power ring.
    """
    side = int(round(radius * 2 * CANVAS_PAD))
    cx, cy = centre
    left, top = int(round(cx - side / 2)), int(round(cy - side / 2))

    # Keep only this component's bounding box; blank everything outside it.
    isolated = Image.new("RGBA", im.size, (0, 0, 0, 0))
    isolated.paste(im.crop(bbox), (bbox[0], bbox[1]))

    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    # crop() pads with transparent where the box runs off the source.
    canvas.paste(isolated.crop((left, top, left + side, top + side)), (0, 0))
    return canvas


def main() -> int:
    names = sys.argv[1:]
    files = ([SRC / f"HeroFrames_{n}.png" for n in names] if names
             else sorted(p for p in SRC.glob("HeroFrames_*.png") if "_cut" not in p.stem))
    if not files:
        print(f"nothing in {SRC} — run gen_hero_frames.py first", file=sys.stderr)
        return 1

    UNITY.mkdir(parents=True, exist_ok=True)
    rc = 0
    for f in files:
        mat = f.stem.replace("HeroFrames_", "")
        print(f"{f.name}:")

        piece = cut(Image.open(f).convert("RGBA"))
        piece.save(f.with_name(f.stem + "_cut.png"))

        comps = components(piece)
        print(f"    components: {comps}")

        if len(comps) < 2:
            print("    *** THE TWO RINGS ARE FUSED - cannot split by component. ***")
            print("    *** Regenerate with clearance between the left ring and  ***")
            print("    *** the weapon's inboard boss.                           ***")
            rc = 3
            continue
        if len(comps) > 2:
            print(f"    note: {len(comps)} components; taking leftmost and rightmost")

        meta = {"canvasPad": CANVAS_PAD, "frames": {}}
        for label, (_, bbox) in (("HeroPower", comps[0]), ("Weapon", comps[-1])):
            centre, radius = ring_geometry(piece, bbox)
            img = emit(piece, centre, radius, bbox)
            name = f"{label}_{mat}.png"
            img.save(SRC / name)
            (UNITY / name).write_bytes((SRC / name).read_bytes())
            meta["frames"][label] = {
                "ringCentrePx": [round(centre[0], 1), round(centre[1], 1)],
                "ringDiameterPx": round(radius * 2, 1),
                "canvasPx": img.size[0],
            }
            print(f"    {name}  canvas {img.size[0]}px  ring d={radius*2:.0f}px")

        (SRC / f"HeroFrames_{mat}.json").write_text(json.dumps(meta, indent=2))

        d = [v["ringDiameterPx"] for v in meta["frames"].values()]
        print(f"    ring diameters: {d[0]:.0f} vs {d[1]:.0f}  "
              f"(mismatch {abs(d[0]-d[1])/max(d)*100:.1f}%)")

    print(f"\n-> {UNITY}")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
