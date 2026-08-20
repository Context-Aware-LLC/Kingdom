"""SUPERSEDED 2026-08-19. The rarity-gem socket is no longer painted into the frame art --
it is its own sprite (Assets/Art/UI/Frames/socket_gem.png) composited on top, so there is
nothing in the frame left to measure. GemSocket/GemHeight are now placement constants for that
sprite, derived in docs/dev-docs/specs/card-frame-geo.md. Kept because it is the record of how
the old socket was found, not because it still runs against anything meaningful.
"""

"""Measure the rarity-gem socket painted into each card frame.

The frame set carries three sockets of the same construction: a stone bezel around a dark,
near-black interior. Two are the stat bosses (bottom-left / bottom-right) and the third is the
one nothing has ever drawn into -- bottom-centre, between them. This finds that third one and
reports its centre and radius as fractions of the frame, in the same top-left-origin convention
FrameRarityCatalog uses for every other socket.

The detector is validated in the same run against the cost socket, whose fraction is already
pinned in FrameRarityCatalog (0.0911, 0.0731). If the printed cost socket does not land on that,
do not trust the gem numbers either -- the detector is wrong, not the constant.

Usage:  python tooling/scripts/measure_gem_socket.py
"""

import sys
from pathlib import Path

import numpy as np
from PIL import Image

FRAMES = Path("unity/Emberfall/Assets/Art/UI/Frames")

# A socket interior is opaque and near-black. The bezel around it is mid-grey stone, and the
# card body behind the frame is transparent, so both are excluded by these two tests together.
DARK_MAX = 60      # 0-255, per channel
ALPHA_MIN = 200


def largest_blob(mask):
    """Centre and radius of the largest 4-connected run-region in `mask`, by flood fill."""
    h, w = mask.shape
    seen = np.zeros_like(mask, dtype=bool)
    best = None

    for sy in range(h):
        for sx in range(w):
            if not mask[sy, sx] or seen[sy, sx]:
                continue
            stack = [(sy, sx)]
            seen[sy, sx] = True
            pts = []
            while stack:
                y, x = stack.pop()
                pts.append((y, x))
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
            if best is None or len(pts) > len(best):
                best = pts

    if not best:
        return None

    arr = np.array(best)
    cy, cx = arr[:, 0].mean(), arr[:, 1].mean()
    # Equivalent-circle radius from area: robust to the bezel nibbling the edges.
    radius = (len(best) / np.pi) ** 0.5
    return cy, cx, radius, len(best)


def measure(path, box):
    """box = (l, t, r, b) as fractions of the frame; searched for the darkest blob inside."""
    img = Image.open(path).convert("RGBA")
    w, h = img.size
    a = np.asarray(img)

    l, t, r, b = box
    x0, x1 = int(l * w), int(r * w)
    y0, y1 = int(t * h), int(b * h)
    crop = a[y0:y1, x0:x1]

    dark = (crop[:, :, :3].max(axis=2) <= DARK_MAX) & (crop[:, :, 3] >= ALPHA_MIN)
    found = largest_blob(dark)
    if not found:
        return None

    cy, cx, radius, area = found
    return {
        "fx": (x0 + cx) / w,
        "fy": (y0 + cy) / h,
        "r_frac_h": radius / h,
        "px": int(area),
        "size": (w, h),
    }


def main():
    frames = sorted(FRAMES.glob("frame_*.png"))
    if not frames:
        print(f"no frames under {FRAMES}", file=sys.stderr)
        return 1

    print(f"{'frame':<28} {'cost fx,fy':>18} {'GEM fx,fy':>18} {'gem r/h':>9} {'gem px':>9}")
    print("-" * 88)

    gem_fx, gem_fy, gem_r = [], [], []
    for f in frames:
        cost = measure(f, (0.02, 0.02, 0.20, 0.14))
        gem = measure(f, (0.38, 0.86, 0.62, 1.00))
        cost_s = f"{cost['fx']:.4f},{cost['fy']:.4f}" if cost else "-"
        if gem:
            gem_s = f"{gem['fx']:.4f},{gem['fy']:.4f}"
            gem_fx.append(gem["fx"])
            gem_fy.append(gem["fy"])
            gem_r.append(gem["r_frac_h"])
            print(f"{f.name:<28} {cost_s:>18} {gem_s:>18} {gem['r_frac_h']:>9.4f} {gem['px']:>9}")
        else:
            print(f"{f.name:<28} {cost_s:>18} {'NOT FOUND':>18} {'-':>9} {'-':>9}")

    if gem_fx:
        print("-" * 88)
        print(f"{'MEAN':<28} {'':>18} {np.mean(gem_fx):.4f},{np.mean(gem_fy):.4f} "
              f"{np.mean(gem_r):>9.4f}")
        print(f"{'SPREAD (max-min)':<28} {'':>18} "
              f"{max(gem_fx) - min(gem_fx):.4f},{max(gem_fy) - min(gem_fy):.4f} "
              f"{max(gem_r) - min(gem_r):>9.4f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
