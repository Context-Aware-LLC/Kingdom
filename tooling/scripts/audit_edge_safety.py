#!/usr/bin/env python3
"""Audit UI screens for hardware edge-clearance handling.

A phone's camera cutout eats a strip of one screen edge. Anything anchored to
that edge lands under it: on 2026-08-21 the shop's Back button sat directly
beneath a Pixel 5a's front camera and could not be seen or pressed.

`theme.uss` holds the whole policy in three declarations:

    --edge-safe: 150px;
    .emb-edge  { margin:  var(--edge-safe); }   -- chrome anchored to a screen edge
    .emb-bleed { padding: var(--edge-safe); }   -- a layer that PAINTS to the edge

MARGIN for chrome, not padding, and that is not a style preference. Measured in
Play Mode 2026-08-21: a parent padding of 136 moved the identity plate, the
footer and the shard counter by exactly 0, because a parent's padding does not
reach a position:absolute child. The same 136 as margin moved all three.

WHAT MAKES THIS WORTH A GATE. The failure is silent in every direction that
matters. A screen whose corner chrome never opts in renders correctly on a
desktop, in the Editor, in the Device Simulator, and on any phone without a
cutout. It is wrong only on real hardware, only in one orientation, and only for
the person holding it. 65831e56 is the case in point: 44b8831a tagged MainMenu's
three chrome elements, MatchSettings' settings gear was missed, and nothing
anywhere reported it -- it took Sam turning a phone over and reading three
corners against a fourth.

WHY IT CANNOT SIMPLY BE `.emb-root > *`. Three of the edge-anchored rules in this
project are positioned inside a TILE or a CARD, not against the screen:
deck-plate-star, hero-tile-badge, reveal-card__flourish. A blanket rule throws
them 150px off their own parent. Screen chrome has to be distinguished from
element chrome, and the distinction is structural: screen chrome is a DIRECT
CHILD of a `.emb-root` element.

WHY C# NEEDS THE ALLOWLIST. That structural test works on UXML, where the tree is
written down. An element built in C# (`el.AddToClassList("x")`) carries the class
but its parent is a runtime value this script cannot resolve. So for those, the
script does not guess -- it requires an entry in ELEMENT_INTERNAL saying which
element owns it. The list is a set of DECLARATIONS, not suppressions: adding to it
is a claim that the element is not at screen level, and the note says why.


SHAPE: five passes, each consuming the one before it
----------------------------------------------------
  PASS 1  USS   -> the RISK SURFACE   every rule that anchors to an edge
  PASS 2  UXML  -> the STRUCTURE      where each class is used, and how deep
  PASS 3  C#    -> the CODE-APPLIED   classes attached at runtime
  PASS 4  join  -> the VERDICT        1 classified by 2 and 3
  PASS 5  2+USS -> the POLICY         --edge-safe locality, .emb-edge placement

Each pass prints what it found and hands a value to the next, so a failure names
the stage that produced it and no stage's output has to be taken on trust. Run it
with no arguments; exit 1 on any finding.
"""

import io
import os
import re
import sys
import glob

UI = 'unity/Emberfall/Assets/UI'
SCRIPTS = 'unity/Emberfall/Assets/Scripts'
THEME = UI + '/Deckbuilder/theme.uss'
EDGE_CLASS = 'emb-edge'
BLEED_CLASS = 'emb-bleed'
ROOT_CLASS = 'emb-root'

# Classes whose USS anchors to an edge but which are NOT screen chrome: they sit
# inside a tile, a plate or a card, and taking --edge-safe would throw them off
# their own parent. Each entry names the element that owns it.
ELEMENT_INTERNAL = {
    'deck-plate-star':
        'star on a deck plate; CharacterRosterController.cs parents it to the plate',
    'hero-tile-badge':
        'badge on a hero tile; CharacterRosterController.cs parents it to the tile',
    'reveal-card__flourish':
        'decoration on a revealed card at top:-18% left:-15%; PackRevealPanel.cs '
        'parents it to the card',
}


def _read(p):
    return io.open(p, encoding='utf-8').read()


def _files(root, ext):
    return sorted(p.replace(os.sep, '/') for p in
                  glob.glob('%s/**/*.%s' % (root, ext), recursive=True))


def _short(p, marker):
    return p.split(marker)[-1]


def _head(n, title):
    print('\nPASS %d - %s' % (n, title))
    print('-' * 104)


def _comment_lines(text):
    """1-indexed line numbers falling inside a USS /* */ block.

    Needed because a rule can legitimately be DISCUSSED in a comment without
    being restated -- shop.uss explains why it does NOT set --edge-safe, and a
    naive scan reads that as a violation. Found by this script's own first run.
    """
    out, depth = set(), 0
    for i, line in enumerate(text.split('\n'), 1):
        if depth:
            out.add(i)
        j = 0
        while j < len(line):
            if not depth and line[j:j + 2] == '/*':
                depth, j = 1, j + 2
                out.add(i)
                continue
            if depth and line[j:j + 2] == '*/':
                depth, j = 0, j + 2
                continue
            j += 1
        if depth:
            out.add(i)
    return out


