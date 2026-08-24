#!/usr/bin/env python3
"""Assign the permanent numeric card id.

Sam ruled the identity model on 2026-08-15 and the bands on 2026-08-23: a card carries a
NUMERIC id (permanent, meaningless, the only thing anything resolves against), a HANDLE
(mnemonic, freely editable, referenced by nothing) and a NAME (player-facing).

    HERO_POWER   1 -   50
    WEAPON      51 -  150
    SPELL      151 - 1000
    MINION    1001 -

Bands, not one run, so a later set appends inside its own type without ever colliding with
the next one. Numbering starts at 1 and never at 0: `default(int)` is 0, so a card id of 0
is indistinguishable from an unset field at every boundary it crosses.

ORDER WITHIN A BAND is the order the rows appear on disk today -- Emberfall.cards.json then
Rekindling.cards.json, each row in file order. Deterministic and reproducible, which is the
only property that matters, because the number is meaningless by design.

RUN IT ONCE. Every number this assigns is permanent from the moment it ships: a saved deck,
a replay and a telemetry row all carry it. Re-running against rows that already have an `id`
REFUSES rather than renumbering -- see `--allow-new`, which extends the bands for rows added
later and leaves every existing assignment untouched.
"""
from __future__ import annotations

import argparse
import io
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CARD_DATA = REPO / "unity" / "Emberfall" / "Assets" / "CardData"
# Core first: the system cards (hero powers, the coin) belong to no expansion because
# they never rotate. Order only decides who gets the next free number in a band.
FILES = ["Core.cards.json", "Emberfall.cards.json", "Rekindling.cards.json"]

# (first, last) inclusive. `None` for a band that runs to infinity.
BANDS = {
    "HERO_POWER": (1, 50),
    "WEAPON": (51, 150),
    "SPELL": (151, 1000),
    "MINION": (1001, None),
}


def load(path: Path):
    with io.open(path, encoding="utf-8") as fh:
        return json.load(fh)


def assign(rows_by_file, allow_new: bool):
    """Return {handle: number}, and the per-band occupancy, or raise on a band overflow."""
    taken = {band: [] for band in BANDS}
    existing = {}

    for _, rows in rows_by_file:
        for row in rows:
            if isinstance(row.get("id"), int):
                existing[row["handle"]] = row["id"]

    if existing and not allow_new:
        raise SystemExit(
            f"{len(existing)} rows already carry a numeric id. This script does not renumber "
            "-- a number is permanent once it ships. Pass --allow-new to number only the rows "
            "that lack one."
        )

    numbers = dict(existing)
    for band, number in existing.items():
        for name, (first, last) in BANDS.items():
            if number >= first and (last is None or number <= last):
                taken[name].append(number)

    for _, rows in rows_by_file:
        for row in rows:
            handle = row["handle"] if "handle" in row else row["id"]
            if handle in numbers:
                continue
            kind = row["type"]
            if kind not in BANDS:
                raise SystemExit(f"'{handle}' has type {kind}, which has no band.")
            first, last = BANDS[kind]
            nxt = max(taken[kind]) + 1 if taken[kind] else first
            if last is not None and nxt > last:
                raise SystemExit(
                    f"The {kind} band ({first}-{last}) is full at '{handle}'. Widen the band "
                    "before assigning -- do NOT spill into the next one."
                )
            numbers[handle] = nxt
            taken[kind].append(nxt)

    return numbers, taken


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write", action="store_true",
                    help="rewrite the card data: 'id' becomes the number, the old string id "
                         "moves to 'handle'. Without this, prints the assignment and exits.")
    ap.add_argument("--allow-new", action="store_true",
                    help="number only rows that lack a number, extending each band.")
    args = ap.parse_args()

    rows_by_file = [(name, load(CARD_DATA / name)) for name in FILES]
    numbers, taken = assign(rows_by_file, args.allow_new)

    print(f"{sum(len(r) for _, r in rows_by_file)} rows, {len(numbers)} numbered\n")
    for kind, (first, last) in BANDS.items():
        got = sorted(taken[kind])
        ceiling = last if last is not None else "-"
        span = f"{got[0]}-{got[-1]}" if got else "(none)"
        room = "unbounded" if last is None else f"{last - got[-1]} free" if got else "empty"
        print(f"  {kind:<11} band {first:>4} - {str(ceiling):<4}   used {span:<12} "
              f"{len(got):>3} cards   {room}")

    if not args.write:
        print("\nDry run. Nothing written. Pass --write to apply.")
        return 0

    # A rule naming another card is a RESOLUTION site, so it carries the number too. Leaving
    # these as handles is precisely the "mostly cosmetic handle that exactly one thing keys on"
    # the identity ruling names as the failure mode -- it would make every handle load-bearing
    # again through a field nobody thinks of as a key.
    rewritten = 0

    def renumber_refs(node):
        nonlocal rewritten
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "cardId" and isinstance(value, str):
                    if value not in numbers:
                        raise SystemExit(f"rule references unknown card '{value}'.")
                    node[key] = numbers[value]
                    rewritten += 1
                else:
                    renumber_refs(value)
        elif isinstance(node, list):
            for value in node:
                renumber_refs(value)

    for name, rows in rows_by_file:
        out = []
        for row in rows:
            renumber_refs(row.get("rules", []))
            handle = row["handle"] if "handle" in row else row["id"]
            # Rebuild in order so 'id' and 'handle' lead the row, as they read on the page.
            fresh = {"id": numbers[handle], "handle": handle}
            for key, value in row.items():
                if key in ("id", "handle"):
                    continue
                fresh[key] = value
            out.append(fresh)
        path = CARD_DATA / name
        with io.open(path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(out, fh, indent=2, ensure_ascii=False)
            fh.write("\n")
        print(f"\nwrote {path.relative_to(REPO)}  ({len(out)} rows)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
