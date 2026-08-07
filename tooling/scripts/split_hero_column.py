"""Turn a cut hero-column plate on its side and split it into two square frames.

Sam's revision (2026-07-25): the hero power owns its own frame, and the weapon
frame DOCKS onto it when a weapon is played. Rather than regenerate, we reuse the
generated column — rotate it 90 degrees clockwise and cut it down the middle.

The rotation direction matters. Clockwise sends the column's LOWER socket to the
LEFT and its UPPER socket to the RIGHT, which is the arrangement we want: hero
power inboard against the hero portrait, weapon outboard where it can appear and
disappear without moving anything else.

The split is clean because of where the generation contract put things. After a
clockwise rotation the sockets sit at 27% and 73% of the width and both stat
bosses land at 60% — so a cut at 50% puts the hero-power socket alone in the left
half and the weapon socket WITH its attack/durability bosses in the right half.
Nothing that matters straddles the seam; only the connecting strut is severed,
which is what makes the two pieces read as parts that join.

Each half is square: 1024 x 1024, drawn at 1.35 x 1.35 world units (3/4 of the
1.8 hero plate).

Usage: split_hero_column.py [name ...]   (default: every cut HeroColumn_*)
"""

import pathlib
import sys

from PIL import Image

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "ui" / "board" /"hp-frames"
UNITY = (pathlib.Path(__file__).resolve().parents[2]
         / "unity" / "Emberfall" / "Assets" / "Art" / "UI" / "Board" / "HpFrames")


def split(path: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    im = Image.open(path).convert("RGBA")
    # Clockwise 90: PIL rotates counter-clockwise for positive angles, so -90.
    rot = im.rotate(-90, expand=True)
    W, H = rot.size

    mid = W // 2
    left = rot.crop((0, 0, mid, H))       # hero power — inboard
    right = rot.crop((mid, 0, W, H))      # weapon + stat bosses — outboard

    # Material name: HeroColumn_IronEmber_nocost -> IronEmber_nocost
    stem = path.stem.replace("HeroColumn_", "").replace("_cut", "")
    lp = SRC / f"HeroPowerFrame_{stem}.png"
    rp = SRC / f"WeaponFrame_{stem}.png"
    left.save(lp)
    right.save(rp)
    return lp, rp


def main() -> int:
    names = sys.argv[1:]
    files = ([SRC / f"{n}.png" for n in names] if names
             else sorted(SRC.glob("HeroColumn_*_cut.png")))
    if not files:
        print(f"no cut plates in {SRC} — run cut_hero_column.py first", file=sys.stderr)
        return 1

    UNITY.mkdir(parents=True, exist_ok=True)
    for f in files:
        if not f.exists():
            print(f"missing {f}", file=sys.stderr)
            return 2
        lp, rp = split(f)
        for p in (lp, rp):
            (UNITY / p.name).write_bytes(p.read_bytes())
            print(f"{p.name}  {Image.open(p).size}")

    print(f"\n-> {UNITY}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
