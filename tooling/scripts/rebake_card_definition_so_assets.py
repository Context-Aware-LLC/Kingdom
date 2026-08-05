"""Synchronize CardDefinitionSO YAML payloads with the canonical CardData rows.

Usage:
  python tooling/scripts/rebake_card_definition_so_assets.py --check
  python tooling/scripts/rebake_card_definition_so_assets.py --write

The source JSON rows are authoritative. This deliberately changes only each asset's final
`_json` scalar; Unity YAML headers, object identity, and `.meta` files remain untouched.
"""

import argparse
import json
import pathlib
import re
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DATA = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ASSET_ROOT = ROOT / "unity" / "Emberfall" / "Assets" / "Cards"
NAME = re.compile(r"^  m_Name: (?P<id>.+)$", re.MULTILINE)
JSON_START = re.compile(r"^  _json: ", re.MULTILINE)


def canonical_rows():
    rows = {}
    for path in sorted(CARD_DATA.glob("*.cards.json")):
        for row in json.loads(path.read_text(encoding="utf-8")):
            card_id = row["id"]
            if card_id in rows:
                raise ValueError(f"duplicate canonical card id '{card_id}'")
            rows[card_id] = row
    return rows


def normalized_json(row):
    # Compact strict JSON makes the YAML payload one stable scalar while preserving the canonical
    # row's object and array ordering. CardJson remains the authoritative parser in the regression.
    return json.dumps(row, ensure_ascii=False, separators=(",", ":"))


def baked_asset_text(path, row):
    original = path.read_bytes().decode("utf-8")
    name = NAME.search(original)
    if name is None:
        raise ValueError(f"{path}: missing m_Name")
    asset_id = name.group("id").strip()
    if asset_id != row["id"]:
        raise ValueError(f"{path}: m_Name '{asset_id}' does not match '{row['id']}'")

    json_start = JSON_START.search(original)
    if json_start is None:
        raise ValueError(f"{path}: missing _json scalar")

    # CardDefinitionSO has no serialized fields after _json. Fail rather than guessing if that
    # contract changes, so this bulk rebake cannot discard future Unity YAML fields.
    scalar = original[json_start.start():]
    if not scalar.rstrip().endswith(("'", '"')):
        raise ValueError(f"{path}: malformed terminal _json scalar")

    newline = "\r\n" if "\r\n" in original else "\n"
    yaml_string = normalized_json(row).replace("'", "''")
    return original[:json_start.start()] + f"  _json: '{yaml_string}'{newline}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="fail if any asset is not rebaked")
    mode.add_argument("--write", action="store_true", help="rewrite mismatched _json scalars")
    args = parser.parse_args()

    rows = canonical_rows()
    assets = sorted(ASSET_ROOT.rglob("*.asset"))
    changes = []
    for path in assets:
        original = path.read_bytes().decode("utf-8")
        name = NAME.search(original)
        if name is None:
            raise ValueError(f"{path}: missing m_Name")
        card_id = name.group("id").strip()
        if card_id not in rows:
            raise ValueError(f"{path}: no canonical CardData row for '{card_id}'")
        expected = baked_asset_text(path, rows[card_id])
        if original != expected:
            changes.append((path, expected))

    print(f"CARD_DEFINITION_SO_ASSETS={len(assets)}")
    print(f"CARD_DEFINITION_SO_MISMATCHES={len(changes)}")
    for path, _ in changes:
        print(path.relative_to(ROOT).as_posix())

    if args.check and changes:
        return 1
    if args.write:
        for path, expected in changes:
            path.write_bytes(expected.encode("utf-8"))
        print("CARD_DEFINITION_SO_REBAKE=COMPLETE")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ValueError as error:
        print(f"CARD_DEFINITION_SO_REBAKE_ERROR={error}", file=sys.stderr)
        sys.exit(2)
