#!/usr/bin/env python3
"""Find UI Toolkit controls authored smaller than Android's 48dp touch floor.

#244 asked for `.ms-gear` to go 44 -> 48 and for a sweep of everything else now readable
as a dp. This is that sweep, kept as a script rather than done once by eye, because the
thing that makes it checkable is new and easy to lose again:

    Since 99b179b1 the panels run ConstantPhysicalSize at referenceDpi 160, so ONE
    AUTHORED PIXEL IS ONE ANDROID dp. Before that no USS number was comparable to
    Google's 48dp guidance and this audit could not have been written at all.

WHAT IT CHECKS. A rule that sets an explicit `width`/`height`/`min-*` in bare px, on a
selector this script believes is INTERACTIVE, where the value is under 48.

HOW IT DECIDES A SELECTOR IS INTERACTIVE. Never by name — `.mm-footer` and `.ms-gear`
read the same to a substring match, and guessing wrong in either direction makes the
audit worthless. Three pieces of real evidence, any one of which counts:

  pseudo    the sheet itself declares `:hover`, `:active`, `:checked` or `:focus` on the
            same selector. A stylesheet only says that about something it expects to be
            pressed.
  uxml      the class or #name is used in UXML on a `<ui:Button>`, `<ui:Toggle>`,
            `<ui:RadioButton>`, `<ui:Slider>` or `<ui:Foldout>`.
  code      a C# file registers a ClickEvent/PointerDownEvent, or assigns `.clicked`, on
            an element queried by that class or name.

Anything with no evidence is listed separately as UNCLASSIFIED rather than dropped, so a
control the heuristics cannot see is visible as a gap instead of counted as a pass.

BORDER-BOX. UI Toolkit sizes border-box, so an authored 48 IS 48dp of touch target with
no adjustment for the rule's own border-width. (#244's fix note said `.ms-gear` carried
3px borders; the sheet reads 1px. Neither figure changes the fix, and neither belongs in
the arithmetic.)

TWO PARSER BUGS THIS FILE HAS ALREADY HAD, both of which produced a confident green:

  1. The `;` was missing from the size regex, so it matched nothing and the audit printed
     "0 selectors ... PASS". There is a self-test below that refuses to pass over an
     empty set now.
  2. Comments were not stripped, so prose inside `/* ... */` was read as a selector list
     and the report invented rows named `(Sam`, `show`, `creation`, `art`, `badge`.

Exit 1 if any interactive control is under the floor, so this can gate. Exit 2 if the
parser itself came up empty.

    python tooling/scripts/audit_touch_targets.py
    python tooling/scripts/audit_touch_targets.py --all   # also list what passed
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
UI = REPO / "unity" / "Emberfall" / "Assets" / "UI"
SCRIPTS = REPO / "unity" / "Emberfall" / "Assets" / "Scripts"

FLOOR_DP = 48

# A control deliberately under the floor records WHY here, keyed by the full selector as
# it appears in the sheet, rather than in a comment the audit cannot read. Exemptions are
# PRINTED, not silently skipped — an allowlist nobody reads is how the 44s survived.
EXEMPT: dict[str, str] = {
    ".hero-tile-badge__disc":
        "not the touch target — the VISIBLE disc inside .hero-tile-badge, which is a "
        "transparent 48dp box that catches the tap. It is 28dp because the portrait it "
        "sits on is only 64dp square on a landscape phone and a 48dp disc covered 56% of "
        "the hero's face. The evidence that flags it is a :hover rule on the PARENT "
        "(.hero-tile-badge:hover .hero-tile-badge__disc), which is correct — the parent is "
        "interactive, this is its paint.",
}


THEME = UI / "Deckbuilder" / "theme.uss"


def read(p: pathlib.Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


def token_values() -> list[tuple[str, float]]:
    """Every `--touch-min` declaration in theme.uss, with the selector it sits under.

    The fix for #244 replaced fifteen bare pixel values with `var(--touch-min)`, which takes
    them out of this audit's reach by design — a token IS the compliant form. So the token
    itself has to be checked, or the whole sweep could be undone by editing one line that
    nothing looks at.

    #247 then made it a RUN rather than a single value: the base in `:root` is the phone, and
    `.emb-sw-medium` / `.emb-sw-expanded` step up from it. Reading only the first declaration
    would check the phone and silently ignore the other two — the safe direction, but not the
    whole answer, and "safe direction" is how an audit rots. ALL of them must clear the floor.
    """
    text = read(THEME)
    out: list[tuple[str, float]] = []
    for m in re.finditer(r"--touch-min\s*:\s*(\d+(?:\.\d+)?)px\s*;", text):
        # Nearest selector opening a block above this declaration.
        head = text[:m.start()]
        sels = re.findall(r"([^{};/]+)\{", head)
        where = sels[-1].strip().splitlines()[-1].strip() if sels else "?"
        out.append((where, float(m.group(1))))
    return out


# ── the sheets ──────────────────────────────────────────────────────────────────────
COMMENT = re.compile(r"/\*.*?\*/", re.S)
RULE = re.compile(r"([^{}]+?)\s*\{([^{}]*)\}", re.S)
# The trailing `;` is NOT optional. Leaving it off made this regex hit nothing at all and
# the audit reported a PASS over an empty set. Re-check any change here against a known
# bad row — `.ms-gear`, `width: 44px;`.
SIZE = re.compile(r"^[ \t]*(width|height|min-width|min-height)[ \t]*:[ \t]*"
                  r"(\d+(?:\.\d+)?)px[ \t]*;", re.M)


def strip_comments(text: str) -> str:
    """Blank out /* ... */ but KEEP every newline, so line numbers survive.

    Not cosmetic. The sheets here carry long prose comments, and a bare rule regex read
    `(Sam, 2026-07-24: ... show ... creation)` as a selector list — the first run of this
    audit invented eight selectors named after words inside a comment.
    """
    return COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)


def selectors(raw: str) -> list[str]:
    return [s.strip() for s in raw.split(",") if s.strip()]


def bases(sel: str) -> list[str]:
    """`.deck-drawer__back.chrome-button:hover` -> ['.deck-drawer__back', '.chrome-button'].

    Interactivity is a property of the ELEMENT, so evidence found for `.chrome-button`
    anywhere counts for `.deck-drawer .chrome-button` and for the COMPOUND
    `.deck-drawer__back.chrome-button` too. Splitting the compound matters: without it
    `.deck-drawer__back.chrome-button` and `.shop-action` fell into UNCLASSIFIED while the
    plain `.chrome-button` they modify was correctly flagged — the same button, sorted two
    different ways. Reporting stays on the full selector.
    """
    sel = re.sub(r":[a-zA-Z-]+(\([^)]*\))?", "", sel).strip()
    parts = sel.split()
    if not parts:
        return []
    last = parts[-1]
    simple = [s for s in re.split(r"(?=[.#])", last) if s and re.match(r"^[.#]", s)]
    return simple or [last]


def scan_uss():
    """-> {full selector: (bases, [(file, line, prop, value)])}, {bases with a pseudo}."""
    sized: dict[str, tuple[str, list]] = {}
    pseudo: set[str] = set()
    for path in sorted(UI.rglob("*.uss")):
        text = strip_comments(read(path))
        for m in RULE.finditer(text):
            head, body = m.group(1), m.group(2)
            for sel in selectors(head):
                bs = bases(sel)
                if not bs:
                    continue
                if re.search(r":(hover|active|checked|focus)\b", sel):
                    pseudo.update(bs)
                for sm in SIZE.finditer(body):
                    off = m.start(2) + sm.start()
                    # re.M anchors ^ AFTER the newline, so this is the property's own line.
                    ln = text.count("\n", 0, off) + 1
                    key = re.sub(r":[a-zA-Z-]+(\([^)]*\))?", "", sel).strip()
                    entry = sized.setdefault(key, (bs, []))
                    entry[1].append((path.relative_to(UI).as_posix(), ln,
                                     sm.group(1), float(sm.group(2))))
    return sized, pseudo


# ── the markup ──────────────────────────────────────────────────────────────────────
INTERACTIVE_TAGS = ("Button", "Toggle", "RadioButton", "Slider", "SliderInt", "Foldout")


def scan_uxml() -> set[str]:
    """Classes and #names used on an element whose TAG is an interactive control."""
    found: set[str] = set()
    tag = re.compile(r"<(?:ui:)?(" + "|".join(INTERACTIVE_TAGS) + r")\b([^>]*)>")
    for path in sorted(UI.rglob("*.uxml")):
        for m in tag.finditer(read(path)):
            attrs = m.group(2)
            cls = re.search(r'class\s*=\s*"([^"]*)"', attrs)
            if cls:
                found.update("." + c for c in cls.group(1).split())
            nm = re.search(r'name\s*=\s*"([^"]*)"', attrs)
            if nm:
                found.add("#" + nm.group(1))
    return found


