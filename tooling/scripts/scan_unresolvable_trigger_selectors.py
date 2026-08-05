#!/usr/bin/env python3
"""Find card effects that will THROW at runtime because a fired trigger declares an
explicit-choice selector.

The canonical selector switch lives in `Targeting.cs`. This scan reads its cases and the
default player-choice selector set from `TargetSpec`, rather than duplicating either list.
Anything outside those structural seams fails the scan — silent fallback is silent
mistargeting.

Player-initiated effects (ON_PLAY / ON_CAST) are safe: CommandProcessor makes the player
supply the target, so `ChosenTargets` is non-empty and the selector is never consulted. Every
OTHER trigger fires with an empty target list, so its selector MUST be auto-resolvable or the
card is a live crash the moment it fires.

A row carrying `requiresEngineeringPrimitive: true` is HELD, not an offender: since EF-032a
that flag excludes the row from `ICardDatabase.AllCollectible()`, so it reaches no deck, no
pack and no pool, and therefore can never fire. Held rows are printed anyway — a reservation
the set is carrying is worth seeing, and a silent hold is how content goes missing.

requiresEngineeringPrimitive is a TEMPORARY quarantine (Sam, 2026-08-03), not a standing
pattern — see the retirement trigger on CardDefinition.PoolEligible. Once card authoring is
backend-first (no new card ships against a primitive that doesn't exist yet) and this backlog
clears, the HELD branch below should read zero rows forever; that is the signal to retire it.

Exit 1 if any offender is found.
"""
import json
import pathlib
import re
import sys

# The two triggers whose targets come from the player's intent.
PLAYER_TARGETED_TRIGGERS = {"ON_PLAY", "ON_CAST"}

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
TARGETING = ROOT / "src" / "Emberfall.Core" / "Targeting.cs"
TARGET_SPEC = ROOT / "src" / "Emberfall.Core.Cards" / "CardDefinition.cs"

# `case "X":` inside Targeting's switch over TargetSpec.Selector.
CASE_LABEL = re.compile(r'^\s*case\s+"([A-Z_]+)"\s*:', re.MULTILINE)
SELECTOR_SWITCH = re.compile(r'switch\s*\(\s*spec\.Selector\s*\)')


def block_after(src: str, start: int) -> "str | None":
    """Return the balanced brace block beginning at or after start, or None when malformed."""
    opening = src.find("{", start)
    if opening < 0:
        return None
    depth = 0
    for index in range(opening, len(src)):
        if src[index] == "{":
            depth += 1
        elif src[index] == "}":
            depth -= 1
            if depth == 0:
                return src[opening + 1:index]
    return None


def targeting_selectors() -> "set[str] | None":
    """Read the selector labels from the shared runtime target-policy switch.

    The scanner anchors on the selector expression, not a method name, because the structural
    switch is the runtime seam shared by player validation and effect resolution.
    """
    if not TARGETING.exists():
        return None
    src = TARGETING.read_text(encoding="utf-8")
    match = SELECTOR_SWITCH.search(src)
    if match is None:
        return None
    body = block_after(src, match.end())
    if body is None:
        return None
    selectors = set(CASE_LABEL.findall(body))
    return selectors if selectors else None


def default_player_selectors() -> "set[str] | None":
    """Read TargetSpec's default explicit-choice policy from its declared set."""
    if not TARGET_SPEC.exists():
        return None
    src = TARGET_SPEC.read_text(encoding="utf-8")
    start = src.find("DefaultPlayerSelectors")
    if start < 0:
        return None
    body = block_after(src, start)
    if body is None:
        return None
    selectors = set(re.findall(r'"([A-Z_]+)"', body))
    return selectors if selectors else None


def main() -> int:
    selectors = targeting_selectors()
    player_defaults = default_player_selectors()
    if selectors is None or player_defaults is None:
        print("could not read the Targeting selector switch or TargetSpec default choice set — "
              "the scan cannot prove it is in sync with the engine", file=sys.stderr)
        return 2
    if not player_defaults.issubset(selectors):
        print("TargetSpec default player-choice selectors are not all handled by Targeting:",
              file=sys.stderr)
        for selector in sorted(player_defaults - selectors):
            print(f"  default choice missing in Targeting: {selector}", file=sys.stderr)
        return 2

    files = sorted(CARD_DIR.glob("*.cards.json"))
    if not files:
        print(f"no *.cards.json under {CARD_DIR}", file=sys.stderr)
        return 2

    scanned = 0
    rules = 0
    offenders = []
    held = []

    for path in files:
        doc = json.loads(path.read_text(encoding="utf-8"))
        cards = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for card in cards:
            scanned += 1
            for rule in card.get("rules") or []:
                rules += 1
                trigger = (rule.get("activation") or {}).get("trigger")
                action = rule.get("action") or {}
                target = action.get("target") or {}
                selector = target.get("selector")
                if selector is None:
                    continue  # DefaultTargets handles it
                if selector not in selectors:
                    print(f"{card['id']} uses selector '{selector}' that Targeting does not handle",
                          file=sys.stderr)
                    return 2
                if trigger in PLAYER_TARGETED_TRIGGERS:
                    continue
                chosen_by = target.get("chosenBy")
                if chosen_by == "RANDOM":
                    continue
                if chosen_by not in (None, "PLAYER"):
                    print(f"{card['id']} uses unknown target choice '{chosen_by}'", file=sys.stderr)
                    return 2
                if chosen_by is None and selector not in player_defaults:
                    continue
                row = (path.name, card["id"], trigger, action.get("type"), selector)
                # Held out of every pool by the gate — it cannot be drawn, so it cannot fire.
                if card.get("requiresEngineeringPrimitive") is True:
                    held.append(row)
                else:
                    offenders.append(row)

    def table(rows):
        print(f"{'file':<16} {'card id':<34} {'trigger':<14} {'action':<10} selector")
        print("-" * 96)
        for row in rows:
            print(f"{row[0]:<16} {row[1]:<34} {str(row[2]):<14} {str(row[3]):<10} {row[4]}")

    print(f"card files scanned : {len(files)}  ({', '.join(p.name for p in files)})")
    print(f"cards scanned      : {scanned}")
    print(f"rules scanned      : {rules}")
    print(f"offenders          : {len(offenders)}")
    print(f"held (quarantined) : {len(held)}")
    if held:
        print()
        print("HELD — requiresEngineeringPrimitive is set, so AllCollectible() excludes these rows")
        print("and they reach no pool. TEMPORARY quarantine (retire this branch once it's empty):")
        table(held)
    if offenders:
        print()
        print("OFFENDERS — these WILL throw when the trigger fires:")
        table(offenders)
    return 1 if offenders else 0


if __name__ == "__main__":
    sys.exit(main())
