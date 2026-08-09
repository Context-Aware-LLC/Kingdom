#!/usr/bin/env python3
"""Aggregate the Emberfall card corpus into a single data file for the admin panel.

Reads the three set files under unity/Emberfall/Assets/CardData/ and emits
tooling/card-admin/data.json: a normalized per-card array plus precomputed
corpus-wide and per-class rollups (keyword frequency, targeting-selector
frequency, cost distribution, type split, and minion attack/health
distribution).

Regenerate any time the card data changes:  python tooling/card-admin/build_data.py

Follows the pattern of tooling/scripts/card_inventory.py: the printed stdout is
the deliverable summary; data.json is the machine-readable output the panel reads.
"""
import json
from collections import Counter, defaultdict
from pathlib import Path

# Repo root is two levels up from this file (tooling/card-admin/build_data.py).
REPO_ROOT = Path(__file__).resolve().parents[2]
CARD_FILES = [
    REPO_ROOT / "unity/Emberfall/Assets/CardData/M1.cards.json",
    REPO_ROOT / "unity/Emberfall/Assets/CardData/M2.cards.json",
    REPO_ROOT / "unity/Emberfall/Assets/CardData/M3.cards.json",
]
OUT_PATH = Path(__file__).resolve().parent / "data.json"
# Browsers block fetch('data.json') under file://, so the panel loads this thin
# wrapper instead (window.CARD_DATA = <same object>). data.json stays canonical.
OUT_JS_PATH = Path(__file__).resolve().parent / "data.js"

CARD_TYPES = ["MINION", "SPELL", "WEAPON", "HERO_POWER"]


def load_cards():
    """Return a flat list of normalized card records across all set files."""
    cards = []
    for path in CARD_FILES:
        with open(path, "r", encoding="utf-8") as f:
            entries = json.load(f)
        for c in entries:
            cards.append(normalize(c, path.name))
    return cards


def selectors_of(card):
    """Every targeting selector on a card (rules[].action.target.selector)."""
    sels = []
    for rule in card.get("rules", []) or []:
        action = rule.get("action")
        if not isinstance(action, dict):
            continue
        target = action.get("target")
        if isinstance(target, dict) and "selector" in target:
            sels.append(target["selector"])
    return sels


def normalize(card, source_file):
    """Flatten one raw card into the shape the panel consumes."""
    return {
        "id": card.get("id"),
        "name": card.get("name"),
        "type": card.get("type"),
        "cost": card.get("cost"),
        "attack": card.get("attack"),
        "health": card.get("health"),
        "durability": card.get("durability"),
        "class": card.get("class"),
        "rarity": card.get("rarity"),
        "keywords": list(card.get("keywords", []) or []),
        "selectors": selectors_of(card),
        "tribe": card.get("tribe"),
        "setId": card.get("setId"),
        "collectible": card.get("collectible"),
        "artId": card.get("artId"),
        "sourceFile": source_file,
    }


def rollup(cards):
    """Compute an aggregate bundle over an arbitrary list of cards."""
    keywords = Counter()
    selectors = Counter()
    cost = Counter()
    types = Counter()
    rarities = Counter()
    tribes = Counter()
    attack = Counter()
    health = Counter()

    for c in cards:
        for kw in c["keywords"]:
            keywords[kw] += 1
        for sel in c["selectors"]:
            selectors[sel] += 1
        if c["cost"] is not None:
            cost[c["cost"]] += 1
        if c["type"]:
            types[c["type"]] += 1
        if c["rarity"]:
            rarities[c["rarity"]] += 1
        if c["tribe"]:
            tribes[c["tribe"]] += 1
        if c["type"] == "MINION":
            if c["attack"] is not None:
                attack[c["attack"]] += 1
            if c["health"] is not None:
                health[c["health"]] += 1

    minion_count = types.get("MINION", 0)
    return {
        "total": len(cards),
        "typeSplit": {t: types.get(t, 0) for t in CARD_TYPES},
        "keywordFrequency": ordered_counter(keywords),
        "selectorFrequency": ordered_counter(selectors),
        "costDistribution": int_keyed(cost),
        "rarityBreakdown": ordered_counter(rarities),
        "tribeBreakdown": ordered_counter(tribes),
        "minionCount": minion_count,
        "attackDistribution": int_keyed(attack),
        "healthDistribution": int_keyed(health),
    }


def ordered_counter(counter):
    """Counter -> dict sorted by count desc, then name, for stable output."""
    return {k: v for k, v in sorted(counter.items(), key=lambda kv: (-kv[1], str(kv[0])))}


def int_keyed(counter):
    """Counter with int keys -> dict with string keys sorted numerically."""
    return {str(k): counter[k] for k in sorted(counter.keys())}


def main():
    cards = load_cards()
    classes = sorted({c["class"] for c in cards if c["class"]})

    by_class = defaultdict(list)
    for c in cards:
        by_class[c["class"]].append(c)

    data = {
        "generatedFrom": [str(p.relative_to(REPO_ROOT)).replace("\\", "/") for p in CARD_FILES],
        "totalCards": len(cards),
        "classes": classes,
        "cardTypes": CARD_TYPES,
        "corpus": rollup(cards),
        "byClass": {cls: rollup(by_class[cls]) for cls in classes},
        "cards": cards,
    }

    with open(OUT_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")

    # Offline-loadable mirror for the file:// panel (no fetch, no server).
    with open(OUT_JS_PATH, "w", encoding="utf-8") as f:
        f.write("// Generated by build_data.py — do not edit by hand. Mirror of data.json.\n")
        f.write("window.CARD_DATA = ")
        json.dump(data, f, ensure_ascii=False)
        f.write(";\n")

    # Printed summary is the run's deliverable — quote it, don't paraphrase it.
    print("=" * 72)
    print("CARD CORPUS AGGREGATION")
    print("=" * 72)
    print(f"Sources: {', '.join(p.name for p in CARD_FILES)}")
    print(f"Total cards: {len(cards)}")
    print(f"Classes ({len(classes)}): {', '.join(classes)}")
    print()
    print("Type split (corpus):")
    for t in CARD_TYPES:
        print(f"  {t}: {data['corpus']['typeSplit'][t]}")
    print()
    print("Per-class card counts:")
    for cls in classes:
        print(f"  {cls}: {data['byClass'][cls]['total']}")
    print()
    print(f"Wrote {OUT_PATH.relative_to(REPO_ROOT)} ({OUT_PATH.stat().st_size} bytes)")
    print(f"Wrote {OUT_JS_PATH.relative_to(REPO_ROOT)} ({OUT_JS_PATH.stat().st_size} bytes)")
    print("=" * 72)


if __name__ == "__main__":
    main()
