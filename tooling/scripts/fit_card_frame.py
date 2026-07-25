"""Measure a card-frame master's sockets and cut its art window.

The generation prompt (gen_card_frames.py) makes the art window SOLID PURE
BLACK, so the window is found by scanning for the largest near-black run per
row/column inside the upper 60% of the card. Corner bosses are located by
scanning each corner quadrant for its dark disc. Output:

  <name>_cut.png   — window cut to transparency (the game draws art behind)
  <name>_fit.json  — socket geometry in card-local UV (0..1, y down from top)
  <name>_overlay.png — measurement overlay for eyeball verification

Usage: fit_card_frame.py <Frame_Master.png>
"""

import json
import pathlib
import sys

from PIL import Image, ImageDraw

BLACK_T = 22          # channel threshold for "pure black window"
MIN_RUN = 0.45        # a window row must be at least this fraction of width


def find_window(px, w, h):
    """Largest vertical span of rows whose near-black run exceeds MIN_RUN,
    searched in the upper 70% of the card; returns (l, t, r, b)."""
    rows = []
    # start below the name plaque band — on dark frames the plaque is also
    # near-black and would merge into the window
    for y in range(int(h * 0.12), int(h * 0.70)):
        run = best = 0
        x0 = x1 = 0
        cur0 = 0
        for x in range(w):
            r, g, b = px[x, y][:3]
            if r < BLACK_T and g < BLACK_T and b < BLACK_T:
                if run == 0:
                    cur0 = x
                run += 1
                if run > best:
                    best, x0, x1 = run, cur0, x
            else:
                run = 0
        if best >= w * MIN_RUN:
            rows.append((y, x0, x1))
    if not rows:
        raise SystemExit("no black window found")
    t = rows[0][0]
    b = rows[-1][0]
    l = int(sorted(r[1] for r in rows)[len(rows) // 2])
    r_ = int(sorted(r[2] for r in rows)[len(rows) // 2])
    return l, t, r_, b


def find_boss(px, w, h, corner, window=None):
    """Centroid of near-black pixels in a corner box (the boss's flat dark
    centre). The top-left box is kept tight so the dark name plaque and the
    art window can't pollute the centroid on dark frames."""
    box = int(w * (0.22 if "t" in corner else 0.30))
    xs = range(0, box) if "l" in corner else range(w - box, w)
    ys = range(0, box) if "t" in corner else range(h - box, h)
    wl, wt, wr, wb = window if window else (w, h, w, h)
    pts = [(x, y) for y in ys for x in xs
           if all(c < BLACK_T for c in px[x, y][:3])
           and not (wl <= x <= wr and wt <= y <= wb)]
    if not pts:
        return None
    cx = sum(p[0] for p in pts) / len(pts)
    cy = sum(p[1] for p in pts) / len(pts)
    rad = (len(pts) / 3.14159) ** 0.5
    return cx, cy, rad


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    src_path = pathlib.Path(sys.argv[1])
    img = Image.open(src_path).convert("RGBA")
    w, h = img.size
    px = img.load()

    l, t, r, b = find_window(px, w, h)

    bosses = {}
    for key, corner in (("cost", "tl"), ("attack", "bl"), ("health", "br")):
        hit = find_boss(px, w, h, corner, window=(l, t, r, b))
        if hit:
            bosses[key] = {"cx": hit[0] / w, "cy": hit[1] / h, "r": hit[2] / w}

    # Cut the window: window rect inset by 1px, alpha -> 0 where near-black
    # (preserves the rounded corners the ornament ring draws over the window).
    for y in range(t, b + 1):
        for x in range(l, r + 1):
            pr, pg, pb, pa = px[x, y]
            if pr < BLACK_T and pg < BLACK_T and pb < BLACK_T:
                px[x, y] = (0, 0, 0, 0)

    fit = {
        "source": src_path.name,
        "size": [w, h],
        "art_window": {"l": l / w, "t": t / h, "r": r / w, "b": b / h},
        "bosses": bosses,
    }

    stem = src_path.with_suffix("")
    img.save(f"{stem}_cut.png")
    with open(f"{stem}_fit.json", "w", encoding="utf-8") as f:
        json.dump(fit, f, indent=2)

    overlay = Image.open(src_path).convert("RGBA")
    d = ImageDraw.Draw(overlay)
    d.rectangle([l, t, r, b], outline=(255, 0, 0, 255), width=6)
    for key, v in bosses.items():
        cx, cy, rad = v["cx"] * w, v["cy"] * h, v["r"] * w
        d.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], outline=(0, 255, 0, 255), width=6)
    overlay.save(f"{stem}_overlay.png")

    print(json.dumps(fit, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
