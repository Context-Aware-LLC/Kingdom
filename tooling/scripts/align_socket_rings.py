#!/usr/bin/env python3
"""Clean, scale and co-register the hero-power / weapon socket rings onto one canvas.

WHY THIS EXISTS. The two IronEmber rings are hand-cut in GIMP from one source frame, and two
things go wrong every time that happens:

  1. The matte comes back with debris and haze. Measured on weapon-frame-2.png: 382 separate
     islands (2,080 px of flecks), and 65,868 partial-alpha pixels of which only 13.7% sit
     against a solid pixel — so the rest is a faint haze through the field rather than ordinary
     anti-aliasing. hp_frame-2.png by contrast is clean: 142 px of debris, and 83.3% of its
     partial alpha is a real edge.
  2. The two rings end up with different aperture diameters and different aperture CENTRES.
     Measured: 731x742 at (605.0, 506.5) against 686x700 at (630.5, 475.5) — 6% smaller and
     30px off. Scale cannot fix that, because it is wrong at every scale: a glyph placed inside
     one ring changes size AND jumps position when the other is swapped in.

WHAT "CONSISTENT" ACTUALLY MEANS HERE, and it is not "same canvas". Two ring sprites are
interchangeable when, drawn at one USS width, their apertures land in the same place at the same
size. That is three invariants — canvas, aperture diameter, aperture centre — and only the first
is easy to eyeball, which is why the other two drift.

THE FLARES ARE THE REASON THE CANVAS IS COMPUTED, NOT CHOSEN (Sam, 2026-09-03: "the difference
in the flares prevents me from avoiding a cutoff"). The two rings carry different ornament — the
weapon's blades reach further than the hero power's wreath — so any canvas picked to suit one
clips the other. Solved by measuring both AFTER alignment and sizing the canvas to the union,
with the aperture centre pinned at the middle. The smaller ornament simply gets more transparent
margin, which costs nothing.

WHAT IT CANNOT DO: restore ornament already cut off in the input. If a source touches its own
canvas border, those pixels are gone. The script reports that per file rather than papering over
it, because a re-export is the only fix and it needs to happen before anyone trusts the output.

Usage:
    python tooling/scripts/align_socket_rings.py OUT_DIR REF.png OTHER.png [OTHER2.png ...]

The FIRST input is the reference: its aperture diameter is the target every other file is scaled
to match. Pick the one with the cleaner matte.
"""

import sys
import os
import numpy as np
from PIL import Image
from scipy import ndimage

ALPHA_ON = 24          # alpha above this counts as "there" when finding the silhouette
SOLID = 250            # alpha at or above this is fully opaque
CLEAR = 5              # alpha at or below this is fully transparent
MIN_ISLAND = 64        # connected components smaller than this are matte noise, not art


def load(path):
    im = Image.open(path).convert("RGBA")
    return np.array(im), im.size


def despeckle(rgba, label):
    """Drop everything that is not the ring, and report what went.

    This removes ISOLATED islands and nothing else. On weapon-frame-2.png that is real work —
    382 components, 381 of them dropped, 2,080 px of flecks gone. On hp_frame-2.png it is nearly
    a no-op: 19 components, 142 px.

    DO NOT ADD A MORPHOLOGICAL OPENING HERE. One was written, on the theory that long diagonal
    streaks were bridged onto the ring by a hairline of faint pixels — and the streaks did not
    exist. They were an artifact of previewing an RGBA image on a WHITE background, where a
    transparent field and a white slash look identical. Composited onto magenta both rings are
    clean. The opening was then measured "failing" to remove them at r=2..16, which is exactly
    what a filter does when its target is not there, and that non-result nearly shipped in this
    docstring as a fact about the art.

    **Check an alpha claim against a non-white matte before believing it.** The one-liner:

        Image.alpha_composite(Image.new('RGBA', im.size, (255,0,255,255)), im)
    """
    alpha = rgba[..., 3]
    on = alpha > ALPHA_ON
    lab, n = ndimage.label(on)
    if n == 0:
        raise SystemExit(f"{label}: nothing above the alpha floor — is this file empty?")
    sizes = ndimage.sum(on, lab, range(1, n + 1))
    keep = int(np.argmax(sizes)) + 1
    final = lab == keep

    removed_islands = n - 1
    removed_px = int(on.sum() - final.sum())

    out = rgba.copy()
    out[..., 3] = np.where(final, alpha, 0)

    partial_before = int(((alpha > CLEAR) & (alpha < SOLID)).sum())
    a2 = out[..., 3]
    partial_after = int(((a2 > CLEAR) & (a2 < SOLID)).sum())
    print(f"  despeckle    {n} island(s), kept the largest; dropped {removed_islands}, {removed_px} px")
    print(f"               partial-alpha {partial_before} -> {partial_after}")
    return out


