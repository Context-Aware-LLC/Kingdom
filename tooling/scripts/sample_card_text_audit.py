"""Picks a representative sample of authored cards for the text-vs-behaviour audit.

The audit asks whether a card's authored sentence describes what the engine actually
does with its rules. Which cards you ask that of decides what you can find, so the
sample is STRATIFIED rather than taken off the top: it walks the corpus once, keys
each targeted card by (card type, action, selector, selection, trigger), and takes
one card per distinct key until the budget is spent, rarest key first.

Rarest-first is the point. Taking the commonest keys would return twenty near-copies
of the ANY_MINION / player: 1 shape that 73 rows already share, and a divergence that
shows up only on a sweep or a random pick would never appear in the sample.
"""
import json, collections, sys

SETS = ["unity/Assets/CardData/Emberfall.cards.json",
        "unity/Assets/CardData/Rekindling.cards.json"]


def load():
    cards = []
    for f in SETS:
        d = json.load(open(f, encoding="utf-8"))
        cards.extend(d["cards"] if isinstance(d, dict) and "cards" in d else d)
    return cards


def facets(card):
    """Every (action, selector, selection, trigger) a card's rules carry."""
    out = []
    for rule in card.get("rules", []):
        trigger = (rule.get("activation") or {}).get("trigger", "?")
        action = rule.get("action") or {}
        stack = [action]
        while stack:
            node = stack.pop()
            if isinstance(node, dict):
                target = node.get("target")
                if isinstance(target, dict) and isinstance(target.get("selector"), str):
                    out.append((node.get("type", "?"), target["selector"],
                                target.get("selection", "?"), trigger))
                stack.extend(v for v in node.values() if isinstance(v, (dict, list)))
            elif isinstance(node, list):
                stack.extend(node)
    return out


def main(budget=20):
    cards = load()
    targeted = [c for c in cards if facets(c)]

    by_key = collections.defaultdict(list)
    for card in targeted:
        for facet in facets(card):
            by_key[(card["type"],) + facet].append(card["id"])

    print(f"corpus: {len(cards)} cards, {len(targeted)} carrying a target")
    print(f"distinct (type, action, selector, selection, trigger) keys: {len(by_key)}")
    print()

    chosen, seen = [], set()
    for key in sorted(by_key, key=lambda k: (len(by_key[k]), k)):
        if len(chosen) >= budget:
            break
        for card_id in sorted(by_key[key]):
            if card_id not in seen:
                seen.add(card_id)
                chosen.append((card_id, key, len(by_key[key])))
                break

    print(f"sample of {len(chosen)}, rarest key first (rows = how many targets share the key):")
    print()
    for card_id, key, n in chosen:
        card_type, action, selector, selection, trigger = key
        print(f"  {card_id:<34} {card_type:<10} {trigger:<18} {action:<12} "
              f"{selector:<22} {selection:<10} x{n}")

    covered = {k[2] for k in (c[1] for c in chosen)}
    print()
    print(f"selectors covered by the sample: {len(covered)} of "
          f"{len({k[2] for k in by_key})} the corpus uses")
    print("  " + ", ".join(sorted(covered)))
    return [c[0] for c in chosen]


if __name__ == "__main__":
    ids = main(int(sys.argv[1]) if len(sys.argv) > 1 else 20)
    open("card_text_audit_sample.txt", "w", encoding="utf-8").write("\n".join(ids))
