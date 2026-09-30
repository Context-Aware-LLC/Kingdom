#!/usr/bin/env python3
"""Find UI Toolkit controls authored smaller than Android's 48dp touch floor.

#244 asked for `.ms-gear` to go 44 -> 48 and for a sweep of everything else. This is that
sweep, kept as a script rather than done once by eye.

AN AUTHORED PIXEL IS NOT A dp, AND THE CONVERSION IS THIS SCRIPT'S WHOLE POINT.

    It was one, once. Between 99b179b1 and 02048077 the panels ran ConstantPhysicalSize
    at referenceDpi 160 and an authored px WAS an Android dp, so `48` in a sheet could be
    read straight against Google's guidance. That world is gone (#349): the panels run
    ScaleWithScreenSize against 1920x1080 matched on HEIGHT, so the panel is 1080 authored
    px tall on every device and an authored px is a FRACTION OF THE SCREEN, not a length.

    Converting back needs only the screen's physical short side, and nothing else:

        authored px = dp * (1080 / 160) / short_side_inches = dp * 6.75 / inches

    because the pixel count and the density cancel. On the reference device — a Pixel 5a,
    2.60" of short side — Android's 48dp floor is 48 * 6.75 / 2.60 = 125 AUTHORED PX.
    That number, not 48, is what a sheet has to clear.

    So a rule reading `height: 56px` is 21dp of finger target, not 56dp. FLOOR_PX below is
    computed rather than typed so that the two can never be confused again.

WHAT IT CHECKS. A rule that sets an explicit `width`/`height`/`min-*` in bare px, on a
selector this script believes is INTERACTIVE, where the value is under FLOOR_PX.

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

BORDER-BOX. UI Toolkit sizes border-box, so an authored value is the whole touch target
with no adjustment for the rule's own border-width. (#244's fix note said `.ms-gear`
carried 3px borders; the sheet reads 1px. Neither figure changes the fix, and neither
belongs in the arithmetic.)

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
UI = REPO / "unity" / "Assets" / "UI"
SCRIPTS = REPO / "unity" / "Assets" / "Scripts"

FLOOR_DP = 48

# The panel's authored height, from EmberfallPanelSettings' 1920x1080 reference matched on
# height. Every authored px is 1/1080 of the screen's short side, on every device.
PANEL_SHORT_AXIS = 1080

# The reference device: a Pixel 5a, 2400 x 1080 in a 6.34" diagonal at 20:9, which puts
# 1080 px across 2.60" of glass. A SMALLER phone than this needs a LARGER authored number
# for the same finger, so the reference has to be the smallest device supported, not a
# typical one. RETIREMENT TRIGGER: if a smaller phone is supported, this number moves and
# every sheet has to be re-checked against the new floor.
REFERENCE_SHORT_SIDE_INCHES = 2.60

# dp -> authored px. See the module docstring: the pixel count and the density cancel, so
# only the physical short side survives. 48dp is 125 authored px on the reference device.
FLOOR_PX = round(FLOOR_DP * (PANEL_SHORT_AXIS / 160.0) / REFERENCE_SHORT_SIDE_INCHES)


def to_dp(authored_px: float) -> float:
    """The physical size, in Android dp, of an authored USS number on the reference device."""
    return authored_px * 160.0 * REFERENCE_SHORT_SIDE_INCHES / PANEL_SHORT_AXIS


# A control deliberately under the floor records WHY here, keyed by the full selector as
# it appears in the sheet, rather than in a comment the audit cannot read. Exemptions are
# PRINTED, not silently skipped — an allowlist nobody reads is how the 44s survived.
EXEMPT: dict[str, str] = {
    ".hero-tile-badge__disc":
        "not the touch target — the VISIBLE disc inside .hero-tile-badge, which is a "
        "transparent box that catches the tap. It is smaller than the box because the "
        "portrait it sits on is small on a landscape phone and a full-floor disc covered "
        "56% of the hero's face. The evidence that flags it is a :hover rule on the PARENT "
        "(.hero-tile-badge:hover .hero-tile-badge__disc), which is correct — the parent is "
        "interactive, this is its paint. NOTE: the sizes in this exemption's reasoning were "
        "authored in the pre-#349 world where an authored px was a dp; the RELATION it "
        "describes still holds, the absolute numbers do not.",
}


THEME = UI / "Common" / "theme.uss"


def read(p: pathlib.Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


def token_values() -> list[tuple[str, float]]:
    """Every `--touch-min` declaration in theme.uss, with the selector it sits under.

    The fix for #244 replaced fifteen bare pixel values with `var(--touch-min)`, which takes
    them out of this audit's reach by design — a token IS the compliant form. So the token
    itself has to be checked, or the whole sweep could be undone by editing one line that
    nothing looks at.

    #247 briefly made it a RUN of three, one per screen class. #349 deleted the screen classes
    (the panel's short axis became a constant, so only one branch could ever fire) and folded
    the live step back into the base, so there is one declaration again. This still reads ALL
    of them rather than the first: a second one reappearing silently is exactly the shape of
    the bug that made the run unreadable in the first place.
    """
    # COMMENTS ARE BLANKED, NOT REMOVED — same bug as parser bug 2 in the module docstring,
    # which was fixed for the sheet scan and never for this function. Prose inside a `/* */`
    # can contain a `{`, and the "nearest selector above" search then reported the token as
    # living under a fragment of a sentence. Blanking rather than deleting keeps every offset
    # aligned with the original text, so m.start() still means what it says.
    text = COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), read(THEME))
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
    print(f"AN AUTHORED px IS NOT A dp (#349). The panel is {PANEL_SHORT_AXIS} authored px across")
    print(f"{REFERENCE_SHORT_SIDE_INCHES}\" of glass on the reference phone, so {FLOOR_DP}dp of finger is "
          f"{FLOOR_PX} AUTHORED px.")
    print(f"Everything below is measured against {FLOOR_PX}, with the dp it works out to in brackets.")
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
        print(f"  --touch-min declarations ({len(toks)}):")
        for where, val in toks:
            ok = val >= FLOOR_PX
            token_bad = token_bad or not ok
            flag = "" if ok else f"   <<< BELOW the {FLOOR_PX}px ({FLOOR_DP}dp) floor it exists to name"
            print(f"     {where:<20} {val:g}px  = {to_dp(val):.0f}dp{flag}")
        if not token_bad:
            print(f"     at or over {FLOOR_PX}px, so every var(--touch-min) site below is")
            print( "     compliant by construction.")
    print()

    under, over, unclassified, exempted = [], [], [], []
    for sel, (bs, hits) in sorted(sized.items()):
        why = [w for w, src in (("pseudo", pseudo), ("uxml", from_uxml), ("code", from_code))
               if any(b in src for b in bs)]
        small = [h for h in hits if h[3] < FLOOR_PX]
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
            print(f"  {sel}  ({smallest:g}px = {to_dp(smallest):.0f}dp)")
            for line in EXEMPT[sel].split(". "):
                if line.strip():
                    print(f"      {line.strip().rstrip('.')}.")
        print()

    print(f"UNDER THE FLOOR — interactive and smaller than {FLOOR_PX}px ({FLOOR_DP}dp) " + "=" * 22)
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
            print(f"  {sel:<40} {smallest:>6.0f}px = {to_dp(smallest):>3.0f}dp   [{why}]")
        print()

    if under or token_bad:
        if under:
            print(f"FAIL — {len(under)} interactive rule(s) under {FLOOR_PX}px ({FLOOR_DP}dp).")
        if token_bad:
            print("FAIL — --touch-min does not hold the floor.")
        return 1
    print(f"PASS — every --touch-min step holds at or over {FLOOR_PX}px, and no rule this "
          f"audit can identify as interactive is under it in bare pixels.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
