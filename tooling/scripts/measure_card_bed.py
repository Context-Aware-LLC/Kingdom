"""Measure the parchment "bed" (rules-text panel) in each card frame.

`card-rendering.md §3.1` sizes the rules body against "the bed's inner height"
and states that height is **unmeasured**. This measures it, per rarity, so the
wrap box can be a figure instead of a guess.

DETECTION. `flatten_parchment.py` already separates bed from frame, but only by
an ABSOLUTE luminance floor (`LUMA_FLOOR` 70, dropped to 40 for the two dark
beds) inside a hardcoded band — it never measures the bed's extent, and needing
two constants is the tell that an absolute floor doesn't generalise. The same
bright-parchment-vs-dark-metal idea is reused here, made per-frame adaptive by
taking the threshold as a fraction of each bed's own plateau:

  1. Luminance L = 0.299R + 0.587G + 0.114B; the cut art window (alpha < 128) is
     forced to L = 0 so it can never be mistaken for bed.
  2. Row profile P(y) = MEDIAN of L over the central columns x in [0.35w, 0.65w).
     Central-only keeps the side rails out (they inset at most 0.10w) and clears
     all three corner bosses (the widest reaches x = 0.245w / 0.755w). Median,
     not mean, so ornament painted ON the bed can't drag a row down.
  3. P is box-smoothed over SMOOTH rows (edge-replicated).
  4. Plateau = max over y of min(P) on a PLATEAU_RUN-row window centred on y — a
     morphological erosion, so the level must be SUSTAINED. A plain max picks
     the specular highlight on DarkGilt's gold rail (a ~14-row spike at ~2x the
     bed's own level) and throws the whole measurement out.
  5. Bed = the contiguous row run containing the plateau where P >= f * plateau,
     edges linearly interpolated between bracketing rows. f = 0.50 (half-max) is
     the headline figure; f = 0.65 / 0.35 / 0.25 are reported alongside it as the
     uncertainty envelope. Beds painted with a vignette (DarkGilt, IronEmber)
     have no hard bottom edge at all, and the width of that envelope is the
     honest statement of it.
  6. Inner width: per bed row, the contiguous column run containing x = w/2 with
     L >= 0.50 * plateau; reported as the median over the bed rows.

What this measures is the parchment light enough to take dark rules ink, which
is the quantity the wrap box needs. It is deliberately NOT the painted panel's
full rail-to-rail extent: on the vignetted beds the panel keeps going into
near-black, where text would be unreadable.

The measurement is therefore NOT treatment-invariant — `flatten_parchment.py`'s
retint moves it, by design. Frame GEOMETRY is compared separately and directly,
by diffing master against shipped: identical alpha plus a colour-diff bounding
box confined to the flatten band proves the geometry is untouched.

Deterministic: pure threshold arithmetic on the PNGs, no randomness, no
per-frame tuning.

Usage:
    python measure_card_bed.py [--overlay <path.png>] [--body-pct 5.27]
                               [--leading 1.20]
"""

import argparse
import pathlib

from PIL import Image, ImageChops, ImageDraw

REPO = pathlib.Path(r"c:\Users\User\Documents\tcg-hearthlike")
MASTERS = REPO / "art-bakeoff" / "frames"
SHIPPED = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "Frames"

# rarity -> (master stem, shipped stem). Sam's untouched GIMP cut is the master;
# the pre-cut `Frame_<Name>.png` is not kept on disk, only `_cut.png`.
FRAMES = {
    "common": ("Frame_CarvedStone_cut", "frame_common"),
    "rare": ("Frame_DarkGilt_cut", "frame_rare"),
    "epic": ("Frame_IronEmber_cut", "frame_epic"),
    "legendary": ("Frame_AgedGilt_cut", "frame_legendary"),
}

COL_LO, COL_HI = 0.35, 0.65   # central column band, clear of rails and bosses
SMOOTH = 9                    # box-filter width in rows (odd)
PLATEAU_RUN = 101             # rows the plateau level must be sustained over
ALPHA_CUT = 128
# plateau fractions: (key, f). "main" is the headline half-max figure.
THRESHOLDS = (("tight", 0.65), ("main", 0.50), ("loose", 0.35), ("env", 0.25))

