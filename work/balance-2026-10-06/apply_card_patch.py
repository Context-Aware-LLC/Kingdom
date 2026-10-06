"""Apply row patches, and new rows, to a card-data directory IN PLACE.

    python apply_card_patch.py <card-data-dir> <patch.json>

The patch file is {"<card id>": {field: value, ...}, "_add": {"<file>": [row, ...]}}. A patched row
keeps its key order, with a newly added "text" placed ahead of "rules" the way the files write
it. New rows are appended to the named file. Rows stay one per line in the compact form the
files already use, so the diff is exactly the rows touched.
"""
import glob
import json
import os
import sys

card_dir, patch_path = sys.argv[1], sys.argv[2]
patches = json.load(open(patch_path, encoding="utf-8"))
additions = patches.pop("_add", {})
patches = {int(key): value for key, value in patches.items()}
applied = set()


def compact(row):
    return json.dumps(row, ensure_ascii=False, separators=(",", ":"))


for path in sorted(glob.glob(os.path.join(card_dir, "*.cards.json"))):
    name = os.path.basename(path)
    lines = open(path, encoding="utf-8", newline="").read().split("\n")
    changed = 0
    last_row = None
    for index, line in enumerate(lines):
        body = line.strip()
        if not body.startswith("{"):
            continue
        last_row = index
        row = json.loads(body.rstrip(","))
        patch = patches.get(row.get("id"))
        if patch is None:
            continue
        fresh_text = "text" in patch and "text" not in row
        row.update(patch)
        if fresh_text and "rules" in row:
            row["rules"] = row.pop("rules")
        indent = line[: len(line) - len(line.lstrip())]
        lines[index] = indent + compact(row) + ("," if body.endswith(",") else "")
        applied.add(row["id"])
        changed += 1

    added = additions.get(name, [])
    if added:
        indent = lines[last_row][: len(lines[last_row]) - len(lines[last_row].lstrip())]
        if not lines[last_row].rstrip().endswith(","):
            lines[last_row] = lines[last_row].rstrip() + ","
        new_lines = [indent + compact(row) + "," for row in added]
        new_lines[-1] = new_lines[-1][:-1]
        lines[last_row + 1:last_row + 1] = new_lines

    if changed or added:
        open(path, "w", encoding="utf-8", newline="").write("\n".join(lines))
    print(f"{name}: {changed} row(s) patched, {len(added)} added")

missing = sorted(set(patches) - applied)
print(f"patched ids: {sorted(applied)}  not found: {missing}")
sys.exit(1 if missing else 0)
