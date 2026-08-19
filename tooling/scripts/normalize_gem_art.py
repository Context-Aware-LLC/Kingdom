"""Normalise rarity-gem PNGs so one size constant renders all four consistently.

WHY THIS EXISTS. A gem is drawn into the frame's bottom-centre socket at a size the card
layout picks (FrameRarityCatalog.GemSocket / GemHeight). That size applies to the TEXTURE, so
a gem whose art fills 96% of its canvas renders visually huge next to one that fills 37% of
its own -- which is exactly the spread the four gems arrived with on 2026-08-19:

    gem_legendary  200x200  content 192x200   96% of width
    gem_epic       501x500  content 314x318   63%
    gem_rare       800x800  content 430x418   54%
    gem_common     679x536  content 250x251   37%

Nothing at runtime can see that: a SpriteRenderer sizes by the sprite rect, and the padding is
inside it. So the fix belongs here, on the art, not in the layout.

WHAT IT DOES. Trims each PNG to its visible content, then re-pads it to a SQUARE canvas with the
content centred. After this every gem's art touches its canvas edge on the gem's longer axis, so
drawing all four at the same world size makes them the same visual size -- while each keeps its
own proportions (the rare is a square princess cut, the common and legendary are ovals, and that
is Sam's art choice, not drift to be flattened).

"Content" is any pixel with alpha above ALPHA_FLOOR, so a deliberate glow counts as part of the
gem -- gem_legendary's red halo reaches its canvas edge and is meant to. That gem's STONE
therefore reads a little smaller than the others', because its glow is occupying the margin.
That is the honest reading of the art; if it should read larger, crop the halo tighter at
source rather than special-casing it here.

RE-RUN IT whenever a gem is re-exported -- an export tool will pad again and the sizes will
drift apart again. It is idempotent: a already-normalised gem is left byte-identical.

Originals are copied to art-pipeline/gems/original/ before the first overwrite (that tree is
gitignored by design -- see CLAUDE.md -- so treat it as a convenience, not as a backup).

Usage:  python tooling/scripts/normalize_gem_art.py [--dry-run]
"""

import shutil
import sys
from pathlib import Path

from PIL import Image

GEMS = Path("unity/Emberfall/Assets/Art/UI/Gems")
ORIGINALS = Path("art-pipeline/gems/original")

# Below this an "edge" is compression noise or an invisible halo tail, not art.
ALPHA_FLOOR = 8


def content_box(img):
    """Bounding box of everything visible enough to count as art."""
    alpha = img.getchannel("A").point(lambda v: 255 if v > ALPHA_FLOOR else 0)
    return alpha.getbbox()


def normalise(path, dry_run):
    img = Image.open(path).convert("RGBA")
    before = img.size

    box = content_box(img)
    if box is None:
        return path.name, before, None, "EMPTY - no visible pixels, skipped"

    content = img.crop(box)
    cw, ch = content.size
    side = max(cw, ch)

    if before == (side, side) and (cw == side or ch == side):
        return path.name, before, (side, side), "already normalised"

    out = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    out.paste(content, ((side - cw) // 2, (side - ch) // 2))

    if not dry_run:
        ORIGINALS.mkdir(parents=True, exist_ok=True)
        backup = ORIGINALS / path.name
        if not backup.exists():
            shutil.copy2(path, backup)
        out.save(path)

    fill_before = 100.0 * cw / before[0]
    return path.name, before, (side, side), f"content {cw}x{ch} ({fill_before:.0f}% of old width)"


def main():
    dry_run = "--dry-run" in sys.argv

    gems = sorted(GEMS.glob("gem_*.png"))
    if not gems:
        print(f"no gems under {GEMS}", file=sys.stderr)
        return 1

    print(f"{'gem':<20}{'before':>12}{'after':>12}  note")
    print("-" * 76)
    for g in gems:
        name, before, after, note = normalise(g, dry_run)
        b = f"{before[0]}x{before[1]}"
        a = f"{after[0]}x{after[1]}" if after else "-"
        print(f"{name:<20}{b:>12}{a:>12}  {note}")
    print("-" * 76)
    print("DRY RUN - nothing written." if dry_run
          else f"written in place; originals copied to {ORIGINALS}/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
