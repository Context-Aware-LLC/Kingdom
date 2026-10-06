"""Copy the repo's card data to a new directory and apply row patches from a JSON file.

    python make_card_variant.py <out-dir> <patch.json>

The patch file is {"<card id>": {field: value, ...}}. Rows stay one per line in the same compact
form the repo files use, so the copy differs from the source only on the patched rows. The repo's
own files are never written.
"""
import glob
import json
import os
import sys

SOURCE = r"C:/Users/User/Documents/tcg-hearthlike/unity/Assets/CardData"
out_dir, patch_path = sys.argv[1], sys.argv[2]
patches = {int(k): v for k, v in json.load(open(patch_path, encoding="utf-8")).items()}
os.makedirs(out_dir, exist_ok=True)
applied = set()

for path in sorted(glob.glob(os.path.join(SOURCE, "*.cards.json"))):
    lines = open(path, encoding="utf-8", newline="").read().split("\n")
    changed = 0
    for index, line in enumerate(lines):
        body = line.strip()
        if not body.startswith("{"):
            continue
        trailing = "," if body.endswith(",") else ""
        row = json.loads(body.rstrip(","))
        patch = patches.get(row.get("id"))
        if patch is None:
            continue
        row.update(patch)
        indent = line[: len(line) - len(line.lstrip())]
        lines[index] = indent + json.dumps(row, ensure_ascii=False, separators=(",", ":")) + trailing
        applied.add(row["id"])
        changed += 1
    target = os.path.join(out_dir, os.path.basename(path))
    open(target, "w", encoding="utf-8", newline="").write("\n".join(lines))
    print(f"{os.path.basename(path)}: {changed} row(s) patched")

missing = sorted(set(patches) - applied)
print(f"patched ids: {sorted(applied)}  not found: {missing}")
sys.exit(1 if missing else 0)
