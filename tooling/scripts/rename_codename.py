#!/usr/bin/env python3
"""Rename the codename Emberfall -> Kingdom across tracked files and tracked paths.

Dry run by default: prints what would change and what is held back, and touches nothing.
`--apply` performs the content rewrite and the `git mv`s. It commits nothing.

THE RULE, and why it is this shape. The codename as STRUCTURE is PascalCase - namespaces,
assembly and project names, folders, menu paths, class names. The codename as an EXTERNAL
CONTRACT is lowercase or UPPERCASE - the `emberfall://auth` redirect, the `EMBERFALL_*` env
vars, `emberfall.fly.dev`, the `emberfall_data` DB and `emberfall_app` role, the PlayerPrefs
keys that hold a device's identity and session, the `emberfallReplay` corpus key, and setId
`EMBERFALL`. Each of those is registered or stored somewhere this repo cannot reach (Supabase,
Google, Fly, a keystore, a player's device, a replay corpus on disk), so renaming the string here
breaks the pairing silently. So:

  * `Emberfall`      -> `Kingdom`, everywhere, minus PASCAL_HOLDS below.
  * `com.emberfall.` -> `com.kingdom.` (the local UPM package ids; both ends live in this repo).
  * every other lowercase or UPPERCASE occurrence is HELD and listed in the report.

Each held contract is its own change, made together with the system on the other end.

Works on bytes, so encoding, BOM and line endings pass through untouched.

Usage:
    python tooling/scripts/rename_codename.py            # dry run
    python tooling/scripts/rename_codename.py --apply    # needs the Editor closed, clean tree
"""
import argparse
import collections
import os
import re
import subprocess
import sys

OLD, NEW = b"Emberfall", b"Kingdom"
PKG_OLD, PKG_NEW = b"com.emberfall.", b"com.kingdom."

# PascalCase occurrences that are NOT the codename-as-structure.
PASCAL_HOLDS = [
    # The set's card data file and its Addressables address. This is the setId in filename
    # form; it moves with the setId (the Pendingham rename, #397), not with the codename.
    rb"Emberfall\.cards",
    # F:\EmberfallData - a directory on disk outside the repo.
    rb"EmberfallData",
]
PASCAL_HOLD_RE = re.compile(b"|".join(PASCAL_HOLDS))

# Records of what was true when written; rewriting them falsifies them.
SKIP_CONTENT_PREFIXES = ("work/", "evidence/")
SELF = "tooling/scripts/rename_codename.py"

ANY_RE = re.compile(rb"emberfall", re.IGNORECASE)
TOKEN_RE = re.compile(rb"[A-Za-z0-9_.:/@-]*(?:emberfall|EMBERFALL)[A-Za-z0-9_.:/-]*")
EXTERNAL_RE = re.compile(rb"(?:[A-Za-z]:[\\/]|github\.com[/:])[^\s\"'`)>]*Emberfall[^\s\"'`)>]*")
MAX_BYTES = 64 * 1024 * 1024


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True).stdout


def tracked():
    return [p for p in git("ls-files", "-z").decode("utf-8").split("\0") if p]


def rewrite(data):
    """Returns (new bytes, structural replacements, package-id replacements, pascal holds)."""
    held = []

    def mask(m):
        held.append(m.group(0))
        return b"\x00HOLD%d\x00" % (len(held) - 1)

    out = PASCAL_HOLD_RE.sub(mask, data)
    out, n = re.subn(re.escape(OLD), NEW, out)
    out, k = re.subn(re.escape(PKG_OLD), PKG_NEW, out)
    out = re.sub(rb"\x00HOLD(\d+)\x00", lambda m: held[int(m.group(1))], out)
    return out, n, k, len(held)


def new_path(path):
    parts = path.split("/")
    out = []
    for part in parts:
        if PASCAL_HOLD_RE.search(part.encode("utf-8")):
            out.append(part)
        else:
            out.append(part.replace("Emberfall", "Kingdom").replace("com.emberfall.", "com.kingdom."))
    return "/".join(out)