def aperture(rgba, label):
    """The transparent disc reachable from the image centre: size, centre, solidity."""
    alpha = rgba[..., 3]
    off = alpha <= ALPHA_ON
    lab, n = ndimage.label(off)
    h, w = alpha.shape
    cid = lab[h // 2, w // 2]
    if cid == 0:
        raise SystemExit(f"{label}: the image centre is not transparent — no aperture to measure.")
    # The background outside the ring is also transparent; if the flood from the centre reaches a
    # border the ring is broken open and the measurement is meaningless.
    ys, xs = np.where(lab == cid)
    if xs.min() == 0 or ys.min() == 0 or xs.max() == w - 1 or ys.max() == h - 1:
        raise SystemExit(f"{label}: the centre hole leaks to the canvas border — the ring is not "
                         f"closed. Re-cut it; nothing downstream of this is trustworthy.")
    aw, ah = xs.max() - xs.min() + 1, ys.max() - ys.min() + 1
    cx, cy = (xs.min() + xs.max()) / 2.0, (ys.min() + ys.max()) / 2.0
    solidity = len(xs) / (np.pi * aw * ah / 4.0)
    return aw, ah, cx, cy, solidity


def clipping(rgba):
    a = rgba[..., 3]
    return {
        "top": int((a[0, :] > ALPHA_ON).sum()),
        "bottom": int((a[-1, :] > ALPHA_ON).sum()),
        "left": int((a[:, 0] > ALPHA_ON).sum()),
        "right": int((a[:, -1] > ALPHA_ON).sum()),
    }


def scale_about(rgba, factor):
    if abs(factor - 1.0) < 1e-6:
        return rgba
    h, w = rgba.shape[:2]
    im = Image.fromarray(rgba, "RGBA").resize(
        (max(1, round(w * factor)), max(1, round(h * factor))), Image.LANCZOS)
    return np.array(im)


def place(rgba, cx, cy, canvas_w, canvas_h):
    """Paste so the aperture centre (cx, cy) lands at the canvas centre."""
    out = np.zeros((canvas_h, canvas_w, 4), dtype=np.uint8)
    ox = int(round(canvas_w / 2.0 - cx))
    oy = int(round(canvas_h / 2.0 - cy))
    h, w = rgba.shape[:2]
    sx0, sy0 = max(0, -ox), max(0, -oy)
    dx0, dy0 = max(0, ox), max(0, oy)
    cw = min(w - sx0, canvas_w - dx0)
    ch = min(h - sy0, canvas_h - dy0)
    out[dy0:dy0 + ch, dx0:dx0 + cw] = rgba[sy0:sy0 + ch, sx0:sx0 + cw]
    return out, ox, oy


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        raise SystemExit(2)
    out_dir, paths = sys.argv[1], sys.argv[2:]
    os.makedirs(out_dir, exist_ok=True)

    stages = []
    print("=" * 78)
    print("MEASURE + CLEAN")
    print("=" * 78)
    for i, p in enumerate(paths):
        name = os.path.basename(p)
        rgba, (w, h) = load(p)
        print(f"\n{name}   {'(REFERENCE)' if i == 0 else ''}")
        print(f"  canvas       {w} x {h}")
        clip = clipping(rgba)
        cut = ", ".join(f"{k} {v}px" for k, v in clip.items() if v)
        print(f"  clipped at   {cut if cut else 'nothing touches a border'}")
        rgba = despeckle(rgba, name)
        aw, ah, cx, cy, sol = aperture(rgba, name)
        print(f"  aperture     {aw} x {ah}  round={aw/ah:.3f}  centre ({cx:.1f}, {cy:.1f})  "
              f"solidity={sol:.3f}")
        stages.append(dict(name=name, rgba=rgba, aw=aw, ah=ah, cx=cx, cy=cy, clip=clip))

    ref = stages[0]
    ref_d = (ref["aw"] + ref["ah"]) / 2.0

    print("\n" + "=" * 78)
    print(f"ALIGN — reference aperture {ref_d:.1f}px mean diameter, from {ref['name']}")
    print("=" * 78)

    # Scale each to the reference aperture, then find the canvas that fits every ornament with
    # the aperture centre pinned at the middle. Half-extents are measured FROM the aperture
    # centre, which is the only point the sprites share.
    need_x = need_y = 0.0
    for s in stages:
        d = (s["aw"] + s["ah"]) / 2.0
        f = ref_d / d
        s["factor"] = f
        s["rgba"] = scale_about(s["rgba"], f)
        aw, ah, cx, cy, sol = aperture(s["rgba"], s["name"])
        s.update(aw=aw, ah=ah, cx=cx, cy=cy, sol=sol)
        ys, xs = np.where(s["rgba"][..., 3] > ALPHA_ON)
        s["half"] = (max(cx - xs.min(), xs.max() - cx), max(cy - ys.min(), ys.max() - cy))
        need_x = max(need_x, s["half"][0])
        need_y = max(need_y, s["half"][1])
        print(f"\n{s['name']}")
        print(f"  scale        {f:.5f}x")
        print(f"  aperture     {aw} x {ah}  round={aw/ah:.3f}  solidity={sol:.3f}")
        print(f"  ornament reaches {s['half'][0]:.0f}px / {s['half'][1]:.0f}px from the hole centre")

    side = int(2 * np.ceil(max(need_x, need_y) + 2))
    side += side % 2
    print(f"\nCanvas: square {side} x {side} — the union of both ornaments, aperture centred.")
    print("(Square, and sized by the FURTHEST-reaching ornament, so neither ring is the one that")
    print(" gets cut. The smaller one just carries more transparent margin.)")

    print("\n" + "=" * 78)
    print("WRITE + VERIFY")
    print("=" * 78)
    results = []
    for s in stages:
        placed, ox, oy = place(s["rgba"], s["cx"], s["cy"], side, side)
        dest = os.path.join(out_dir, s["name"])
        Image.fromarray(placed, "RGBA").save(dest)
        aw, ah, cx, cy, sol = aperture(placed, s["name"])
        clip = clipping(placed)
        cut = ", ".join(f"{k} {v}px" for k, v in clip.items() if v)
        print(f"\n{dest}")
        print(f"  offset       X={ox:+d}  Y={oy:+d}")
        print(f"  aperture     {aw} x {ah}  centre ({cx:.1f}, {cy:.1f})  "
              f"ap/canvas {aw/side:.4f}  solidity={sol:.3f}")
        print(f"  clipped at   {cut if cut else 'nothing touches a border'}")
        results.append((s["name"], aw, ah, cx, cy))

    print("\n" + "=" * 78)
    print("AGREEMENT — this is the whole point; every row must read 0.0 or near it")
    print("=" * 78)
    n0, aw0, ah0, cx0, cy0 = results[0]
    print(f"{'file':30} {'d(width)':>9} {'d(height)':>10} {'d(centre x)':>12} {'d(centre y)':>12}")
    for n, aw, ah, cx, cy in results:
        print(f"{n:30} {aw-aw0:>9} {ah-ah0:>10} {cx-cx0:>12.1f} {cy-cy0:>12.1f}")

    if any(v for s in stages for v in s["clip"].values()):
        print("\nWARNING — at least one INPUT touched its own canvas border. Those pixels were")
        print("already gone before this script ran and no amount of padding brings them back.")
        print("Re-export from the .xcf with room around the ornament, then re-run.")


if __name__ == "__main__":
    main()
