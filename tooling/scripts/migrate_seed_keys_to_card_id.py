#!/usr/bin/env python3
"""Rekey the hand-authored art seeds from `artId` onto the row's NUMERIC CARD ID.

RULING (Sam, 2026-08-27). "All of our card generation from now on should mint the seeds or the
IDs from the gate. When a brief is written, the ID is written with it, and that's the only
immutable thing."

WHY. Seeds are the quality constraint on generated art -- a card with no seed generates from
"[perspective unset]" and comes back weakly directed -- and they have now been stranded by a
rename TWICE. First keyed by HANDLE, where renaming the TEMPEST and STONEWARDEN handles orphaned
18 of them at a stroke and `stone_earthwardens_maul` had to be redrawn by hand. Then keyed by
`artId`, which is better but is still a name a designer may want to change: reclassing a card,
correcting a stale prefix, or renaming the art to match a redraw all move it. Each time, the
cards read as UNSEEDED and generation runs on placeholder direction with no error anywhere.

The numeric id is the one identifier that cannot move -- see the ruling in `CardDefinition`, whose
test is that you can change a handle and have nothing break. Keying direction on it ends the class
of bug rather than relocating it a third time.

WHAT DOES *NOT* MOVE, and this is the point of the split:

  * the PNG filename and its `.meta`      -- keyed on artId, because Addressables resolves the
                                             address `card_art/<fileName>` and the guid lives in
                                             the .meta. That coupling is real and stays.
  * the prompt-manifest key               -- the re-roll history OF AN IMAGE, so it belongs to the
                                             image's name, not to the card that happens to use it.
  * the art-pipeline candidate directory  -- likewise: it holds candidate images.

  * the SEED                              -- authored direction ABOUT A CARD. Moves here.

So after this, renaming an artId is three files that are genuinely about the image, and the words
an art lead wrote stay attached to the card no matter what it is called.

Refuses to write unless every existing key resolves to exactly one row, because a key that does
not is direction about to be lost -- which is the failure this whole change exists to end.

  python tooling/scripts/migrate_seed_keys_to_card_id.py --check
  python tooling/scripts/migrate_seed_keys_to_card_id.py --write

RETIREMENT TRIGGER: this is a one-shot. Delete it once `tooling/card-art-seeds/*.json` holds no
artId-shaped key -- `--check` reporting "already migrated" is that condition, and it is the only
thing this file is still good for.
"""
import argparse
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
SEEDS_DIR = ROOT / "tooling" / "card-art-seeds"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load_rows():
    rows = []
    for fp in sorted(CARD_DIR.glob("*.cards.json")):
        rows.extend(json.loads(fp.read_text(encoding="utf-8")))
    return rows


def resolve_index(rows):
    """{artId | handle | str(id) -> str(id)}, with the numeric id winning every collision."""
    index = {}
    for row in rows:
        cid = str(row["id"])
        if row.get("artId"):
            index.setdefault(row["artId"], cid)
        if row.get("handle"):
            index.setdefault(row["handle"], cid)
        index[cid] = cid            # last and unconditional: the id is the canonical key
    return index


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write", action="store_true", help="apply; without it, only report")
    args = ap.parse_args()

    rows = load_rows()
    index = resolve_index(rows)
    files = sorted(SEEDS_DIR.glob("*.json"))
    if not files:
        print(f"no seed files under {SEEDS_DIR.relative_to(ROOT)} — nothing to migrate.")
        return 0

    total = moved = already = 0
    unresolved = []
    collisions = []
    plans = []

    for fp in files:
        seeds = json.loads(fp.read_text(encoding="utf-8"))
        out = {}
        for key, seed in seeds.items():
            total += 1
            cid = index.get(key)
            if cid is None:
                unresolved.append((key, fp.name))
                continue
            if cid in out:
                collisions.append((key, cid, fp.name))
                continue
            if key == cid:
                already += 1
            else:
                moved += 1
            out[cid] = seed
        plans.append((fp, out))

    print(f"seed files          : {len(files)}")
    print(f"seed entries        : {total}")
    print(f"  already on card id: {already}")
    print(f"  to rekey          : {moved}")
    print(f"  unresolved        : {len(unresolved)}")
    print(f"  collisions        : {len(collisions)}")

    for key, fname in unresolved:
        print(f"UNRESOLVED {fname}: '{key}' matches no row's artId, handle or id")
    for key, cid, fname in collisions:
        print(f"COLLISION  {fname}: '{key}' -> {cid}, which another key already claimed")

    if unresolved or collisions:
        print("\nREFUSING: every key must resolve to exactly one row, or authored direction is "
              "lost in the rewrite. Fix the keys above (or the rows) and run again.")
        return 1

    if moved == 0:
        print("\nalready migrated — nothing to do. This script has discharged its purpose; "
              "see its RETIREMENT TRIGGER.")
        return 0

    if not args.write:
        print("\n--check only. Re-run with --write to apply.")
        return 0

    for fp, out in plans:
        # Sorted NUMERICALLY: these keys are numbers in string clothing, and a lexical sort puts
        # 1015 between 101 and 102, which makes every future diff of this file unreadable.
        ordered = {k: out[k] for k in sorted(out, key=int)}
        fp.write_text(json.dumps(ordered, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {fp.relative_to(ROOT)}: {len(ordered)} seeds")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
