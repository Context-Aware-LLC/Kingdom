"""Cut a loading/versus orb concept to a feathered circular alpha.

Reproduces the as-built recipe of `Art/Orbs/Orb_StormGlass.png` (2026-07-19), recovered
by fitting the shipped file back against its concept:

  1. crop a square around the sphere (side 878 of 1024, centred (514,507) for StormGlass),
  2. resize that square up to the full 1024 canvas (Lanczos) — this reproduces the shipped
     RGB byte-exactly,
  3. apply the feathered circular alpha (solid to r~485, zero at r~502).

Step 2 is the part that is easy to miss: the concepts are rendered with black margin, and
the shipped orb fills its canvas edge to edge. Cutting alpha *without* the upscale yields an
orb that renders ~14% small in the Unity slot.

For step 3 the mask is lifted verbatim from the shipped StormGlass alpha rather than
re-modelled. Its edge is circular to within ~1px but not an exact smoothstep, and since all
three orbs cycle in the same slot, byte-identical masks are what guarantee no edge pop
between them. `--mask synth` falls back to a fitted smoothstep if the reference is missing.

Usage:
    python cut_orb_alpha.py <concept.png> <out.png> [--cx CX --cy CY --side S]

Defaults locate the subject by luminance bounding box, which is correct for a round subject
(StormGlass, Confluence). Pass explicit --cx/--cy/--side for subjects the bbox gets wrong --
notably HollowCore, whose orbiting rings make its silhouette 1.43:1 rather than square.
"""
import argparse
import os

import numpy as np
from PIL import Image

CANVAS = 1024
R_SOLID = 485.0
R_ZERO = 502.0
REFERENCE_MASK = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "unity", "Emberfall", "Assets", "Art", "Orbs", "Orb_StormGlass.png",
)


def find_subject(img, threshold=16):
    """Square crop box around the luminance silhouette, centred on its bbox."""
    lum = np.array(img.convert("L")).astype(float)
    ys, xs = np.nonzero(lum > threshold)
    cx = (xs.min() + xs.max()) / 2.0
    cy = (ys.min() + ys.max()) / 2.0
    side = max(xs.max() - xs.min(), ys.max() - ys.min()) + 1
    return cx, cy, float(side)


def feathered_disc(size, mode="reference"):
    """The orb alpha: the shipped StormGlass mask, or a fitted smoothstep fallback."""
    if mode == "reference" and os.path.exists(REFERENCE_MASK):
        ref = Image.open(REFERENCE_MASK).split()[-1]
        return np.array(ref if ref.size == (size, size) else ref.resize((size, size), Image.LANCZOS))

    c = (size - 1) / 2.0
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.hypot(yy - c, xx - c)
    t = np.clip((r - R_SOLID) / (R_ZERO - R_SOLID), 0.0, 1.0)
    return (255.0 * (1.0 - (t * t * (3.0 - 2.0 * t)))).astype(np.uint8)


def cut(src_path, out_path, cx=None, cy=None, side=None, mask="reference"):
    img = Image.open(src_path).convert("RGB")
    if cx is None or cy is None or side is None:
        fcx, fcy, fside = find_subject(img)
        cx = fcx if cx is None else cx
        cy = fcy if cy is None else cy
        side = fside if side is None else side

    half = side / 2.0
    box = (cx - half, cy - half, cx + half, cy + half)
    filled = img.resize((CANVAS, CANVAS), Image.LANCZOS, box=box)

    alpha = feathered_disc(CANVAS, mask)

    # The shipped StormGlass zeroes RGB outside the disc; match it so all three orbs carry
    # the same convention and mip down identically as they cycle.
    rgb = np.array(filled).astype(np.uint16)
    rgb[alpha == 0] = 0

    out = Image.fromarray(rgb.astype(np.uint8), "RGB").convert("RGBA")
    out.putalpha(Image.fromarray(alpha))
    out.save(out_path)
    print("%s -> %s  (crop centre %.1f,%.1f side %.1f)" % (src_path, out_path, cx, cy, side))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("src")
    p.add_argument("out")
    p.add_argument("--cx", type=float)
    p.add_argument("--cy", type=float)
    p.add_argument("--side", type=float)
    p.add_argument("--mask", choices=("reference", "synth"), default="reference")
    a = p.parse_args()
    cut(a.src, a.out, a.cx, a.cy, a.side, a.mask)