# ── the controllers ─────────────────────────────────────────────────────────────────
def scan_code() -> set[str]:
    """Classes and #names an element is queried by, near a line that wires a click.

    Deliberately crude and deliberately WIDE: it scans a window around each click
    registration rather than resolving the variable, because a false positive here only
    means one extra row gets measured, while a false negative means a real 44dp button is
    silently declared fine.
    """
    found: set[str] = set()
    click = re.compile(r"RegisterCallback<\s*(?:Click|PointerDown|PointerUp)Event\s*>"
                       r"|\.clicked\s*\+?=")
    q = re.compile(r'\.Q<[^>]*>\s*\(\s*"([^"]+)"(?:\s*,\s*"([^"]+)")?'
                   r'|\.Q\s*\(\s*"([^"]+)"(?:\s*,\s*"([^"]+)")?'
                   r'|AddToClassList\s*\(\s*"([^"]+)"')
    for path in sorted(SCRIPTS.rglob("*.cs")):
        lines = read(path).splitlines()
        for i, line in enumerate(lines):
            if not click.search(line):
                continue
            window = "\n".join(lines[max(0, i - 12):i + 3])
            for m in q.finditer(window):
                name, cls_a, name2, cls_b, added = m.groups()
                for nm in (name, name2):
                    if nm:
                        found.add("#" + nm)
                for c in (cls_a, cls_b, added):
                    if c:
                        found.add("." + c)
    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true",
                    help="also list interactive controls that meet the floor")
    args = ap.parse_args()

    sized, pseudo = scan_uss()

    # SELF-TEST. This audit's failure mode is a regex that matches nothing and then reports
    # a confident PASS over an empty set — which is exactly what it did on its first run.
    # Every sheet losing its px sizes at once is possible in principle and a parser bug in
    # practice, so refuse rather than pass.
    if not sized:
        print(f"audit_touch_targets: FAILED — parsed 0 px-sized selectors from {UI}.\n"
              "The sheets are not empty; the parser is. Refusing to report a PASS over "
              "nothing.", file=sys.stderr)
        return 2

    from_uxml = scan_uxml()
    from_code = scan_code()
    sheets = len(list(UI.rglob("*.uss")))

    print(f"#244 · TOUCH TARGETS vs Android's {FLOOR_DP}dp floor")
    print("An authored px has been one Android dp since 99b179b1 (ConstantPhysicalSize,")
    print("referenceDpi 160), so the sheet reads directly against Google's guidance.")
    print()
    print(f"  {len(sized)} selector(s) set an explicit px size across {sheets} sheet(s)")
    print(f"  interactivity evidence: {len(pseudo)} by pseudo-class, "
          f"{len(from_uxml)} by UXML tag, {len(from_code)} by wired click")
    print()

    toks = token_values()
    token_bad = False
    if not toks:
        print(f"  --touch-min: NOT DECLARED in {THEME.relative_to(REPO).as_posix()}")
        print("     Every var(--touch-min) in the sheets resolves to nothing, and a control")
        print("     sized by it collapses. This is a failure, not a missing nicety.")
        token_bad = True
    else:
        print(f"  --touch-min declarations ({len(toks)} — one per screen class since #247):")
        for where, val in toks:
            ok = val >= FLOOR_DP
            token_bad = token_bad or not ok
            flag = "" if ok else f"   <<< BELOW the {FLOOR_DP}dp floor it exists to name"
            print(f"     {where:<20} {val:g}px{flag}")
        if not token_bad:
            print(f"     all at or over {FLOOR_DP}dp, so every var(--touch-min) site below is")
            print( "     compliant by construction, on every screen class.")
    print()

    under, over, unclassified, exempted = [], [], [], []
    for sel, (bs, hits) in sorted(sized.items()):
        why = [w for w, src in (("pseudo", pseudo), ("uxml", from_uxml), ("code", from_code))
               if any(b in src for b in bs)]
        small = [h for h in hits if h[3] < FLOOR_DP]
        smallest = min(h[3] for h in hits)
        row = (sel, smallest, small or hits, ",".join(why))
        if small and sel in EXEMPT:
            exempted.append(row)
        elif not why:
            if small:
                unclassified.append(row)
        elif small:
            under.append(row)
        else:
            over.append(row)

    if exempted:
        print("DELIBERATELY UNDER THE FLOOR — declared in EXEMPT " + "=" * 39)
        for sel, smallest, hits, why in sorted(exempted, key=lambda r: r[0]):
            print(f"  {sel}  ({smallest:g}dp)")
            for line in EXEMPT[sel].split(". "):
                if line.strip():
                    print(f"      {line.strip().rstrip('.')}.")
        print()

    print(f"UNDER THE FLOOR — interactive and smaller than {FLOOR_DP}dp " + "=" * 33)
    if not under:
        print("  none")
    for sel, smallest, hits, why in sorted(under, key=lambda r: (r[1], r[0])):
        print(f"  {sel:<40} [{why}]")
        for f, ln, prop, val in hits:
            print(f"      {f}:{ln}   {prop}: {val:g}px")
    print()

    print("UNCLASSIFIED — under the floor, no evidence either way " + "=" * 34)
    print("  NOT a pass. This audit could not tell whether these are pressed. If one is,")
    print("  give it evidence (a :hover rule is enough) or measure it by hand.")
    if not unclassified:
        print("  none")
    for sel, smallest, hits, _ in sorted(unclassified, key=lambda r: (r[1], r[0])):
        f, ln, prop, val = hits[0]
        print(f"  {sel:<40} {prop}: {val:g}px   {f}:{ln}")
    print()

    if args.all:
        print("AT OR OVER THE FLOOR " + "=" * 67)
        for sel, smallest, hits, why in sorted(over, key=lambda r: (r[1], r[0])):
            print(f"  {sel:<40} {smallest:>6.0f}dp   [{why}]")
        print()

    if under or token_bad:
        if under:
            print(f"FAIL — {len(under)} interactive rule(s) under {FLOOR_DP}dp.")
        if token_bad:
            print("FAIL — --touch-min does not hold the floor.")
        return 1
    print(f"PASS — every --touch-min step holds at or over {FLOOR_DP}dp, and no rule this "
          f"audit can identify as interactive is under it in bare pixels.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