# The current spec's wrap box: band_cy 0.70h, height 0.21h (card-rendering.md §3.1).
WRAP_TOP, WRAP_BOT = 0.595, 0.805


def luma_rows(img):
    """Per-row median luminance over the central columns, plus the art-window
    bottom (last row that is majority transparent in that band)."""
    w, h = img.size
    px = img.load()
    x0, x1 = int(COL_LO * w), int(COL_HI * w)
    prof, window_bottom = [], 0
    for y in range(h):
        vals, clear = [], 0
        for x in range(x0, x1):
            r, g, b, a = px[x, y]
            if a < ALPHA_CUT:
                vals.append(0.0)
                clear += 1
            else:
                vals.append(0.299 * r + 0.587 * g + 0.114 * b)
        vals.sort()
        prof.append(vals[len(vals) // 2])
        if clear * 2 > (x1 - x0):
            window_bottom = y
    return prof, window_bottom


def smooth(prof, k=SMOOTH):
    n, half = len(prof), k // 2
    out = []
    for i in range(n):
        lo, hi = max(0, i - half), min(n, i + half + 1)
        seg = ([prof[0]] * (half - (i - lo))) + prof[lo:hi] \
            + ([prof[-1]] * (half - (hi - 1 - i)))
        out.append(sum(seg) / len(seg))
    return out


def erode(prof, lo, hi, k):
    """out[y] = min P over a k-row window: the plateau must be SUSTAINED."""
    half = k // 2
    return [min(prof[max(lo, y - half):min(hi + 1, y + half + 1)])
            for y in range(lo, hi + 1)]


def crossing(prof, y_from, thresh, step):
    """Walk from y_from in direction `step` to the last row still >= thresh;
    return the sub-row interpolated crossing."""
    n = len(prof)
    y = y_from
    while 0 <= y + step < n and prof[y + step] >= thresh:
        y += step
    y2 = y + step
    if not (0 <= y2 < n) or prof[y] == prof[y2]:
        return float(y)
    return y + step * (prof[y] - thresh) / (prof[y] - prof[y2])


def measure(path):
    img = Image.open(path).convert("RGBA")
    w, h = img.size
    raw, window_bottom = luma_rows(img)
    prof = smooth(raw)

    lo, hi = window_bottom + 1, h - 1
    er = erode(prof, lo, hi, PLATEAU_RUN)
    plateau = max(er)
    peak_y = lo + er.index(plateau)

    res = {"size": (w, h), "window_bottom": window_bottom / h,
           "peak_y": peak_y / h, "plateau": plateau}
    for key, f in THRESHOLDS:
        t = f * plateau
        res[f"top_{key}"] = crossing(prof, peak_y, t, -1) / h
        res[f"bot_{key}"] = crossing(prof, peak_y, t, +1) / h

    # inner width across the bed rows, at the same half-max threshold
    px = img.load()
    t = 0.50 * plateau
    cx = w // 2
    widths = []
    for y in range(int(res["top_main"] * h), int(res["bot_main"] * h) + 1):
        def bright(x, yy=y):
            r, g, b, a = px[x, yy]
            return a >= ALPHA_CUT and (0.299 * r + 0.587 * g + 0.114 * b) >= t
        if not bright(cx):
            continue
        xl = xr = cx
        while xl - 1 >= 0 and bright(xl - 1):
            xl -= 1
        while xr + 1 < w and bright(xr + 1):
            xr += 1
        widths.append(xr - xl + 1)
    widths.sort()
    # p10/p90, not min/max: the single rows at the very bed edge degenerate to a
    # few px as the threshold bites, and are not a width anyone can use.
    def pct(p):
        return (widths[min(len(widths) - 1, int(p * len(widths)))] / w) if widths else 0.0
    res["width_med"] = pct(0.50)
    res["width_p10"] = pct(0.10)
    res["width_p90"] = pct(0.90)
    return res


def geometry_diff(master_path, shipped_path):
    """Is the shipped frame the master with only a colour treatment applied?
    Returns (alpha_identical, rgb_diff_bbox_as_fractions, changed_px)."""
    a = Image.open(master_path).convert("RGBA")
    b = Image.open(shipped_path).convert("RGBA")
    w, h = a.size
    alpha_same = ImageChops.difference(a.getchannel("A"), b.getchannel("A")).getbbox() is None
    diff = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
    mask = diff.point(lambda v: 255 if v > 2 else 0)
    bbox = mask.getbbox()
    changed = sum(mask.histogram()[1:])
    if bbox is None:
        return alpha_same, None, 0
    l, t, r, bo = bbox
    return alpha_same, (l / w, t / h, r / w, bo / h), changed


def lines_fit(bed_h, body_pct, leading):
    return int(bed_h // (body_pct / 100.0 * leading))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--overlay", default=None, help="write a visual-check sheet here")
    ap.add_argument("--body-pct", type=float, default=5.27,
                    help="rules body size as %%h (card-rendering.md 3.1)")
    ap.add_argument("--leading", type=float, default=1.20,
                    help="line pitch as a multiple of body size (single-spaced)")
    args = ap.parse_args()

    results = {r: {"master": measure(MASTERS / f"{m}.png"),
                   "shipped": measure(SHIPPED / f"{s}.png")}
               for r, (m, s) in FRAMES.items()}

    h = 2048
    pitch = args.body_pct * args.leading / 100
    print("\nCard bed — half-max of the central-column (0.35w..0.65w) median row profile.")
    print(f"Body {args.body_pct}%h, leading x{args.leading} => line pitch {pitch:.4f}h "
          f"({pitch * h:.0f}px)\n")

    hdr = (f"{'frame':<10} {'src':<8} {'bed top':>16} {'bed bottom':>16} {'height':>8} "
           f"{'width':>7} {'lines':>6}   envelope top / bottom (0.65..0.25 plateau)")
    print(hdr); print("-" * len(hdr))
    for rarity in FRAMES:
        for src in ("shipped", "master"):
            r = results[rarity][src]
            t, b = r["top_main"], r["bot_main"]
            print(f"{rarity:<10} {src:<8} {t:.4f} ({t * h:6.0f}) {b:.4f} ({b * h:6.0f}) "
                  f"{b - t:8.4f} {r['width_med']:7.4f} "
                  f"{lines_fit(b - t, args.body_pct, args.leading):6d}   "
                  f"{r['top_tight']:.4f}..{r['top_env']:.4f} / "
                  f"{r['bot_env']:.4f}..{r['bot_tight']:.4f}")
        rs, rm = results[rarity]["shipped"], results[rarity]["master"]
        print(f"{'':<19} plateau luma shipped {rs['plateau']:.0f} / master "
              f"{rm['plateau']:.0f}   inner width p10..p90 {rs['width_p10']:.4f}"
              f"..{rs['width_p90']:.4f} (median {rs['width_med']:.4f})")
        print()

    print("\nGeometry: is shipped == master + colour only?")
    for rarity, (m, s) in FRAMES.items():
        alpha_same, bbox, n = geometry_diff(MASTERS / f"{m}.png", SHIPPED / f"{s}.png")
        bb = "none" if bbox is None else \
            f"x {bbox[0]:.4f}..{bbox[2]:.4f}  y {bbox[1]:.4f}..{bbox[3]:.4f}"
        print(f"  {rarity:<10} alpha identical: {str(alpha_same):<5}  "
              f"RGB-changed px: {n:>9,}  bbox {bb}")

    box_h = WRAP_BOT - WRAP_TOP
    n_box = lines_fit(box_h, args.body_pct, args.leading)
    print(f"\nvs the current wrap box {WRAP_TOP}..{WRAP_BOT} ({box_h:.4f}h, "
          f"{n_box} lines) — SHIPPED frames:")
    for rarity in FRAMES:
        r = results[rarity]["shipped"]
        t, b = r["top_main"], r["bot_main"]
        d = (b - t) - box_h
        v = "TALLER " if d > 0.002 else ("SHORTER" if d < -0.002 else "same   ")
        print(f"  {rarity:<10} bed {t:.4f}..{b:.4f}  {v} by {d:+.4f}h ({d * h:+5.0f}px)"
              f"   top {t - WRAP_TOP:+.4f}h  bottom {b - WRAP_BOT:+.4f}h"
              f"   lines {n_box} -> {lines_fit(b - t, args.body_pct, args.leading)}")

    it = max(results[r]["shipped"]["top_main"] for r in FRAMES)
    ib = min(results[r]["shipped"]["bot_main"] for r in FRAMES)
    print(f"\nONE box for all four rarities = the intersection: {it:.4f}..{ib:.4f} "
          f"({ib - it:.4f}h, {lines_fit(ib - it, args.body_pct, args.leading)} lines) "
          f"— bounded by the two vignetted beds. Per-rarity boxes are worth "
          f"{max(lines_fit(results[r]['shipped']['bot_main'] - results[r]['shipped']['top_main'], args.body_pct, args.leading) for r in FRAMES)} "
          f"lines on the best frame.")

    if args.overlay:
        write_overlay(pathlib.Path(args.overlay), results)
        print(f"\noverlay -> {args.overlay}")
    return 0


def write_overlay(out, results):
    tw, th = 420, 590
    sheet = Image.new("RGB", (tw * 4, th * 2 + 44), (18, 18, 22))
    dd = ImageDraw.Draw(sheet)
    for col, rarity in enumerate(FRAMES):
        for row, src in enumerate(("shipped", "master")):
            stem = FRAMES[rarity][1] if src == "shipped" else FRAMES[rarity][0]
            p = (SHIPPED if src == "shipped" else MASTERS) / f"{stem}.png"
            im = Image.open(p).convert("RGBA")
            bg = Image.new("RGBA", im.size, (255, 0, 255, 255))
            bg.alpha_composite(im)
            im = bg.convert("RGB").resize((tw, th))
            d = ImageDraw.Draw(im)
            r = results[rarity][src]
            for key in ("top_tight", "top_loose", "top_env",
                        "bot_tight", "bot_loose", "bot_env"):
                y = int(r[key] * th)
                d.line([(0, y), (tw, y)], fill=(255, 170, 0), width=1)
            for f, c in ((WRAP_TOP, (255, 40, 40)), (WRAP_BOT, (255, 40, 40))):
                y = int(f * th)
                d.line([(0, y), (tw, y)], fill=c, width=2)
                d.text((tw - 74, y + 2), f"box {f}", fill=c)
            wm = r["width_med"]
            x0, x1 = int((0.5 - wm / 2) * tw), int((0.5 + wm / 2) * tw)
            yt, yb = int(r["top_main"] * th), int(r["bot_main"] * th)
            d.rectangle([x0, yt, x1, yb], outline=(0, 255, 90), width=3)
            d.line([(0, yt), (tw, yt)], fill=(0, 255, 90), width=2)
            d.line([(0, yb), (tw, yb)], fill=(0, 255, 90), width=2)
            d.text((4, yt - 14), f"bed top {r['top_main']:.4f}", fill=(0, 255, 90))
            d.text((4, yb + 3), f"bed bot {r['bot_main']:.4f}", fill=(0, 255, 90))
            d.text((4, (yt + yb) // 2), f"w {wm:.4f}", fill=(0, 255, 90))
            sheet.paste(im, (col * tw, 22 + row * th))
        dd.text((col * tw + 6, 3), f"{rarity}", fill=(255, 255, 255))
        dd.text((col * tw + 6, 12), "top: shipped   bottom: master   "
                                    "green=bed(0.50) orange=envelope red=wrap box",
                fill=(160, 160, 170))
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)


if __name__ == "__main__":
    raise SystemExit(main())
