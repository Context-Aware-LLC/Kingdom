"""Build the board-surface master from a 16:9 plate or a composed ultrawide master.

The board's logical design box is 9.2 world units tall; the perspective camera
fits that box into the safe area and width flexes with aspect up to +-10.2 at
20:9 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md 1.1).
Legacy 16:9 plates are extended to the 20:9 span with horizontally MIRRORED
edge strips: pixel-continuous at the seam (a block touching the edge continues
into its reflection), and only ever visible on ultrawide displays. New plates
may instead be authored directly at the final 2.217:1 composition; those are
resized without adding or mirroring content.

Legacy preview-sized inputs are normalized to the board's 3696x2080 authored
frame before the bleed is added, so swapping in a concept image does not change
the runtime camera/layout contract.

Usage: build_board_master.py <plate_16x9_or_master_ratio.png>
Writes unity/Assets/Art/Backgrounds/BoardPlate_Master.png — a fixed
name, so swapping the board art is: generate a new plate at either supported ratio, rerun this.
"""

import pathlib
import sys

from PIL import Image

VISIBLE_H = 9.2    # 2 x BoardCamera ortho size 4.6
MAX_HALF_W = 10.2  # 20:9 span
SOURCE_W = 3696     # Existing 16:9 authored-frame contract.
OUTPUT_H = 2080
MASTER_ASPECT_TOLERANCE = 0.002

REPO = pathlib.Path(__file__).resolve().parents[2]
DEST = REPO / "unity" / "Assets" / "Art" / "Backgrounds" / "BoardPlate_Master.png"


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2

    src = Image.open(sys.argv[1]).convert("RGB")
    original_size = src.size
    target_w = round(OUTPUT_H * (2 * MAX_HALF_W) / VISIBLE_H)
    target_w += target_w % 2
    target_aspect = target_w / OUTPUT_H

    # New board art can be composed against the entire runtime plate. Do not squeeze it into
    # the legacy 16:9 frame and manufacture mirrored side strips: its perimeter furniture is
    # intentional content, not disposable bleed.
    if abs(src.width / src.height - target_aspect) <= MASTER_ASPECT_TOLERANCE:
        if src.size != (target_w, OUTPUT_H):
            src = src.resize((target_w, OUTPUT_H), Image.Resampling.LANCZOS)
        DEST.parent.mkdir(parents=True, exist_ok=True)
        src.save(DEST, optimize=True)
        print(f"{original_size[0]}x{original_size[1]} master-ratio source -> {DEST.name} "
              f"({target_w}x{OUTPUT_H}, {target_aspect:.3f}:1)")
        return 0

    if original_size != (SOURCE_W, OUTPUT_H):
        src = src.resize((SOURCE_W, OUTPUT_H), Image.Resampling.LANCZOS)
    w, h = src.size

    bleed = (target_w - w) // 2
    if bleed <= 0:
        print(f"input already covers 20:9 ({w}x{h})", file=sys.stderr)
        return 2

    canvas = Image.new("RGB", (target_w, h))
    canvas.paste(src.crop((0, 0, bleed, h)).transpose(Image.FLIP_LEFT_RIGHT), (0, 0))
    canvas.paste(src, (bleed, 0))
    canvas.paste(src.crop((w - bleed, 0, w, h)).transpose(Image.FLIP_LEFT_RIGHT), (bleed + w, 0))

    DEST.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(DEST, optimize=True)
    normalized = (f"{original_size[0]}x{original_size[1]} -> {w}x{h}; "
                  if original_size != (w, h) else "")
    print(f"{normalized}{w}x{h} + {bleed}px mirrored bleed each side -> {DEST.name} "
          f"({target_w}x{h}, {target_w/h:.3f}:1 vs target {2*MAX_HALF_W/VISIBLE_H:.3f}:1)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
