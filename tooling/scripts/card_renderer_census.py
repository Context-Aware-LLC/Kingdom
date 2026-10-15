#!/usr/bin/env python3
"""EF-064 · Static census of the renderer components a single procedural card builds.

WHY THIS IS A SCRIPT AND NOT A PARAGRAPH
----------------------------------------
The "~16 renderers per card" figure in the launch workplan is the named suspect for
Android frame cost, and an earlier audit had already corrected a stale "13" comment.
A number that decides whether to spend days consolidating the card hierarchy must be
read off the source, not recalled. This parses `BoardController.CreatePresenter` and
enumerates every construct that brings a `Renderer` into being.

WHAT COUNTS AS A RENDERER
-------------------------
  GameObject.CreatePrimitive(PrimitiveType.Quad)  -> 1 MeshRenderer
  MakeSprite(...)                                 -> 1 SpriteRenderer   (AddComponent<SpriteRenderer>)
  MakeLabel(...)                                  -> 1 MeshRenderer     (TextMeshPro renders via MeshRenderer)
  AddComponent<TextMeshPro>()                     -> 1 MeshRenderer

A `SortingGroup` is NOT a renderer (it re-layers existing ones) and a `BoxCollider` is not
either; both are excluded deliberately.

DRAW CALLS != RENDERER COUNT. Several renderers are built disabled (glow, aegis, frost, the
status-effect pieces) or on an inactive GameObject (art, sockets), so the *steady-state* draw cost is lower than
the component count. The script reports both, because the component count drives
CPU-side culling//batching walk while the enabled count drives GPU submission.

Usage:  python tooling/scripts/card_renderer_census.py
Exit 0 always; this is a report, not a gate.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SRC = REPO / "unity" / "Assets" / "Scripts" / "Presentation" / "BoardController.cs"

# (regex, kind, unity_component)
SITE_PATTERNS = [
    (re.compile(r"GameObject\.CreatePrimitive\(PrimitiveType\.Quad\)"), "Quad", "MeshRenderer"),
    (re.compile(r"\bMakeSprite\("), "Sprite", "SpriteRenderer"),
    (re.compile(r"\bMakeLabel\("), "Label", "MeshRenderer(TMP)"),
    (re.compile(r"AddComponent<TextMeshPro>\(\)"), "Label", "MeshRenderer(TMP)"),
]

# Renderers dormant at build time that the parser cannot see being disabled, keyed by the name
# `name_for` recovers for the site. Each entry names HOW it is dormant:
#   "renderer"  -> Renderer.enabled = false (GameObject stays active)
#   "gameobject"-> GameObject.SetActive(false)
#   "view"      -> handed to a status view whose Setup() disables it
# A site whose own variable is disabled in the method body (`x.enabled = false`,
# `x.gameObject.SetActive(false)`) is detected automatically and needs no entry here. These
# entries are the ones that cannot be: a quad disabled through a second variable, and pieces a
# view hides. Keys are the LOCAL VARIABLE for a quad (CreatePrimitive takes no name argument),
# the GameObject name string for a sprite.
DORMANT = {
    "glowGo": "renderer",     # glowRenderer.enabled  = false
    "aegisGo": "renderer",    # aegisRenderer.enabled = false
    "frostGo": "renderer",    # frostRenderer.enabled = false
    "artGo": "gameobject",    # artGo.SetActive(false)
    # FrostCrystalView / ShroudSmokeView / VenomBubbleView.Setup -> pieces disabled
    "LeftGrowth": "view", "RightGrowth": "view", "LowerShelf": "view",
    "LeftPlume": "view", "RightPlume": "view", "LowerBank": "view",
    "First": "view", "Second": "view", "Third": "view",
}

# A local helper such as `SpriteRenderer MakeSocket(string goName, float z) => MakeSprite(...);`
# builds one renderer per CALL, not one at its definition.
HELPER_DEF = re.compile(r"^\s*\w+\s+(Make\w+)\([^)]*\)\s*=>")


def extract_method(text: str, signature_fragment: str) -> tuple[str, int]:
    """Return (body, start_line) for the brace-balanced method containing `signature_fragment`."""
    idx = text.find(signature_fragment)
    if idx < 0:
        sys.exit(f"FATAL: could not find `{signature_fragment}` in {SRC}")
    brace = text.find("{", idx)
    depth, i = 0, brace
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    return text[brace : i + 1], text[:idx].count("\n") + 1


def name_for(line: str) -> str:
    """Best-effort GameObject name for a renderer site."""
    m = re.search(r'"([A-Za-z0-9_{}$]+)"', line)
    if m:
        return m.group(1).replace("{i}", "N").replace("$", "")
    m = re.search(r"var\s+(\w+)\s*=", line)
    return m.group(1) if m else "?"


def dormancy(go_name: str, var: str | None, body: str) -> str | None:
    if go_name in DORMANT:
        return DORMANT[go_name]
    if var:
        v = re.escape(var)
        if re.search(rf"\b{v}\.enabled\s*=\s*false", body):
            return "renderer"
        if re.search(rf"\b{v}(\.gameObject)?\.SetActive\(false\)", body):
            return "gameobject"
    return None


def main() -> int:
    text = SRC.read_text(encoding="utf-8")
    body, method_line = extract_method(text, "private CardPresenter CreatePresenter(")
    body_start_line = text[: text.find(body)].count("\n") + 1

    lines = body.splitlines()
    patterns = list(SITE_PATTERNS)
    in_helper = False
    sites = []
    for offset, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("//") or stripped.startswith("///"):
            continue
        helper = HELPER_DEF.match(line)
        if helper:
            # Count the helper's calls (typed like what it wraps); skip its own body.
            patterns.insert(0, (re.compile(rf"\b{helper.group(1)}\("), "Sprite", "SpriteRenderer"))
            in_helper = not stripped.endswith(";")
            continue
        if in_helper:
            in_helper = not stripped.endswith(";")
            continue
        for pattern, kind, component in patterns:
            if pattern.search(line):
                lineno = body_start_line + offset
                m = re.search(r"var\s+(\w+)\s*=", line)
                sites.append((lineno, kind, component, name_for(line), m.group(1) if m else None, 1))
                break

    total = sum(s[5] for s in sites)
    dormant_count = 0
    print("EF-064 - renderer census for one card")
    print(f"source : {SRC.relative_to(REPO).as_posix()}")
    print(f"method : CreatePresenter  (line {method_line})")
    print()
    print(f"{'line':>6}  {'kind':<7} {'component':<17} {'x':<3} {'gameobject':<14} state")
    print("-" * 74)
    for lineno, kind, component, go, var, mult in sites:
        how = dormancy(go, var, body)
        if how:
            dormant_count += mult
            state = "DORMANT (%s)" % how
        else:
            state = "live"
        print(f"{lineno:>6}  {kind:<7} {component:<17} {mult:<3} {go:<14} {state}")
    print("-" * 74)
    live = total - dormant_count
    print(f"{'':>6}  TOTAL renderer components per card : {total}")
    print(f"{'':>6}    of which dormant at build time   : {dormant_count}")
    print(f"{'':>6}    of which drawing at build time   : {live}")
    print()

    # CardPresenter builds some pieces on first use (mulligan mark, tooltip banner), not in
    # CreatePresenter, so a card that never needs them never has them.
    presenter = SRC.parent / "CardPresenter.cs"
    ptext = presenter.read_text(encoding="utf-8")
    lazy = sum(len(p.findall(ptext)) for p, _, _ in SITE_PATTERNS)
    if lazy:
        print(f"NOT COUNTED ABOVE: {lazy} lazily-built renderer(s) in "
              f"{presenter.name} (mulligan mark, tooltip banner; only exist once needed).")

    print()
    print(f"BOARD SCALE: a full board is 2 heroes x 7 minions + 2 hands. At {total} "
          f"renderers/card,\n  14 board minions alone = {14 * total} renderer components.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
