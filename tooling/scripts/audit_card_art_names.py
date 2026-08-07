#!/usr/bin/env python3
"""Audit card-art filenames against the class of the row that points at them.

The convention (CLAUDE.md, "Expansion at the directory level, class at the filename level"):

    Assets/Art/Cards/<setId>/<classToken>[_<role>]_<name>.png
    Assets/Art/Cards/<setId>/[<role>_]<name>.png            for NEUTRAL

**The absence of a class token is what means neutral**, so a neutral row carrying one is just as
wrong as a classed row missing one. Class tokens are abbreviations that do not match their class
names, so this derives the map from the data rather than hardcoding it, and prints what it derived.

Also checks the hazard that has no error message: CardArtPostprocessor keys import settings off
`Assets/Art/Cards/<setId>/`, so a file sitting in the wrong set folder imports with the wrong
settings and nothing complains.

Exit 1 if any mismatch is found.
"""
import collections
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ART_ROOT = ROOT / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"

ROLES = ("hero_power", "token")


def rows():
    for path in sorted(CARD_DIR.glob("*.cards.json")):
        doc = json.loads(path.read_text(encoding="utf-8"))
        cards = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for card in cards:
            yield path.name, card


def first_segment(art_id):
    """Leading token of an artId, skipping a leading role prefix."""
    rest = art_id
    for role in ROLES:
        if rest.startswith(role + "_"):
            rest = rest[len(role) + 1:]
    return rest.split("_", 1)[0]


def main():
    all_rows = [(f, c) for f, c in rows() if c.get("artId")]

    # Derive classToken from the data: the dominant leading segment per non-NEUTRAL class.
    by_class = collections.defaultdict(collections.Counter)
    for _, card in all_rows:
        cls = card.get("class", "NEUTRAL")
        if cls != "NEUTRAL":
            by_class[cls][first_segment(card["artId"])] += 1

    token_of = {}
    print("derived class tokens (dominant leading segment per class):")
    for cls in sorted(by_class):
        counts = by_class[cls].most_common()
        token, n = counts[0]
        token_of[cls] = token
        total = sum(c for _, c in counts)
        print(f"  {cls:<12} -> {token:<8} ({n}/{total} rows)")
    known_tokens = {t: c for c, t in token_of.items()}
    print()

    findings = []
    for file_name, card in all_rows:
        cls = card.get("class", "NEUTRAL")
        set_id = card.get("setId", "CORE")
        art = card["artId"]
        seg = first_segment(art)
        owner = known_tokens.get(seg)          # class this leading token belongs to, if any
        expected = token_of.get(cls)           # None for NEUTRAL

        if cls == "NEUTRAL" and owner is not None:
            findings.append((card["id"], cls, set_id, art,
                             f"NEUTRAL row carries the {owner} class token '{seg}'"))
        elif cls != "NEUTRAL" and owner != cls:
            if owner is None:
                findings.append((card["id"], cls, set_id, art,
                                 f"{cls} row has no class token (expected '{expected}_')"))
            else:
                findings.append((card["id"], cls, set_id, art,
                                 f"{cls} row carries the {owner} token '{seg}' (expected '{expected}_')"))

    # Where the file actually sits vs the setId the row declares.
    on_disk = {}
    for png in ART_ROOT.rglob("*.png"):
        on_disk.setdefault(png.stem, []).append(png.parent.name)

    misplaced, missing = [], []
    for file_name, card in all_rows:
        art, set_id = card["artId"], card.get("setId", "CORE")
        folders = on_disk.get(art)
        if not folders:
            missing.append((card["id"], set_id, art))
        elif set_id not in folders:
            misplaced.append((card["id"], set_id, art, ", ".join(sorted(folders))))

    print(f"card rows with an artId : {len(all_rows)}")
    print(f"png files under Art/Cards: {sum(len(v) for v in on_disk.values())}")
    print(f"class-token mismatches  : {len(findings)}")
    print(f"wrong set folder        : {len(misplaced)}")
    print(f"artId with no png       : {len(missing)}")

    if findings:
        print()
        print("CLASS-TOKEN MISMATCHES")
        print(f"{'card id':<36} {'class':<12} {'set':<11} {'artId':<34} problem")
        print("-" * 130)
        for row in sorted(findings):
            print(f"{row[0]:<36} {row[1]:<12} {row[2]:<11} {row[3]:<34} {row[4]}")

    if misplaced:
        print()
        print("WRONG SET FOLDER (imports with the wrong settings, silently)")
        print(f"{'card id':<36} {'row setId':<11} {'artId':<34} found in")
        print("-" * 110)
        for row in sorted(misplaced):
            print(f"{row[0]:<36} {row[1]:<11} {row[2]:<34} {row[3]}")

    return 1 if (findings or misplaced) else 0


if __name__ == "__main__":
    sys.exit(main())
