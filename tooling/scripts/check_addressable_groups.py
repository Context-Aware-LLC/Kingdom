#!/usr/bin/env python3
"""Refuse a commit that adds an art SET FOLDER with no Addressables group behind it.

WHY THIS IS A CHECK AND NOT A FIXER
-----------------------------------
The obvious version of this runs `Emberfall/Addressables/Setup Groups + Migrate Card Data` for you.
It takes about a second in an open Editor, so the cost is not the objection. Two things are:

1. **A pre-commit hook must not write to the working tree in this repo.** `.githooks/pre-commit`
   carries the full account: its first version rebaked a stale asset and staged it, and because git
   points GIT_INDEX_FILE at a TEMPORARY index during a partial commit, a hook's `git add` lands in
   whoever's commit is running rather than the commit that caused the drift. It put a card asset
   into an unrelated deckbuilder merge, which was then pushed. `git commit --only <paths>` does not
   protect against it. The setup run rewrites AddressableAssetSettings.asset and every group asset
   it touches — shared files — so a fixer here would reproduce that bug with bigger blast radius.
2. **It needs a Unity Editor.** In an open one it is a menu click; from a hook it means spawning
   batchmode, which blocks on Temp/UnityLockfile and so fails precisely when someone has the Editor
   open — the common case.

So: report and block, the same shape the card-row guard settled on. The person who dropped the
folder runs the menu item themselves, in their own commit, where the diff belongs.

WHAT IT CATCHES, AND WHY NOTHING ELSE DOES
------------------------------------------
A set folder with no group renders PERFECTLY in the Editor and ships EMPTY. PackArtProvider (and
the card-art path) resolve through AssetDatabase in the Editor and through Addressables in a
player, and AddressablesSetup only creates PackArt_<setId> / CardArt_<setId> for folders it finds
when it is RUN. `AddressablesGuard` does not cover this: measured 2026-09-03, STARTER had five
faces, a valid manifest, correct importer settings and no group, and unity-verify reported 9/9 ALL
PASS. The failure surfaces as blank art in a build, with a per-entry warning nobody reads.

Scoped to commits that stage something under a set root, so it costs nothing on every other commit.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ASSETS = REPO / "unity" / "Emberfall" / "Assets"
SETTINGS = ASSETS / "AddressableAssetsData" / "AddressableAssetSettings.asset"
GROUPS_DIR = ASSETS / "AddressableAssetsData" / "AssetGroups"

# root under Assets/ -> group name prefix. Both are "one group per SUBFOLDER", which is the whole
# reason a new folder is a silent event: nothing in the folder itself refers to a group.
ROOTS = {
    "Art/Shop/Packs": "PackArt_",
    "Art/Cards": "CardArt_",
}

MENU_ITEM = "Emberfall/Addressables/Setup Groups + Migrate Card Data"


def staged_paths() -> list[str]:
    out = subprocess.run(
        ["git", "diff", "--cached", "--name-only", "--diff-filter=ACMR"],
        cwd=REPO, capture_output=True, text=True,
    )
    return [line.strip() for line in out.stdout.splitlines() if line.strip()]


def touches_a_set_root(paths: list[str]) -> bool:
    return any(f"unity/Emberfall/Assets/{root}/" in p for p in paths for root in ROOTS)


def groups_declared() -> set[str]:
    """Group names that exist as assets AND are referenced by the settings.

    Both halves matter. A .asset file nobody references is not in the build; a settings reference to
    a deleted asset is a dangling guid. Reading the group NAME out of each asset rather than
    trusting the filename, because the two can disagree.
    """
    if not SETTINGS.exists() or not GROUPS_DIR.exists():
        return set()

    referenced = set(re.findall(r"guid: ([0-9a-f]{32})", SETTINGS.read_text(encoding="utf-8", errors="replace")))

    found: set[str] = set()
    for asset in GROUPS_DIR.glob("*.asset"):
        meta = asset.with_suffix(".asset.meta")
        if not meta.exists():
            continue
        guid = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(encoding="utf-8", errors="replace"), re.M)
        if not guid or guid.group(1) not in referenced:
            continue
        name = re.search(r"^  m_GroupName: (.+)$", asset.read_text(encoding="utf-8", errors="replace"), re.M)
        found.add((name.group(1) if name else asset.stem).strip())
    return found


def main() -> int:
    paths = staged_paths()
    if not touches_a_set_root(paths):
        return 0

    declared = groups_declared()
    missing: list[tuple[str, str]] = []
    for root, prefix in ROOTS.items():
        base = ASSETS / root
        if not base.is_dir():
            continue
        for folder in sorted(p for p in base.iterdir() if p.is_dir()):
            # Card backs are cosmetics resolved through the UI sprite provider, not a card set.
            group = "UI_Core" if root == "Art/Cards" and folder.name == "Backs" else prefix + folder.name
            if group not in declared:
                missing.append((f"Assets/{root}/{folder.name}/", group))

    if not missing:
        return 0

    print()
    print("  ADDRESSABLES GROUP MISSING -- this art would render in the Editor and SHIP EMPTY.")
    print()
    for folder, group in missing:
        print(f"    {folder:<46} has no {group}")
    print()
    print("  A set folder is inert until the groups are rebuilt: art resolves through AssetDatabase")
    print("  in the Editor and through Addressables in a player. AddressablesGuard does not catch")
    print("  this -- it passed 9/9 while STARTER had five faces and no group (2026-09-03).")
    print()
    print(f"  Fix, about a second in an open Editor:   {MENU_ITEM}")
    print("  Headless (needs the FULL type name, and no Editor holding the project lock):")
    print("    Unity.exe -batchmode -nographics -quit -projectPath unity/Emberfall \\")
    print("      -executeMethod Emberfall.Editor.AddressablesSetup.Run -logFile <log>")
    print()
    print("  Then stage the AddressableAssetsData changes yourself. THIS HOOK DELIBERATELY DOES NOT")
    print("  RUN IT FOR YOU -- see the module docstring, and the account in .githooks/pre-commit of")
    print("  the hook `git add` that pushed a card asset inside an unrelated merge.")
    print()
    print("  Bypass:  git commit --no-verify")
    print()
    return 1


if __name__ == "__main__":
    sys.exit(main())
