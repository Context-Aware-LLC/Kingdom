#!/usr/bin/env python3
"""Gate UXML WELL-FORMEDNESS, not style.

Unity parses a `.uxml` as XML and, on a parse error, hands the UIDocument an EMPTY
rootVisualElement. Nothing is logged by the parser. The first thing anyone sees is a controller's
own "No rootVisualElement" line, or — for a controller that does not check — a blank screen with
a clean log. It presents as a wiring bug (is the asset assigned? is the PanelSettings right?) and
the wiring is fine.

The specific way it happens here is a DOUBLE HYPHEN INSIDE AN XML COMMENT, which XML forbids
outright. These files carry long design-rationale comments, and the rationale is about CSS, so it
quotes custom properties: writing `var(--edge-safe)` inside a comment makes the whole file
unparseable. AccountCreateScreen.uxml shipped exactly that on 2026-08-29 and a first launch was a
blank screen post-splash.

Same reasoning as the stylelint stage in tools/ci/gates.ps1 (Stage 1b): parseability only. There
are no style rules here and there should not be.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

UI_ROOT = Path(__file__).resolve().parents[2] / "unity" / "Emberfall" / "Assets" / "UI"


def main() -> int:
    files = sorted(UI_ROOT.rglob("*.uxml"))
    if not files:
        print(f"No .uxml found under {UI_ROOT} — the path moved, and this gate is now checking nothing.")
        return 1

    failures = []
    for path in files:
        try:
            ET.parse(path)
        except ET.ParseError as exc:
            failures.append((path, exc))

    for path, exc in failures:
        rel = path.relative_to(UI_ROOT.parents[3])
        print(f"UNPARSEABLE  {rel}")
        print(f"             {exc}")
        print("             Unity will hand this UIDocument an EMPTY root and log nothing.")
        print("             Most likely a '--' inside an XML comment (e.g. a quoted CSS var).")

    print(f"{len(files)} .uxml checked, {len(failures)} unparseable")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
