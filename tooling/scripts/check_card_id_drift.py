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
CARD_DATA_DIR = "unity/Assets/CardData"


def card_files(rev=None):
    """Every set file, by GLOB.

    A hardcoded pair sat here until 2026-08-24 and silently stopped covering CORE the day it became
    its own set file: a check that never looks at a file cannot report drift in it, and reports
    nothing, which reads exactly like a pass. The server globs this directory for the same reason.
    """
    if rev is None:
        return sorted(
            f"{CARD_DATA_DIR}/{p.name}"
            for p in (ROOT / CARD_DATA_DIR).glob("*.cards.json")
        )
    listing = subprocess.check_output(
        ["git", "ls-tree", "--name-only", rev, f"{CARD_DATA_DIR}/"],
        cwd=str(ROOT), text=True, encoding="utf-8",
    )
    return sorted(p for p in listing.splitlines() if p.endswith(".cards.json"))


IDENTITY_FIELDS = ("name", "artId", "type", "class", "rarity", "setId", "tribe", "collectible")


def load_working():
    rows = []
    for rel in card_files():
        rows.extend(json.loads((ROOT / rel).read_text(encoding="utf-8")))
    return {row["id"]: row for row in rows}


def load_base(rev):
    rows = []
    for rel in card_files(rev):
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

    # A REMOVED id and a CHANGED field are not the same failure and must not share a verdict.
    # A saved deck stores the numeric id and nothing else, so only a removal can strand one.
    # Renaming an artId -- 38 of them on 2026-08-24, when NEUTRAL gained its class token --
    # touches the art pipeline and the card face, and no save anywhere. Printing "repair every
    # decks.json" for that sends someone into player save data after a problem that is not
    # there, which is worse than saying nothing.
    if removed:
        print()
        print("CARD_ID_DRIFT=FAIL")
        print(
            "A REMOVED id breaks saved decks that no test can see. If this was deliberate,\n"
            "repair the save data in the same change:\n"
            "  - every decks.json under %LOCALAPPDATA%\\..\\LocalLow\\<company>\\<product>\\<guid>\\\n"
            "  - then re-run this check with the renames applied.\n"
            "Card ids are permanent by ruling (gh#86, 2026-08-07): retire a card, never repoint it."
        )
        return 1

    if changed:
        fields = sorted({f for cid in changed
                         for f in IDENTITY_FIELDS
                         if identity(base[cid])[f] != identity(work[cid])[f]})
        print()
        print("CARD_ID_DRIFT=CHANGED  (no id was removed; no saved deck is affected)")
        print(
            f"Fields moved: {', '.join(fields)}.\n"
            "Every id still resolves, so decks.json needs nothing. What these DO stale is the\n"
            "derived artifacts -- run rebake_card_data.py, and audit_card_art_names.py\n"
            "--check-seeds if an artId moved, since the seed corpus is keyed by it."
        )
        return 1

    print()
    print("CARD_ID_DRIFT=CLEAN  (additions only)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
