#!/usr/bin/env python3
"""Find card effects that will THROW at runtime because a fired trigger declares an
explicit-choice selector.

`EffectInterpreter.SelectorTargets` (src/Emberfall.Core/EffectInterpreter.cs) auto-resolves
exactly seven selectors. Anything else reaching it throws `InvalidOperationException` — by
design, per the M2 Phase 0 grammar-freeze ruling (silent fallback is silent mistargeting).

Player-initiated effects (ON_PLAY / ON_CAST) are safe: CommandProcessor makes the player
supply the target, so `ChosenTargets` is non-empty and the selector is never consulted. Every
OTHER trigger fires with an empty target list, so its selector MUST be auto-resolvable or the
card is a live crash the moment it fires.

Exit 1 if any offender is found.
"""
import json
import pathlib
import sys

# EffectInterpreter.SelectorTargets — the exhaustive auto-resolvable set.
AUTO_RESOLVABLE = {
    "SELF", "ENEMY_MINIONS", "FRIENDLY_MINIONS", "ALL_MINIONS",
    "ENEMY_HERO", "FRIENDLY_HERO",
}

# The two triggers whose targets come from the player's intent.
PLAYER_TARGETED_TRIGGERS = {"ON_PLAY", "ON_CAST"}

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"


def main() -> int:
    files = sorted(CARD_DIR.glob("*.cards.json"))
    if not files:
        print(f"no *.cards.json under {CARD_DIR}", file=sys.stderr)
        return 2

    scanned = 0
    effects = 0
    offenders = []

    for path in files:
        doc = json.loads(path.read_text(encoding="utf-8"))
        cards = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for card in cards:
            scanned += 1
            for eff in card.get("effects") or []:
                effects += 1
                trigger = eff.get("trigger")
                selector = (eff.get("target") or {}).get("selector")
                if selector is None:
                    continue  # DefaultTargets handles it
                if trigger in PLAYER_TARGETED_TRIGGERS:
                    continue
                if selector in AUTO_RESOLVABLE:
                    continue
                offenders.append((path.name, card["id"], trigger,
                                  eff.get("action"), selector))

    print(f"card files scanned : {len(files)}  ({', '.join(p.name for p in files)})")
    print(f"cards scanned      : {scanned}")
    print(f"effects scanned    : {effects}")
    print(f"offenders          : {len(offenders)}")
    if offenders:
        print()
        print(f"{'file':<16} {'card id':<34} {'trigger':<14} {'action':<10} selector")
        print("-" * 96)
        for row in offenders:
            print(f"{row[0]:<16} {row[1]:<34} {str(row[2]):<14} {str(row[3]):<10} {row[4]}")
    return 1 if offenders else 0


if __name__ == "__main__":
    sys.exit(main())
