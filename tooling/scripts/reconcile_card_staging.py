"""Merge staged web edits into the card corpus -- the only thing allowed to write a set file.

    python tooling/scripts/reconcile_card_staging.py            # dry run, reports everything
    python tooling/scripts/reconcile_card_staging.py --apply     # write the clean ones
    python tooling/scripts/reconcile_card_staging.py --apply --force-conflicts

WHAT THIS SOLVES
The Unity authoring window writes each set file WHOLE, from what is in memory. Two editors
therefore cannot share the corpus: the second to save does not merge badly, it reverts
everything it did not know about. The web tool avoids that by never writing a set file at all --
it stages edits, each carrying the row's BASE (what the row looked like when the author opened
it). This script is where a staged edit finally meets the corpus, and it applies one only if the
row has not moved underneath it.

THREE OUTCOMES PER EDIT, and none of them is silent
  CLEAN     the row on disk still matches the base -> the change applies.
  CONFLICT  the row changed locally since the edit was staged -> reported with both sides,
            applied only under --force-conflicts, which is a deliberate act of choosing the
            web author's version over someone's local work.
  ORPHAN    the row no longer exists -> reported, never applied.

WHAT IT DELIBERATELY WILL NOT DO
  - Mint an id, handle or artId. Those come from the Unity window, which is the single minting
    authority; NextIdInBand explicitly is not a reservation, so a second minter would hand out
    numbers already in use. The web tool cannot create cards for exactly this reason.
  - Touch `rules` or `soundEffects`. The web tool does not edit them, so this does not merge
    them; they pass through untouched inside the row.

AFTER A SUCCESSFUL APPLY the derived artifacts are stale. This script says so and, with
--rebake, runs `tooling/scripts/rebake_card_data.py --write` for you. Skipping that leaves deck
fixtures disagreeing with the corpus, which surfaces later and somewhere else.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tooling" / "cardweb"))

import canon  # noqa: E402
from corpus import EDITABLE_FIELDS, IDENTITY_FIELDS, row_base, set_files  # noqa: E402

STAGING = REPO / "tooling" / "cardweb" / "staging" / "pending.json"

CLEAN, CONFLICT, ORPHAN = "CLEAN", "CONFLICT", "ORPHAN"


def load_sets(files: dict) -> dict[str, list[dict]]:
    out = {}
    for set_id, path in files.items():
        if not path.exists():
            raise SystemExit(f"missing set file: {path}")
        out[set_id] = json.loads(path.read_text(encoding="utf-8"))
    return out


def classify(edit: dict, index: dict[int, tuple[str, dict]]) -> tuple[str, str]:
    """Decide what happens to one staged edit, and why."""
    cid = edit.get("cardId")
    if cid not in index:
        return ORPHAN, f"card {cid} is no longer in the corpus"

    _set_id, row = index[cid]
    live = row_base(row)
    if live != edit.get("base"):
        return CONFLICT, "the row changed on disk after this edit was staged"

    bad = set(edit.get("changed", {})) - EDITABLE_FIELDS
    if bad:
        # sanitize_edit should have caught this at staging time; a staging file edited by hand
        # is the way it gets here, and an identity key must never be applied whatever the route.
        kind = "identity field" if bad & IDENTITY_FIELDS else "non-editable field"
        return CONFLICT, f"edit carries {kind}(s) {sorted(bad)}"

    return CLEAN, ""


def diff_lines(row: dict, changed: dict) -> list[str]:
    out = []
    for k in sorted(changed):
        before = row.get(k)
        out.append(f"      {k}: {json.dumps(before, ensure_ascii=False)}"
                   f"  ->  {json.dumps(changed[k], ensure_ascii=False)}")
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="write the set files")
    ap.add_argument("--force-conflicts", action="store_true",
                    help="also apply edits whose row changed on disk (overwrites local work)")
    ap.add_argument("--rebake", action="store_true",
                    help="run rebake_card_data.py --write after a successful apply")
    ap.add_argument("--staging", type=pathlib.Path, default=STAGING)
    ap.add_argument("--card-dir", type=pathlib.Path, default=None,
                    help="operate on a copy of the corpus instead of the live one (for tests)")
    args = ap.parse_args()

    if not args.staging.exists():
        print(f"nothing staged ({args.staging} does not exist).")
        return 0

    doc = json.loads(args.staging.read_text(encoding="utf-8"))
    edits = doc.get("edits", [])
    if not edits:
        print("nothing staged.")
        return 0

    # Confirm the serializer still matches the C# writer BEFORE writing anything with it.
    # A drifted serializer would rewrite all 293 rows, and the damage would look like a
    # successful merge.
    card_dir = args.card_dir or (REPO / "unity" / "Emberfall" / "Assets" / "CardData")
    files = set_files(card_dir)

    print("verifying canonical serializer against the corpus…")
    if canon.selftest(card_dir) != 0:
        print("\nREFUSING TO WRITE: canon.py no longer reproduces the on-disk format.",
              file=sys.stderr)
        return 2
    print()

    sets = load_sets(files)
    index: dict[int, tuple[str, dict]] = {}
    for set_id, rows in sets.items():
        for row in rows:
            index[row["id"]] = (set_id, row)

    buckets: dict[str, list] = {CLEAN: [], CONFLICT: [], ORPHAN: []}
    for e in edits:
        verdict, why = classify(e, index)
        buckets[verdict].append((e, why))

    for verdict in (CLEAN, CONFLICT, ORPHAN):
        items = buckets[verdict]
        if not items:
            continue
        print(f"{verdict}  ({len(items)})")
        for e, why in items:
            cid = e.get("cardId")
            print(f"  {e.get('name','?')}  [{e.get('setId','?')}]" + (f"  — {why}" if why else ""))
            if cid in index:
                print("\n".join(diff_lines(index[cid][1], e.get("changed", {}))))
        print()

    to_apply = list(buckets[CLEAN])
    if args.force_conflicts:
        to_apply += buckets[CONFLICT]

    if not args.apply:
        print(f"dry run — {len(to_apply)} edit(s) would apply, "
              f"{len(buckets[CONFLICT])} conflict, {len(buckets[ORPHAN])} orphaned.")
        print("re-run with --apply to write.")
        return 1 if buckets[CONFLICT] or buckets[ORPHAN] else 0

    if not to_apply:
        print("nothing to apply.")
        return 1

    # ── apply ────────────────────────────────────────────────────────────────
    moved = []
    for e, _ in to_apply:
        cid = e["cardId"]
        set_id, row = index[cid]
        changed = e["changed"]
        new_set = changed.get("setId", row["setId"])

        for k, v in changed.items():
            if v is None or v == "":
                # An emptied optional field is a REMOVAL, matching the C# writer's omit-when-empty
                # behaviour. Writing "" instead would put a field on the row that Unity then
                # strips on its next save -- a diff that ping-pongs forever.
                row.pop(k, None)
            else:
                row[k] = v

        if new_set != set_id:
            # A row whose set changed MOVES file: PathForSet files a row by what it says it is,
            # and the <setId> also decides which Addressables bundle its art ships in.
            sets[set_id] = [r for r in sets[set_id] if r["id"] != cid]
            sets[new_set].append(row)
            moved.append((row.get("name"), set_id, new_set))

    written = []
    for set_id, rows in sets.items():
        path = files[set_id]
        before = path.read_text(encoding="utf-8")
        after = canon.serialize_file(rows)
        if after != before:
            path.write_text(after, encoding="utf-8", newline="")
            written.append((path.name, len(rows)))

    for name, src, dst in moved:
        print(f"moved  {name}: {src} -> {dst}")
    for name, n in written:
        print(f"wrote  {name}  ({n} rows)")
    if not written:
        print("no file changed.")

    # Clear the applied edits; anything unresolved stays staged.
    remaining = [e for e, _ in buckets[CONFLICT] + buckets[ORPHAN]] if not args.force_conflicts \
        else [e for e, _ in buckets[ORPHAN]]
    doc["edits"] = remaining
    args.staging.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n",
                            encoding="utf-8")
    print(f"staging  {len(remaining)} edit(s) left unresolved")

    if written:
        print("\nDerived artifacts are now stale.")
        if args.rebake:
            print("running rebake_card_data.py --write …\n")
            r = subprocess.run(
                [sys.executable, str(REPO / "tooling" / "scripts" / "rebake_card_data.py"),
                 "--write"], cwd=str(REPO))
            return r.returncode
        print("  run: python tooling/scripts/rebake_card_data.py --write")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
