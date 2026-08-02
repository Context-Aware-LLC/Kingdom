#!/usr/bin/env python3
"""Find card effects that will THROW at runtime because a fired trigger declares an
explicit-choice selector.

`EffectInterpreter.SelectorTargets` (src/Emberfall.Core/EffectInterpreter.cs) auto-resolves
exactly six selectors — the six `case` labels there, mirrored in AUTO_RESOLVABLE below.
Anything else reaching it throws `InvalidOperationException` — by design, per the M2 Phase 0
grammar-freeze ruling (silent fallback is silent mistargeting).

Player-initiated effects (ON_PLAY / ON_CAST) are safe: CommandProcessor makes the player
supply the target, so `ChosenTargets` is non-empty and the selector is never consulted. Every
OTHER trigger fires with an empty target list, so its selector MUST be auto-resolvable or the
card is a live crash the moment it fires.

Exit 1 if any offender is found.
"""
import json
import pathlib
import re
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
INTERPRETER = ROOT / "src" / "Emberfall.Core" / "EffectInterpreter.cs"

# `case "X":` inside SelectorTargets — the labels this scan mirrors.
CASE_LABEL = re.compile(r'^\s*case\s+"([A-Z_]+)"\s*:', re.MULTILINE)


def interpreter_selectors() -> "set[str] | None":
    """The `case` labels of EffectInterpreter.SelectorTargets, read from the source.

    AUTO_RESOLVABLE is a hand-copy of a switch in another language in another file, which is
    exactly the kind of duplicate that rots silently: a selector added there and not here makes
    this scan cry wolf, and one removed there makes it miss a live crash. Read the real thing
    and diff, so drift fails the scan instead of quietly degrading it.
    """
    if not INTERPRETER.exists():
        return None
    src = INTERPRETER.read_text(encoding="utf-8")
    start = src.find("SelectorTargets(EffectDef eff, CardInstance source)")
    if start < 0:
        return None
    # The method ends at its `default:` arm — the throw that this whole scan exists to prevent.
    end = src.find("default:", start)
    if end < 0:
        return None
    return set(CASE_LABEL.findall(src[start:end]))


def main() -> int:
    actual = interpreter_selectors()
    if actual is None:
        print(f"could not read SelectorTargets from {INTERPRETER} — "
              "the scan cannot prove it is in sync with the engine", file=sys.stderr)
        return 2
    if actual != AUTO_RESOLVABLE:
        print("AUTO_RESOLVABLE is out of sync with EffectInterpreter.SelectorTargets:",
              file=sys.stderr)
        for s in sorted(actual - AUTO_RESOLVABLE):
            print(f"  in the interpreter, missing here : {s}", file=sys.stderr)
        for s in sorted(AUTO_RESOLVABLE - actual):
            print(f"  listed here, not in the interpreter: {s}", file=sys.stderr)
        return 2

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
