#!/usr/bin/env python3
"""Composition report for every deck in tooling/data/bot-roster.json.

Prints, per bot: legality (L1 size / L2-L3 copy caps / L4 class membership), the mana
curve, the type split, and the card list — plus the two things that decide whether a
deck plays well under the server's GreedyAi rather than merely being legal:

  * DEAD  — a card the AI can never use to any effect (see the flags below).
  * The curve's shape at 1-3 mana. GreedyAi plays the FIRST affordable card in HAND
    ORDER and never mulligans (`GreedyAi.Choose`), so an opening hand with nothing
    under 3 mana is simply passed turns; the curve is the AI's entire game plan.

Run from the repo root:  python tooling/scripts/bot_deck_report.py
"""
import json, glob, collections, sys, os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CARD_DIR = os.path.join(ROOT, "unity", "Emberfall", "Assets", "CardData")
ROSTER = os.path.join(ROOT, "tooling", "data", "bot-roster.json")


def load_cards():
    cards = {}
    for f in glob.glob(os.path.join(CARD_DIR, "*.json")):
        rows = json.load(open(f, encoding="utf-8"))
        if isinstance(rows, dict):
            rows = rows.get("cards", [])
        for r in rows:
            cards[r["id"]] = r
    return cards


def report(entry, cards):
    ids = entry["cards"]
    prim, sec = entry["primaryClass"], entry["secondaryClass"]
    print("=" * 78)
    print(f"{entry['displayName']}  ({entry['accountUsername']})  {prim}/{sec}")
    print("=" * 78)

    counts = collections.Counter(ids)
    problems = []
    if len(ids) != 30:
        problems.append(f"L1: {len(ids)} cards, need 30")
    for cid, n in counts.items():
        d = cards.get(cid)
        if d is None:
            problems.append(f"L4: unknown card {cid}")
            continue
        cap = 1 if d["rarity"] == "LEGENDARY" else 2
        if n > cap:
            problems.append(f"L2/L3: {n}x {d['name']} (cap {cap})")
        if d["class"] not in (prim, sec, "NEUTRAL"):
            problems.append(f"L4: {d['name']} is {d['class']}")
        if not d.get("collectible", True):
            problems.append(f"non-collectible: {d['name']}")
    print("legality:", "OK" if not problems else "; ".join(problems))

    curve = collections.Counter()
    types = collections.Counter()
    classes = collections.Counter()
    rar = collections.Counter()
    for cid in ids:
        d = cards[cid]
        curve[min(d["cost"], 7)] += 1
        types[d["type"]] += 1
        classes[d["class"]] += 1
        rar[d["rarity"]] += 1
    bars = " ".join(f"{c}m:{curve.get(c,0):<2}" for c in range(0, 8))
    print("curve:  ", bars, f" (7m = 7+)   avg {sum(cards[c]['cost'] for c in ids)/len(ids):.2f}")
    print("types:  ", dict(types), " classes:", dict(classes), " rarity:", dict(rar))
    print(f"  <=2 mana: {sum(curve[c] for c in range(0,3))}   <=3 mana: {sum(curve[c] for c in range(0,4))}"
          f"   >=5 mana: {sum(curve[c] for c in range(5,8))}")

    # Cards the AI cannot use. GreedyAi has no notion of holding a card back, so a
    # conditional payoff with no enabler in the deck is a blank it will pitch onto an
    # empty board at the first opportunity.
    dead = []
    for cid in sorted(set(ids)):
        d = cards[cid]
        why = ai_blindspot(d, ids, cards)
        if why:
            dead.append(f"  {counts[cid]}x {d['name']:<26} — {why}")
    print("AI blind spots:" if dead else "AI blind spots: none")
    for line in dead:
        print(line)

    print("  cards:")
    for cid in sorted(set(ids), key=lambda c: (cards[c]["cost"], c)):
        d = cards[cid]
        st = ""
        if d["type"] == "MINION":
            st = f"{d.get('attack')}/{d.get('health')}"
        elif d["type"] == "WEAPON":
            st = f"{d.get('attack')}/{d.get('durability')}"
        print(f"    {counts[cid]}x {d['cost']}m {st:>5} {d['class'][:4]:<4} {d['name'][:28]:<28} {d.get('text','')[:60]}")


def ai_blindspot(d, ids, cards):
    """Why this card is worth less than its cost to GreedyAi, or ''."""
    text = (d.get("text") or "").lower()
    tribes = collections.Counter(cards[c].get("tribe", "NONE") for c in ids)
    if "friendly construct" in text and tribes.get("CONSTRUCT", 0) < 6:
        return f"buffs Constructs; deck holds {tribes.get('CONSTRUCT',0)}"
    if "enemy beast" in text:
        return "hits only BEAST minions"
    if "sacrifice a friendly minion" in text:
        return "needs a friendly minion already on board"
    if "damaged" in text and "destroy" in text:
        return "needs an already-damaged minion"
    if "return a friendly minion" in text:
        return "AI returns its own board to hand for value it cannot use"
    return ""


def main():
    cards = load_cards()
    roster = json.load(open(ROSTER, encoding="utf-8"))
    for entry in roster:
        report(entry, cards)
    print("=" * 78)
    print(f"{len(roster)} bot(s). classes covered as primary:",
          ", ".join(sorted({e['primaryClass'] for e in roster})))


if __name__ == "__main__":
    main()
