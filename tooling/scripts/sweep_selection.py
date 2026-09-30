#!/usr/bin/env python3
"""Rewrite every authored target's `chosenBy` + `count` into one written `selection` term.

Ruled 2026-08-18 (Sam): the two fields were never independent, and they collapse into a
single term with three forms — `all`, `player: N`, `random: N` — always written, never
defaulted. This is the transform that moved the authored corpus onto it.

It is IDEMPOTENT. A row that already carries a `selection` is counted and left alone, so a
second run prints the same tally and writes nothing. That is deliberate: the script is kept
so the sweep can be re-derived and re-checked rather than trusted, and its DIFF is the
finding. Re-run it after importing cards authored against the old shape.

Three data surfaces carry authored targets, and all three move together or the build breaks:

  * `unity/Assets/CardData/*.cards.json`   — the shipped sets
  * `tests/.../Fixtures/CardSets/*.cards.snapshot.json`

The oracle for the rewrite is NOT this script. It is `CardCi` rule C26, which regenerates
every card's text from its rules and compares it to the authored string — so a row whose
term changed what the card MEANS would change what it SAYS. Run it after:

    dotnet run --project src/Emberfall.Tools.CardCi -- \
        unity/Assets/CardData unity/Assets/Art/Cards

Exit 1 if a row cannot be given a term.
"""
import collections
import glob
import io
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]

# The pre-sweep defaults, reproduced here rather than imported, because the code they came
# from is deleted by the same change. TargetSpec.DefaultPlayerSelectors said which selectors
# meant "the player picks" when no chosenBy was authored; Targeting.RequiredAutomaticCount
# said what an absent count meant under each mode. Between them they are the whole of the
# fusion this term replaces, and keeping them here is what makes the transform auditable.
DEFAULT_PLAYER = {"ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ANY_MINION",
                  "ANY_FRIENDLY_MINION", "ANY_HAND_CARD", "ANY_DECK_CARD", "ANY_GRAVEYARD_CARD"}


class Unwritable(Exception):
    """A row whose old shape has no term BY DESIGN, rather than by oversight."""


def term(target):
    """The one written selection term for a pre-sweep target object."""
    selector, chosen_by, count = target["selector"], target.get("chosenBy"), target.get("count")
    if chosen_by == "PLAYER" or (chosen_by is None and selector in DEFAULT_PLAYER):
        if count == -1:
            raise Unwritable(f"{selector}: a player told to pick every candidate is not choosing")
        return "player: %d" % (1 if count is None else count)
    if count == -1 or (count is None and chosen_by != "RANDOM"):
        return "all"
    if chosen_by == "RANDOM":
        return "random: %d" % (1 if count is None else count)
    raise Unwritable(f"{selector}: an automatic finite count takes the first {count} in board "
                     f"order; the intent is 'random: {count}'")


def rewrite(target):
    """Rebuild the target dict with `selection` sitting where `chosenBy` used to."""
    value = term(target)
    out = {}
    for key, item in target.items():
        if key in ("chosenBy", "count"):
            continue
        out[key] = item
        if key == "selector":
            out["selection"] = value
    return out, value


def targets(node):
    """Yield (owner, key) for every authored target object, in document order."""
    if isinstance(node, dict):
        for key, item in list(node.items()):
            if key == "target" and isinstance(item, dict) and "selector" in item:
                yield node, key
            yield from targets(item)
    elif isinstance(node, list):
        for item in node:
            yield from targets(item)


def sweep(card, tally, log, label):
    """Rewrite one card in place. Returns True when anything changed."""
    changed = False
    for owner, key in targets(card):
        target = owner[key]
        if "selection" in target:
            tally[target["selection"]] += 1
            continue
        owner[key], value = rewrite(target)
        tally[value] += 1
        changed = True
        log.append("  %-46s %-22s %s" % (label, target["selector"], value))
    return changed


def sweep_card_files(paths, tally, log):
    """The pretty-printed set files and the golden snapshot: CRLF, two-space indent."""
    touched = 0
    for path in paths:
        raw = io.open(path, encoding="utf-8", newline="").read()
        cards = json.loads(raw)
        changed = False
        for card in cards:
            changed |= sweep(card, tally, log, f"{path.name}:{card['id']}")
        if not changed:
            continue
        out = (json.dumps(cards, indent=2, ensure_ascii=False) + "\n").replace("\n", "\r\n")
        io.open(path, "w", encoding="utf-8", newline="").write(out)
        touched += 1
    return touched


def main() -> int:
    sets = sorted((ROOT / "unity" / "Emberfall" / "Assets" / "CardData").glob("*.cards.json"))
    fixtures = sorted((ROOT / "tests" / "Emberfall.Core.Tests" / "Fixtures" / "CardSets")
                      .glob("*.cards.snapshot.json"))
    if not sets:
        print("no *.cards.json found — wrong repo root?", file=sys.stderr)
        return 2

    tally, log = collections.Counter(), []
    try:
        touched = sweep_card_files(sets + fixtures, tally, log)
    except Unwritable as unwritable:
        print(f"no selection term for this row by design — {unwritable}", file=sys.stderr)
        return 1

    if log:
        print("\n".join(log))
        print()
    print("files scanned  : %d set(s), %d fixture(s)" % (len(sets), len(fixtures)))
    print("files rewritten: %d" % touched)
    print("rows rewritten : %d" % len(log))
    print()
    for form, count in sorted(tally.items()):
        print("  %-12s %4d" % (form, count))
    print("  %-12s %4d" % ("TOTAL", sum(tally.values())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
