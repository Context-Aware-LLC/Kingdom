"""
Replicates the C# SHA256 card identity projection from AllSetsCardCountTests.
Run to get the correct pinned hash after adding/changing cards.
"""
import json
import hashlib

EMBERFALL = "unity/Emberfall/Assets/CardData/Emberfall.cards.json"
REKINDLING = "unity/Emberfall/Assets/CardData/Rekindling.cards.json"

SEP_FIELD = ""
SEP_RULE = ""


def referenced_card_id(action):
    if not action:
        return ""
    t = action.get("type", "")
    if t in ("SUMMON", "ADD_TO_HAND", "EQUIP", "TRANSFORM"):
        return action.get("cardId", "")
    return ""


def main():
    with open(EMBERFALL, encoding="utf-8") as f:
        emberfall = json.load(f)
    with open(REKINDLING, encoding="utf-8") as f:
        rekindling = json.load(f)

    all_cards = emberfall + rekindling
    print(f"Emberfall: {len(emberfall)}, Rekindling: {len(rekindling)}, Total: {len(all_cards)}")

    rows = []
    for c in all_cards:
        row_refs = SEP_RULE.join(
            referenced_card_id(r.get("action")) for r in c.get("rules", [])
        )
        collectible = "True" if c.get("collectible", False) else "False"
        tribe = c.get("tribe") or ""
        faction = c.get("factionId") or ""
        row = SEP_FIELD.join([
            c.get("id", ""),
            c.get("name", ""),
            c.get("artId", ""),
            c.get("type", ""),
            c.get("class", ""),
            c.get("rarity", ""),
            c.get("setId", ""),
            tribe,
            faction,
            collectible,
            row_refs,
        ])
        rows.append(row)

    projection = "\n".join(rows)
    h = hashlib.sha256(projection.encode("utf-8")).hexdigest().upper()
    print(f"Hash: {h}")


if __name__ == "__main__":
    main()
