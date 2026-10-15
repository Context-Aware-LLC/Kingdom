"""Ship a generated three-frame effect sheet from art-pipeline/ into the Unity project.

    python tooling/scripts/ship_spell_effect.py art-pipeline/spell-effects/fire-target-hit-3frame.png effect_spell_fire_impact
    python tooling/scripts/ship_spell_effect.py --defringe-only unity/Assets/Art/UI/Board/effect_spell_fire_impact.png

For a new sheet this writes, under unity/Assets/Art/UI/Board/:
  - <name>.png: resized to 1536x512 (three 512 frames side by side), defringed, bled.
  - <name>.png.meta: a fresh guid, every other setting copied from TEMPLATE_META.
and adds a `ui/<name>` entry to the UI_Core Addressables group. A sheet missing from that group
still draws in the Editor and is blank in a build.

Re-shipping a name that already exists overwrites the .png and leaves the .meta and the group
entry alone, because the .meta carries the guid the group points at (CLAUDE.md, "Two things about
art files").

It does not touch C#: add the key to SpellSchoolEffects.cs and the (name, guid) row to
SpellSchoolEffectAssetContractTests.cs, which this prints.

Why the two cleanups (2026-10-14):
  - Defringe. Generated sheets carry neon paint (pure red, cyan, blue) in their faint edge texels,
    at alpha 1-40. Blended over the board it is invisible; in any viewer showing colour without
    alpha it is a bright halo, and mipmapping can drag it inward. Faint texels take the colour of
    the nearest texel opaque enough to trust, and texels under CUT_ALPHA are cleared.
  - Bleed. Fully transparent texels take the colour of the nearest painted one, so bilinear
    filtering at the edge blends toward the paint, not toward whatever sat under the alpha.
"""

import argparse
import re
import sys
import uuid
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

REPO_ROOT = Path(__file__).resolve().parents[2]
BOARD_ART = REPO_ROOT / "unity/Assets/Art/UI/Board"
UI_CORE = REPO_ROOT / "unity/Assets/AddressableAssetsData/AssetGroups/UI_Core.asset"
TEMPLATE_META = BOARD_ART / "effect_spell_heal_travel.png.meta"
SIZE = (1536, 512)
TRUST_ALPHA = 48
CUT_ALPHA = 6


def defringe(rgba: np.ndarray) -> np.ndarray:
    """Clear near-invisible texels, recolour faint ones from the nearest trusted texel, and bleed
    that colour under the fully transparent ones. Alpha above CUT_ALPHA is never changed."""
    out = rgba.copy()
    alpha = out[..., 3]
    alpha[alpha < CUT_ALPHA] = 0
    trusted = alpha >= TRUST_ALPHA
    if not trusted.any():
        return out
    rows, cols = ndimage.distance_transform_edt(~trusted, return_distances=False, return_indices=True)
    nearest = out[..., :3][rows, cols]
    untrusted = ~trusted
    out[..., :3][untrusted] = nearest[untrusted]
    return out


def add_group_entry(group_text: str, guid: str, address: str) -> str:
    """Insert an entry in guid order, which is how Unity writes the group."""
    newline = "\r\n" if "\r\n" in group_text else "\n"
    entry = newline.join([
        f"  - m_GUID: {guid}",
        f"    m_Address: {address}",
        "    m_ReadOnly: 0",
        "    m_SerializedLabels: []",
        "    FlaggedDuringContentUpdateRestriction: 0",
        "",
    ])
    entries = list(re.finditer(r"  - m_GUID: ([0-9a-f]{32})\r?\n", group_text))
    if not entries:
        raise ValueError("no entries found in the group asset")
    later = [m for m in entries if m.group(1) > guid]
    if later:
        at = later[0].start()
    else:
        # After the last entry's final line.
        last = entries[-1]
        tail = re.compile(r"    FlaggedDuringContentUpdateRestriction: 0\r?\n")
        at = tail.search(group_text, last.end()).end()
    return group_text[:at] + entry + group_text[at:]


def ship(source: Path, name: str) -> str:
    png = BOARD_ART / f"{name}.png"
    meta = Path(str(png) + ".meta")
    sheet = np.array(Image.open(source).convert("RGBA").resize(SIZE, Image.LANCZOS))
    Image.fromarray(defringe(sheet)).save(png)

    if meta.exists():
        guid = re.search(r"guid: ([0-9a-f]{32})", meta.read_text(encoding="utf-8")).group(1)
        print(f"overwrote {png.name}; kept guid {guid}")
        return guid

    guid = uuid.uuid4().hex
    template = TEMPLATE_META.read_text(encoding="utf-8")
    text = re.sub(r"guid: [0-9a-f]{32}", f"guid: {guid}", template, count=1)
    text = re.sub(r"spriteID: [0-9a-f]{32}", f"spriteID: {uuid.uuid4().hex}", text)
    meta.write_text(text, encoding="utf-8", newline="\n")

    group = UI_CORE.read_text(encoding="utf-8")
    UI_CORE.write_bytes(add_group_entry(group, guid, f"ui/{name}").encode("utf-8"))
    print(f"shipped {png.name}, guid {guid}")
    print(f'  contract test row: ("{name}", "{guid}"),')
    return guid


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--defringe-only", action="store_true",
                        help="clean the given shipped PNGs in place, at their own size")
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args(argv)

    if args.defringe_only:
        for p in args.paths:
            image = np.array(Image.open(p).convert("RGBA"))
            Image.fromarray(defringe(image)).save(p)
            print(f"defringed {p}")
        return 0

    if len(args.paths) != 2:
        parser.error("give a source sheet and the shipped name, e.g. effect_spell_fire_impact")
    source, name = Path(args.paths[0]), args.paths[1]
    if not re.fullmatch(r"effect_[a-z0-9_]+", name):
        parser.error("the name is effect_<what>, lower case with underscores")
    ship(source, name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
