"""Cut a static card-text font out of Google Sans Flex at chosen axis settings.

WHY THIS EXISTS. Unity 6000.5's text stack cannot drive a variable font's axes: TextCore's
FontEngine only reports IsVariableFontFace, with no way to set one, so TMP rasterises every
variable font at its DEFAULT instance. Google Sans Flex has six axes (weight, width, optical
size, grade, roundness, slant) and the whole point of using it for card text is choosing where
on them the rules box sits. So the choice is made here, before Unity sees the file: this pins
every axis and writes an ordinary static TTF.

HOW TO TUNE. Adjust the sliders in the Font Bench artifact on real cards at true size, copy the
command it prints, run it, and Unity picks up the new cut. The output is overwritten IN PLACE,
so its .meta and guid survive and every TMP font asset built on it keeps pointing at it. A
DYNAMIC TMP font asset re-rasterises from the source file, so no atlas regeneration is needed.

    python tooling/scripts/instance_font.py --wght 520 --wdth 96 --opsz 12 --GRAD 20
    python tooling/scripts/instance_font.py --show     # print what the current cut was made with

Any axis left out is pinned at the font's own default. The settings are written into the
output's name table (ID 3), which is what --show reads back.
"""
import argparse
import re
import sys
from pathlib import Path

from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

REPO = Path(__file__).resolve().parents[2]
SRC = REPO / "tooling/fonts/GoogleSansFlex-Variable.ttf"
OUT = REPO / "unity/Assets/Art/Fonts/GoogleSansFlex-Card.ttf"
FAMILY = "Google Sans Flex Card"
AXES = ("wght", "wdth", "opsz", "GRAD", "ROND", "slnt")
TAG = "axes:"


def show(path):
    if not path.exists():
        sys.exit(f"{path} does not exist yet -- run without --show to cut it.")
    uid = TTFont(path)["name"].getDebugName(3) or ""
    m = re.search(TAG + r"(.*)$", uid)
    print(m.group(1).strip() if m else f"{path.name} carries no axis record (name ID 3: {uid!r})")


def cut(src, out, requested):
    font = TTFont(src)
    fvar = {a.axisTag: a for a in font["fvar"].axes}
    pins = {}
    for tag in AXES:
        axis = fvar[tag]
        value = requested.get(tag)
        if value is None:
            value = axis.defaultValue
        elif not axis.minValue <= value <= axis.maxValue:
            sys.exit(f"{tag} {value:g} is outside {axis.minValue:g}..{axis.maxValue:g}")
        pins[tag] = value

    static = instantiateVariableFont(font, pins)
    record = " ".join(f"{t}={pins[t]:g}" for t in AXES)
    name = static["name"]
    for nid, value in ((1, FAMILY), (2, "Regular"), (4, f"{FAMILY} Regular"),
                       (6, FAMILY.replace(" ", "") + "-Regular"),
                       (3, f"{FAMILY.replace(' ', '')};{TAG} {record}")):
        name.removeNames(nameID=nid)
        name.setName(value, nid, 3, 1, 0x409)
    for nid in (16, 17):      # typographic family/subfamily would re-group it with the source
        name.removeNames(nameID=nid)

    out.parent.mkdir(parents=True, exist_ok=True)
    static.save(out)
    shown = out.relative_to(REPO) if out.is_relative_to(REPO) else out
    print(f"{shown}  {out.stat().st_size / 1024:.0f} KB")
    print(record)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    for tag in AXES:
        ap.add_argument(f"--{tag}", type=float)
    ap.add_argument("--src", type=Path, default=SRC)
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--show", action="store_true", help="print the settings the current cut was made with")
    args = ap.parse_args()
    if args.show:
        show(args.out)
        return
    cut(args.src, args.out.resolve(), {t: getattr(args, t) for t in AXES})


if __name__ == "__main__":
    main()