# ── PASS 1 ──────────────────────────────────────────────────────────────────
def pass1_risk_surface():
    """USS -> every rule that is position:absolute and anchors to an edge.

    Returns (chrome, bleed). A layer with all four offsets at 0 spans the panel
    and is a bleed candidate, not chrome, so it is kept separate rather than
    reported as a risk.
    """
    _head(1, 'RISK SURFACE - edge-anchored rules in %s/**/*.uss' % UI)
    chrome, bleed = [], []
    for f in _files(UI, 'uss'):
        s = _read(f)
        for m in re.finditer(r'(^\.[\w\-]+(?:[.:][\w\-]+)*)\s*\{([^}]*)\}', s, re.M):
            sel, body = m.group(1).strip(), m.group(2)
            if 'position: absolute' not in body:
                continue
            offs = dict(re.findall(r'\b(top|right|bottom|left)\s*:\s*([^;]+);', body))
            if not offs:
                continue
            cls = sel.lstrip('.').split(':')[0].split('.')[0]
            row = (f, s[:m.start()].count('\n') + 1, cls, sel, offs)
            (bleed if len(offs) == 4 and all(v.strip() == '0' for v in offs.values())
             else chrome).append(row)
    for f, line, cls, sel, offs in sorted(chrome):
        print('   %-42s %-24s %s' % (
            '%s:%d' % (_short(f, 'Assets/UI/'), line), sel[:24],
            '  '.join('%s:%s' % (k, v.strip()[:14]) for k, v in sorted(offs.items()))))
    print('   => %d edge-anchored, %d full-bleed (handled by .%s)'
          % (len(chrome), len(bleed), BLEED_CLASS))
    return chrome, bleed


# ── PASS 2 ──────────────────────────────────────────────────────────────────
def pass2_structure():
    """UXML -> where each class is used, alongside what, and at what depth.

    Returns (used, root_indent). `used` maps class -> [(file, line, classes,
    indent)]. `root_indent` maps file -> the indent of its .emb-root, derived per
    file rather than assumed: the screens do not all put the root at one depth.
    """
    _head(2, 'STRUCTURE - class usage and depth in %s/**/*.uxml' % UI)
    used, root_indent, sites = {}, {}, 0
    for f in _files(UI, 'uxml'):
        for i, line in enumerate(_read(f).split('\n'), 1):
            m = re.search(r'class="([^"]*)"', line)
            if not m:
                continue
            classes = m.group(1).split()
            indent = len(line) - len(line.lstrip())
            sites += 1
            for c in classes:
                used.setdefault(c, []).append((f, i, classes, indent))
            if ROOT_CLASS in classes:
                root_indent[f] = indent
    for f in sorted(root_indent):
        print('   %-52s .%s at indent %d'
              % (_short(f, 'Assets/UI/'), ROOT_CLASS, root_indent[f]))
    print('   => %d class sites across %d file(s); %d carry .%s'
          % (sites, len(_files(UI, 'uxml')), len(root_indent), ROOT_CLASS))
    return used, root_indent


# ── PASS 3 ──────────────────────────────────────────────────────────────────
def pass3_code_applied():
    """C# -> classes attached at runtime. Returns class -> [(file, line)].

    Only string literals are seen. A class built from a variable is invisible
    here, which is a known hole and the reason PASS 4 fails closed rather than
    treating "not found in C#" as "does not exist".
    """
    _head(3, 'CODE-APPLIED - AddToClassList("literal") in %s/**/*.cs' % SCRIPTS)
    out, total = {}, 0
    for f in _files(SCRIPTS, 'cs'):
        for i, line in enumerate(_read(f).split('\n'), 1):
            for m in re.finditer(r'AddToClassList\("([\w\-]+)"\)', line):
                out.setdefault(m.group(1), []).append((f, i))
                total += 1
    print('   => %d literal call(s) applying %d distinct class(es)'
          % (total, len(out)))
    return out


