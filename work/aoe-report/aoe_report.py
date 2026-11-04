"""List every card that damages or drowns a whole side, as a markdown table.

A selection that equals its selector's whole pool is "all": ANY_ENEMY 8 (seven minions and the
hero), ANY_ENEMY_MINION 7, ANY_MINION 14. Runs from any directory:
    python work/aoe-report/aoe_report.py
"""
import glob
import json
import sys
from pathlib import Path

# Anchored to this file, not the working directory: a cwd-relative glob run from anywhere but
# the repo root matched nothing and printed an empty table that read as "no area cards".
REPO = Path(__file__).resolve().parents[2]
CARD_DATA = str(REPO / "unity" / "Assets" / "CardData" / "*.cards.json")
WHOLE = {("ANY_ENEMY", "8"): "all enemies",
         ("ANY_ENEMY_MINION", "7"): "enemy minions",
         ("ANY_MINION", "14"): "all minions"}
CLASS_ORDER = ["NEUTRAL"]


def rows():
    paths = sorted(glob.glob(CARD_DATA))
    if not paths:
        sys.exit(f"no card files match {CARD_DATA}")
    for path in paths:
        for card in json.load(open(path, encoding="utf-8")):
            backdraft, hits = 0, []
            for rule in card.get("rules", []):
                action = rule.get("action", {})
                trigger = rule.get("activation", {}).get("trigger", "")
                if action.get("type") == "BACKDRAFT":
                    backdraft += action.get("amount", 0)
                target = action.get("target") or {}
                scope = WHOLE.get((target.get("selector"), str(target.get("selection"))))
                if action.get("type") in ("DAMAGE", "DROWN") and scope:
                    what = "Drown" if action["type"] == "DROWN" else str(action.get("amount"))
                    hits.append((what, scope, trigger))
            if hits:
                yield card, backdraft, hits


def main():
    out = sorted(rows(), key=lambda r: (r[0]["class"] != "NEUTRAL", r[0]["class"],
                                        r[0].get("cost", 0) + r[1], r[0]["id"]))
    print("| class | card (id) | type | rarity | cost | backdraft | effective | body | hits | text |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for card, backdraft, hits in out:
        body = f"{card.get('attack')}/{card.get('health')}" if card["type"] == "MINION" else ""
        hit = "; ".join(f"{w} to {s} ({t})" for w, s, t in hits)
        name = card["name"] + ("" if card.get("collectible") else " *uncollectible*")
        cost = card.get("cost", 0)
        print(f"| {card['class']} | {name} ({card['id']}) | {card['type'].lower()} | "
              f"{card.get('rarity', '').lower()} | {cost} | {backdraft or ''} | {cost + backdraft} | "
              f"{body} | {hit} | {card.get('text', '')} |")
    print(f"\n{len(out)} cards.")


if __name__ == "__main__":
    main()
