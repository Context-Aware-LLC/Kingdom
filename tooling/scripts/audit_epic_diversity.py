#!/usr/bin/env python3
"""Report how much the EPIC slots repeat themselves, class by class.

Every class gets 6 epic slots (see the corpus target in docs/sam/). Six slots is few
enough that two cards doing the same thing is a real cost, and rarity alone cannot see
it: two cards can differ in cost, stats, keyword and name while resolving to the same
board action. So this classifies each epic by its RULES -- the action types and whether
they hit one thing or the whole board -- and reports the collisions.

The shape is derived, not authored. There is nothing in the card schema that says what a
card "is", and adding one would be another field to keep in sync, so this reads the rules
the resolver reads. Cards with no rules fall back to vanilla-body / keyword-body, which is
itself the finding worth seeing: a keyword-body epic is a statline wearing a rarity.

Two axes beyond duplicate shapes, both of which hid real clustering:

* **Cost spread.** A class whose epics all cost the same is undiversified even when every
  effect differs -- you never choose between them, you just play whichever you drew.
* **Token concentration.** SUMMON is the action most likely to be quietly reused, because
  a token is easy to bolt onto any card.

ADVISORY -- always exits 0. Repetition is a design finding for Sam, not breakage, and
nothing here should ever fail a build.

    python tooling/scripts/audit_epic_diversity.py             # every class
    python tooling/scripts/audit_epic_diversity.py --rarity RARE
    python tooling/scripts/audit_epic_diversity.py --markdown  # fenced, for pasting into docs/sam/
"""
import argparse
import collections
import glob
import json
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Assets" / "CardData"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# action type -> (one-target name, board-wide name).  A single name means breadth is
# meaningless for it: you cannot draw a card "at the whole board".
SHAPES = {
    "DAMAGE":         ("targeted-damage", "board-damage"),
    "MODIFY_STATS":   ("single-pump", "board-pump"),
    "GRANT_KEYWORD":  ("single-keyword", "board-keyword"),
    "RETURN_TO_HAND": ("bounce", "board-bounce"),
    "FROST":          ("freeze", "board-freeze"),
    "DESTROY":        ("hard-removal", "hard-removal"),
    "SUMMON":         ("tokens", "tokens"),
    "DRAW":           ("draw", "draw"),
    "HEAL":           ("heal", "heal"),
    "BACKDRAFT":      ("backdraft", "backdraft"),
    "GAIN_MANA":      ("ramp", "ramp"),
    "EQUIP":          ("equip", "equip"),
}


def load():
    rows = []
    for f in sorted(glob.glob(str(CARD_DIR / "*.cards.json"))):
        rows += json.loads(pathlib.Path(f).read_text(encoding="utf-8"))
    return [r for r in rows if r.get("collectible")]


def is_board(action):
    """Whether the action reaches more than one character."""
    target = action.get("target") or {}
    selector = target.get("selector", "")
    if not selector:
        return False
    return str(target.get("selection", "1")) != "1" or selector.startswith("ALL")


def shape(card):
    tags = []
    for rule in card["rules"]:
        action = rule["action"]
        names = SHAPES.get(action["type"])
        tag = names[1 if is_board(action) else 0] if names else action["type"].lower()
        if tag not in tags:
            tags.append(tag)
    if not tags:
        return "keyword-body" if card.get("keywords") else "vanilla-body"
    return "+".join(tags)


def statline(card):
    if card["type"] == "MINION":
        return "%s/%s" % (card.get("attack"), card.get("health"))
    if card["type"] == "WEAPON":
        return "%s/%s" % (card.get("attack"), card.get("durability"))
    return "-"


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rarity", default="EPIC")
    ap.add_argument("--markdown", action="store_true",
                    help="wrap each section in a fence, for pasting into a doc")
    args = ap.parse_args()

    cards = [c for c in load() if c["rarity"] == args.rarity]
    classes = sorted({c["class"] for c in cards})
    fence = "```" if args.markdown else ""

    clean, repeats = [], []
    if fence:
        print(fence)
    for cls in classes:
        group = sorted([c for c in cards if c["class"] == cls], key=lambda c: c["cost"])
        counts = collections.Counter(shape(c) for c in group)
        dupes = {s for s, n in counts.items() if n > 1}
        kinds = collections.Counter(c["type"] for c in group)
        kind_text = " ".join("%d %s" % (n, k.lower()) for k, n in sorted(kinds.items()))
        note = ""
        if dupes:
            note = "  <== " + ", ".join("%dx %s" % (counts[s], s) for s in sorted(dupes))
            repeats.append(cls)
        else:
            clean.append(cls)
        print("=== %-12s %d %s (%s)%s" % (cls, len(group), args.rarity.lower(), kind_text, note))
        for c in group:
            print("  %s%dc %-3s %-6s %-28s %-27s %s"
                  % ("*" if shape(c) in dupes else " ", c["cost"], c["type"][:3],
                     statline(c), c["handle"], shape(c), c.get("text") or ""))
        print()

    print("COST SPREAD  (a class whose epics all cost the same never makes you choose)")
    for cls in classes:
        costs = sorted(c["cost"] for c in cards if c["class"] == cls)
        counter = collections.Counter(costs)
        cost, n = counter.most_common(1)[0]
        flag = "   <== %d of %d at %dc" % (n, len(costs), cost) if n >= 3 else ""
        print("  %-13s%-28s%s" % (cls, costs, flag))

    print()
    print("TOKEN CONCENTRATION  (SUMMON is the easiest action to quietly reuse)")
    any_tokens = False
    for cls in classes:
        named = [c["handle"] for c in cards if c["class"] == cls
                 and any(r["action"]["type"] == "SUMMON" for r in c["rules"])]
        if len(named) >= 2:
            any_tokens = True
            group = [c for c in cards if c["class"] == cls]
            print("  %-13s%d of %d: %s" % (cls, len(named), len(group), ", ".join(named)))
    if not any_tokens:
        print("  none - no class reuses SUMMON more than once")

    print()
    print("CLEAN (every shape distinct): %s" % (", ".join(clean) or "none"))
    print("REPEATS                     : %s" % (", ".join(repeats) or "none"))
    print("RARITY_AUDITED=%s  CARDS=%d" % (args.rarity, len(cards)))
    if fence:
        print(fence)
    return 0


if __name__ == "__main__":
    sys.exit(main())
