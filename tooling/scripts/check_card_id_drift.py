"""Fail on a card id that DISAPPEARED or an identity that CHANGED. Never on one that was added.

Usage:
  python tooling/scripts/check_card_id_drift.py            # compare working tree against HEAD
  python tooling/scripts/check_card_id_drift.py --base <rev>

Why this exists, and why it is not a pinned count
-------------------------------------------------
A card id is a foreign key into data no test can see. `DeckList.Entries` persists `CardId` to a
player's `decks.json` under `%LOCALAPPDATA%\\..\\LocalLow\\<company>\\<product>\\<accountGuid>\\`,
which is outside the repo entirely — so a repo-wide search-replace cannot reach it, `dotnet test`
cannot see it, and the bake cannot see it. Rename an id and every saved deck holding it keeps an
entry that resolves to nothing. The deckbuilder then HIDES that entry rather than showing it
(CollectionController's BindDeckRow sets DisplayStyle.None on an unresolvable row), so the deck
reads 30/30 with 29 cards drawn and the missing one cannot be counted, clicked or removed.

That happened on 2026-08-15 across seven saved decks, and again — unnoticed for six days — when
tide_glacierborn_leviathan became tide_glacial_leviathan on 2026-08-09.

The instrument this replaces was a hand-maintained corpus total (`Assert.That(all.Count,
Is.EqualTo(261))`). That shape is backwards: it fires on the most routine edit there is (adding a
card) and is silent on the one that costs you a day (changing an existing card). It trains you to
bump the number without reading it, which is exactly how it came to sit red through e0e942f0.

This check inverts that. Pure additions pass in silence. Removals and identity changes fail and
print WHAT moved, so the question at the failure is "did I mean this?" rather than "what is the
new number?".

Identity here is the same tuple the canonical projection pins — id, name, artId, type, class,
rarity, setId, tribe, collectible — because those are the fields a saved deck, an ownership row, a
replay or a telemetry event can be keyed on or rendered from.
"""

import argparse
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_FILES = (
    "unity/Emberfall/Assets/CardData/Emberfall.cards.json",
    "unity/Emberfall/Assets/CardData/Rekindling.cards.json",
)
IDENTITY_FIELDS = ("name", "artId", "type", "class", "rarity", "setId", "tribe", "collectible")


def load_working():
    rows = []
    for rel in CARD_FILES:
        rows.extend(json.loads((ROOT / rel).read_text(encoding="utf-8")))
    return {row["id"]: row for row in rows}


def load_base(rev):
    rows = []
    for rel in CARD_FILES:
        try:
            blob = subprocess.check_output(
                ["git", "show", f"{rev}:{rel}"], cwd=str(ROOT), text=True, encoding="utf-8"
            )
        except subprocess.CalledProcessError:
            # The file did not exist at the base rev — a new set file is an addition, not a drift.
            continue
        rows.extend(json.loads(blob))
    return {row["id"]: row for row in rows}


def identity(row):
    return {field: row.get(field) for field in IDENTITY_FIELDS}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="HEAD", help="revision to compare against (default HEAD)")
    args = parser.parse_args()

    base = load_base(args.base)
    work = load_working()
    if not base:
        print(f"CARD_ID_DRIFT=UNKNOWN  (no card data at {args.base}; nothing to compare)")
        return 0

    removed = sorted(set(base) - set(work))
    added = sorted(set(work) - set(base))
    changed = sorted(
        cid for cid in (set(base) & set(work)) if identity(base[cid]) != identity(work[cid])
    )

    print(f"base={args.base}  base_cards={len(base)}  working_cards={len(work)}")
    print(f"CARD_IDS_ADDED={len(added)}")
    for cid in added:
        print(f"  + {cid}  ({work[cid].get('name','')})")

    print(f"CARD_IDS_REMOVED={len(removed)}")
    for cid in removed:
        # A removal paired with an addition of the same display name is almost always a rename.
        twin = [a for a in added if work[a].get("name") == base[cid].get("name")]
        hint = f"   <-- likely renamed to {twin[0]}" if len(twin) == 1 else ""
        print(f"  - {cid}  ({base[cid].get('name','')}){hint}")

    print(f"CARD_IDENTITIES_CHANGED={len(changed)}")
    for cid in changed:
        before, after = identity(base[cid]), identity(work[cid])
        moved = [f for f in IDENTITY_FIELDS if before[f] != after[f]]
        for field in moved:
            print(f"  ~ {cid}.{field}: {before[field]!r} -> {after[field]!r}")

    if removed or changed:
        print()
        print("CARD_ID_DRIFT=FAIL")
        print(
            "A removed or repointed id breaks saved decks that no test can see. If this was\n"
            "deliberate, repair the save data in the same change:\n"
            "  - every decks.json under %LOCALAPPDATA%\\..\\LocalLow\\<company>\\<product>\\<guid>\\\n"
            "  - then re-run this check with the renames applied.\n"
            "Card ids are permanent by ruling (gh#86, 2026-08-07): retire a card, never repoint it."
        )
        return 1

    print()
    print("CARD_ID_DRIFT=CLEAN  (additions only)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