# ── PASS 4 ──────────────────────────────────────────────────────────────────
def pass4_verdict(chrome, used, code):
    """Classify every PASS 1 risk using PASS 2 structure and PASS 3 code sites."""
    _head(4, 'VERDICT - every edge-anchored rule classified')
    findings = []
    print('   %-42s %-24s %-9s %s' % ('FILE:LINE', 'SELECTOR', 'STATUS', 'DETAIL'))
    for f, line, cls, sel, offs in sorted(chrome):
        where = '%s:%d' % (_short(f, 'Assets/UI/'), line)
        if cls in ELEMENT_INTERNAL:
            print('   %-42s %-24s %-9s %s'
                  % (where[:42], sel[:24], 'internal', ELEMENT_INTERNAL[cls][:34]))
            continue
        sites = used.get(cls, [])
        untagged = [s for s in sites
                    if EDGE_CLASS not in s[2] and BLEED_CLASS not in s[2]]
        if sites and not untagged:
            print('   %-42s %-24s %-9s %s'
                  % (where[:42], sel[:24], 'ok', 'every use carries .' + EDGE_CLASS))
        elif sites:
            print('   %-42s %-24s %-9s %d of %d use(s) untagged'
                  % (where[:42], sel[:24], 'FAIL', len(untagged), len(sites)))
            for sf, sl, _sc, _si in untagged:
                findings.append(
                    '%s:%d  .%s anchors to a screen edge, but the element using it at '
                    '%s:%d carries\n      neither .%s nor .%s. Add .%s there, or declare '
                    'the class in ELEMENT_INTERNAL if it is not screen chrome.'
                    % (_short(f, 'Assets/UI/'), line, cls,
                       _short(sf, 'Assets/UI/'), sl,
                       EDGE_CLASS, BLEED_CLASS, EDGE_CLASS))
        elif cls in code:
            print('   %-42s %-24s %-9s %s'
                  % (where[:42], sel[:24], 'FAIL', 'built in C#, undeclared'))
            findings.append(
                '%s:%d  .%s anchors to a screen edge and is applied from C# at %s.\n'
                '      A runtime parent cannot be resolved here, so this needs a '
                'declaration: add it to\n      ELEMENT_INTERNAL naming the element that '
                'owns it, or move the class into a UXML\n      where its depth is '
                'checkable.'
                % (_short(f, 'Assets/UI/'), line, cls,
                   ', '.join('%s:%d' % (_short(a, 'Assets/Scripts/'), b)
                             for a, b in code[cls][:2])))
        else:
            print('   %-42s %-24s %-9s %s'
                  % (where[:42], sel[:24], 'unused', 'no UXML or C# applies it'))
    return findings


# ── PASS 5 ──────────────────────────────────────────────────────────────────
def pass5_policy(used, root_indent):
    """Two rules PASS 4 cannot see, both needing PASS 2's structure.

    a) --edge-safe is spent through the theme classes and restated nowhere, so
       retuning the clearance stays a one-line change.
    b) .emb-edge sits only on a direct child of .emb-root - the reverse mistake,
       tagging something that is not screen chrome.
    """
    _head(5, 'POLICY - --edge-safe locality, and .%s placement' % EDGE_CLASS)
    findings = []

    for f in _files(UI, 'uss'):
        text = _read(f)
        commented = _comment_lines(text)
        for i, line in enumerate(text.split('\n'), 1):
            if '--edge-safe' not in line or i in commented:
                continue
            where = '%s:%d' % (_short(f, 'Assets/UI/'), i)
            if f == THEME:
                print('   %-42s ok    %s' % (where, line.strip()[:44]))
            else:
                print('   %-42s FAIL  restated outside theme.uss' % where)
                findings.append(
                    '%s:%d  --edge-safe is restated outside theme.uss. It has one home so '
                    'retuning it\n      is one line; use .%s or .%s instead.'
                    % (_short(f, 'Assets/UI/'), i, EDGE_CLASS, BLEED_CLASS))

    for f, line, classes, indent in sorted(used.get(EDGE_CLASS, [])):
        where = '%s:%d' % (_short(f, 'Assets/UI/'), line)
        ri = root_indent.get(f)
        if ri is None:
            print('   %-42s FAIL  no .%s in this file' % (where, ROOT_CLASS))
            findings.append('%s:%d  carries .%s but the file has no .%s element.'
                            % (_short(f, 'Assets/UI/'), line, EDGE_CLASS, ROOT_CLASS))
        elif indent <= ri:
            print('   %-42s FAIL  indent %d vs root %d' % (where, indent, ri))
            findings.append(
                '%s:%d  carries .%s at indent %d, at or above the .%s at indent %d, so it '
                'is not\n      screen chrome. Edge clearance belongs on a direct child of '
                'the root.'
                % (_short(f, 'Assets/UI/'), line, EDGE_CLASS, indent, ROOT_CLASS, ri))
        else:
            print('   %-42s ok    direct child of .%s' % (where, ROOT_CLASS))
    return findings


def main():
    chrome, bleed = pass1_risk_surface()
    used, root_indent = pass2_structure()
    code = pass3_code_applied()
    findings = pass4_verdict(chrome, used, code)
    findings += pass5_policy(used, root_indent)

    print('\n' + '=' * 104)
    print('%d edge-anchored chrome rule(s) | %d full-bleed layer(s) | '
          '%d declared element-internal' % (len(chrome), len(bleed),
                                            len(ELEMENT_INTERNAL)))
    if findings:
        print('\nFINDINGS (%d):\n' % len(findings))
        for x in findings:
            print('  ' + x + '\n')
        return 1
    print('PASS - every edge-anchored rule is tagged or declared, and the clearance '
          'value has one home.')
    return 0


if __name__ == '__main__':
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
    sys.exit(main())
