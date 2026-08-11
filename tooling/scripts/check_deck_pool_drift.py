"""Warn when a staged card-row edit moves the DefaultDeckGenerator's output.

Usage:
  python tooling/scripts/check_deck_pool_drift.py            # staged vs HEAD
  python tooling/scripts/check_deck_pool_drift.py --worktree # working tree vs HEAD

The checked-in starter decks under tests/Emberfall.Core.Tests/Fixtures/DefaultDecks/ are a
byte-exact re-derivation of DefaultDeckGenerator over the live pool. Any edit that changes the
pool or the draw weights makes them stale, and the only way to fix that is to re-run the
[Explicit] DefaultDeckFixtureTests.Regenerate_Fixtures and commit the result.

WHICH FIELDS MATTER is read off DefaultDeckGenerator, not guessed:

  cost         -> Weight(cost) = cost >= 8 ? 1 : 9 - cost, the draw weight
  rarity       -> DeckValidator.MaxCopies(rarity), the per-card copy cap
  class        -> character.AllowsClass(def.Class) filters the candidate pool
  collectible  -> db.AllCollectible() is the pool
  type         -> feeds pool eligibility alongside collectible
  id           -> candidates are sorted by ordinal id, so an id decides draw order

setId, name, artId, keywords and rules are all absent from that list on purpose: they do not
reach the generator, so editing them cannot move a fixture and must not raise a false alarm.

ONLY Emberfall.cards.json is examined. LiveCardSet.Load reads exactly that one file, so a
Rekindling.cards.json edit cannot move a fixture today.

This is ADVISORY. It exits 0 whatever it finds — a stale fixture is caught for real by
DefaultDeckFixtureTests in the Unit lane. The point here is to tell you at commit time instead
of at CI time, because the fix is a command you have to already know about.

RETIREMENT TRIGGER: if DefaultDeckGenerator starts reading a card field outside POOL_FIELDS, or
LiveCardSet starts loading a second *.cards.json, add it here — this script goes quiet rather
than loud when it falls behind, which is the dangerous direction.
"""

import argparse
import json
import pathlib
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
LIVE_SET = "unity/Emberfall/Assets/CardData/Emberfall.cards.json"
POOL_FIELDS = ("id", "cost", "class", "rarity", "collectible", "type")


def git(*args):
    """Run a git command in the repo, returning stdout or None if the object is absent."""
    done = subprocess.run(
        ("git", "-C", str(ROOT)) + args,
        capture_output=True, text=True, encoding="utf-8",
    )
    return done.stdout if done.returncode == 0 else None


def projection(text):
    """The pool-relevant slice of a card file, keyed by id. None if it will not parse."""
    if text is None:
        return None
    try:
        rows = json.loads(text)
    except json.JSONDecodeError:
        return None
    return {row["id"]: tuple(row.get(f) for f in POOL_FIELDS) for row in rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worktree", action="store_true",
                        help="compare the working tree instead of the index")
    args = parser.parse_args()

    before = projection(git("show", f"HEAD:{LIVE_SET}"))
    if args.worktree:
        path = ROOT / LIVE_SET
        after = projection(path.read_text(encoding="utf-8")) if path.exists() else None
    else:
        after = projection(git("show", f":{LIVE_SET}"))

    # No baseline, no staged copy, or unparseable JSON: say nothing. A malformed card file is
    # CardCi's finding to report, and duplicating it here would only add noise to a real failure.
    if before is None or after is None:
        print("DECK_POOL_DRIFT=UNKNOWN")
        return 0

    changed = sorted(
        card_id for card_id in set(before) | set(after)
        if before.get(card_id) != after.get(card_id)
    )

    print(f"DECK_POOL_DRIFT_CARDS={len(changed)}")
    for card_id in changed:
        was, now = before.get(card_id), after.get(card_id)
        if was is None:
            print(f"  + {card_id} (added to the pool)")
        elif now is None:
            print(f"  - {card_id} (removed from the pool)")
        else:
            fields = ", ".join(
                f"{name} {w!r} -> {n!r}"
                for name, w, n in zip(POOL_FIELDS, was, now) if w != n
            )
            print(f"  ~ {card_id}: {fields}")

    if changed:
        print(
            "\nThese fields feed DefaultDeckGenerator, so the checked-in starter decks under\n"
            "tests/Emberfall.Core.Tests/Fixtures/DefaultDecks/ are probably stale now. Rebaseline:\n"
            '  dotnet test tests/Emberfall.Core.Tests --filter '
            '"FullyQualifiedName~DefaultDeckFixtureTests.Regenerate_Fixtures"\n'
            "then stage the fixtures it rewrites. Expect MORE than one class to move: the gate that\n"
            "catches this stops at the first stale class, so its message always undercounts."
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