def top(path):
    return path.split("/")[0] if "/" in path else "(root)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--allow-dirty", action="store_true")
    args = ap.parse_args()

    root = git("rev-parse", "--show-toplevel").decode().strip()
    os.chdir(root)
    paths = tracked()

    edits = {}                                   # path -> new bytes
    by_top = collections.defaultdict(lambda: [0, 0])
    held_tokens = collections.Counter()
    pascal_held = 0
    pkg_total = 0
    binaries, external, skipped_records = [], [], [0, 0]
    preexisting_kingdom_ns = 0

    for path in paths:
        if path == SELF or not os.path.isfile(path) or os.path.getsize(path) > MAX_BYTES:
            continue
        with open(path, "rb") as f:
            data = f.read()
        preexisting_kingdom_ns += len(re.findall(rb"\bKingdom\.[A-Z]", data)) if b"\x00" not in data[:8192] else 0
        if not ANY_RE.search(data):
            continue
        if b"\x00" in data[:8192]:
            binaries.append((path, len(ANY_RE.findall(data))))
            continue
        if path.startswith(SKIP_CONTENT_PREFIXES):
            skipped_records[0] += 1
            skipped_records[1] += len(ANY_RE.findall(data))
            continue
        out, n, k, h = rewrite(data)
        pascal_held += h
        pkg_total += k
        for m in EXTERNAL_RE.finditer(data):
            if not PASCAL_HOLD_RE.search(m.group(0)):
                external.append((path, m.group(0).decode("utf-8", "replace")))
        for m in TOKEN_RE.finditer(out):
            held_tokens[m.group(0).decode("utf-8", "replace")] += 1
        if out != data:
            edits[path] = out
            by_top[top(path)][0] += 1
            by_top[top(path)][1] += n + k

    moves = [(p, new_path(p)) for p in paths if new_path(p) != p]
    existing = set(paths)
    collisions = [(a, b) for a, b in moves if b in existing or os.path.exists(b)]
    move_top = collections.Counter(top(a) for a, _ in moves)

    print("== content: files / replacements by top-level dir ==")
    for d, (files, reps) in sorted(by_top.items(), key=lambda kv: -kv[1][1]):
        print(f"{files:6d} files {reps:7d} replacements  {d}")
    print(f"{len(edits):6d} files {sum(v[1] for v in by_top.values()):7d} replacements  TOTAL"
          f"  (of which com.emberfall. package ids: {pkg_total})")
    print("\n== tracked paths to rename, by top-level dir ==")
    for d, c in move_top.most_common():
        print(f"{c:6d}  {d}")
    print(f"{len(moves):6d}  TOTAL")
    print(f"\n== path collisions: {len(collisions)} ==")
    for a, b in collisions[:20]:
        print(f"  {a} -> {b}")
    print(f"\n== pre-existing `Kingdom.<Upper>` occurrences (namespace clash check): {preexisting_kingdom_ns} ==")
    print(f"\n== HELD: PascalCase holds (Emberfall.cards, EmberfallData): {pascal_held} ==")
    print(f"\n== HELD: lowercase / UPPERCASE tokens left untouched: "
          f"{sum(held_tokens.values())} occurrences, {len(held_tokens)} distinct ==")
    for tok, c in held_tokens.most_common():
        print(f"{c:6d}  {tok[:100]}")
    print(f"\n== skipped as records (work/, evidence/): {skipped_records[0]} files, {skipped_records[1]} hits ==")
    print(f"\n== tracked BINARY files containing the word (not rewritten): {len(binaries)} ==")
    for p, c in binaries:
        print(f"{c:6d}  {p}")
    print(f"\n== PascalCase inside a drive path or GitHub URL (would be rewritten - check): {len(external)} ==")
    for p, t in external:
        print(f"  {p}: {t[:120]}")

    if not args.apply:
        print("\nDRY RUN - nothing changed.")
        return 0

    if os.path.exists("unity/Temp/UnityLockfile"):
        sys.exit("REFUSED: unity/Temp/UnityLockfile exists - close the Editor first.")
    if collisions:
        sys.exit("REFUSED: path collisions above.")
    dirty = git("status", "--porcelain", "--untracked-files=no").decode().strip()
    if dirty and not args.allow_dirty:
        sys.exit("REFUSED: tracked files are modified or staged. Land them first, or --allow-dirty.")

    for path, out in edits.items():
        with open(path, "wb") as f:
            f.write(out)

    # Rename one path component at a time, deepest first, so every parent is still at its
    # original name when its child moves. `git mv` on a directory carries untracked files too.
    components = set()
    for a, _ in moves:
        parts = a.split("/")
        for i, part in enumerate(parts):
            if new_path(part) != part:
                components.add("/".join(parts[: i + 1]))
    for src in sorted(components, key=lambda p: -p.count("/")):
        parent, _, name = src.rpartition("/")
        dst = (parent + "/" if parent else "") + new_path(name)
        subprocess.run(["git", "mv", src, dst], check=True)

    print(f"\nAPPLIED: {len(edits)} files rewritten, {len(components)} path components moved. Nothing committed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
