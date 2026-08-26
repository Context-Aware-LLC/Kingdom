#!/usr/bin/env python3
"""Audit UI screens for hardware edge-clearance handling.

A phone's camera cutout eats a strip of one screen edge. Anything anchored to
that edge lands under it: on 2026-08-21 the shop's Back button sat directly
beneath a Pixel 5a's front camera and could not be seen or pressed.

`theme.uss` holds the whole policy in three declarations:

    --edge-safe: <n>px;
    .emb-edge  { margin:  var(--edge-safe); }   -- chrome anchored to a screen edge
    .emb-bleed { padding: var(--edge-safe); }   -- a layer that PAINTS to the edge

The value is deliberately not repeated here -- PASS 5 prints the live one, and a
number copied into a docstring is a number that goes stale silently.

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
  PASS 6  2+USS -> the SPECIFICITY    rules that would outrank the theme classes

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
THEME = UI + '/Common/theme.uss'
EDGE_CLASS = 'emb-edge'
BLEED_CLASS = 'emb-bleed'
ROOT_CLASS = 'emb-root'

# Classes whose USS anchors to an edge but which are NOT screen chrome: they sit
# inside a tile, a plate or a card, and taking --edge-safe would throw them off
# their own parent. Each entry names the element that owns it.
# Screen-level elements that legitimately spend --edge-safe THEMSELVES because
# neither theme class expresses what they need. Unlike ELEMENT_INTERNAL these do
# clear the edge -- they just cannot do it with `margin: var(--edge-safe)` or
# `padding: var(--edge-safe)` on all four sides. Each entry says why, because
# "the classes did not fit" is the excuse that would otherwise swallow every
# element someone could not be bothered to tag.
TOKEN_EXCEPTIONS = {
    'col-query-toolbar':
        'transparent overlay coordinate surface for Back, Decks and Filters; it spans the '
        'horizontal safe area itself so its absolute children can anchor to 0/50% without '
        'repeating or double-spending the cutout clearance',
    'deck-drawer':
        'three of its four sides are screen edges and the fourth is interior; '
        '.emb-bleed would pad all four and leave 120px of column at min-width 420',
    'craft-modal':
        'offsets on all four sides size it as well as place it; .emb-edge on top '
        'of its old percentages overflowed (8% + 150 + 84% > 1080)',
    'col-query':
        'spans left:0..right:0 at screen level, so it takes the token HORIZONTALLY '
        'and the ordinary gutter top and bottom; the Pixel 5a insets measured over '
        'USB are [136,0][0,63] / [0,0][136,63], so left/right flip and the TOP is 0 '
        'in both landscape rotations, which is the only orientation this game has',
}

ELEMENT_INTERNAL = {
    'col-button--filters':
        'ribbon positioned against the collection toolbar, not the screen; '
        'CollectionScreen.uxml parents it to .col-query-toolbar',
    'col-query-actions':
        'Decks and Back group positioned at right:0 inside .col-query-toolbar; the toolbar '
        'already spans left/right --edge-safe, so clearing this child again would double-inset it',
    'deck-plate-star':
        'star on a deck plate; CharacterRosterController.cs parents it to the plate',
    'hero-tile-badge':
        'badge on a hero tile; CharacterRosterController.cs parents it to the tile',
    'reveal-card__flourish':
        'decoration on a revealed card at top:-18% left:-15%; PackRevealPanel.cs '
        'parents it to the card',
    'col-filter-ribbon__chevron-arm':
        'the two rotated bars that draw the FILTERS chevron; CollectionScreen.uxml:9-12 '
        'parents both to .col-filter-ribbon__chevron inside the #query-toggle Button, and '
        'their offsets are against that 22x12 box. Font-independent by design — the glyph '
        'they replace was a missing character',
    'col-query-bracket':
        'the decorative frame INSIDE the query bar; CollectionScreen.uxml:63 and :68 parent '
        'both to #query-bar, not to .emb-root, and both carry picking-mode="Ignore". This '
        'rule holds only the top/bottom offsets, which are against that bar',
    'col-query-bracket--left':
        'non-pickable decorative upper leg inside #query-bar; it intentionally paints to the '
        'panel edge while interactive content remains inside the parent panel safe padding',
    'col-query-bracket--right':
        'matched non-pickable decorative upper leg inside #query-bar; same intentional bleed',
    'col-query-bracket__piece':
        'the three pieces of the decorative bracket frame INSIDE the query bar; '
        'CollectionScreen.uxml:63-72 parents all six to .col-query-bracket, which is '
        'itself inside .col-query and carries picking-mode="Ignore". Their offsets are '
        'against that bracket, so --edge-safe would push them off the bay they draw',
    'craft-illustration-scrim':
        'the scrim over the craft modal\'s backdrop illustration; '
        'CollectionScreen.uxml:121 parents it to #craft-modal, which already spends '
        '--edge-safe on all four sides (see TOKEN_EXCEPTIONS above). Clearing the edge '
        'twice would inset it from an inset',
}


def _read(p):
    return io.open(p, encoding='utf-8').read()


def _files(root, ext):
    return sorted(p.replace(os.sep, '/') for p in
                  glob.glob('%s/**/*.%s' % (root, ext), recursive=True))


def _short(p, marker):
    return p.split(marker)[-1]


def _rule_body(path, cls):
    m = re.search(r'^\.%s\s*\{([^}]*)\}' % re.escape(cls), _read(path), re.M)
    return m.group(1) if m else ''


def _owning_class(text, lineno):
    """The class whose rule block contains 1-indexed `lineno`, or None."""
    for m in re.finditer(r'(^[.#][\w\-]+(?:[.:][\w\-]+)*)[^{]*\{([^}]*)\}', text, re.M):
        first = text[:m.start()].count('\n') + 1
        last = text[:m.end()].count('\n') + 1
        if first <= lineno <= last:
            return m.group(1).lstrip('.#').split(':')[0].split('.')[0]
    return None


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
        if cls in TOKEN_EXCEPTIONS:
            body = _rule_body(f, cls)
            if '--edge-safe' not in body:
                findings.append(
                    '%s:%d  .%s is declared in TOKEN_EXCEPTIONS, which is a claim that it '
                    'spends\n      --edge-safe itself, but its rule never mentions the '
                    'token. Either use it or drop\n      the declaration.'
                    % (_short(f, 'Assets/UI/'), line, cls))
                print('   %-42s %-24s %-9s %s'
                      % (where[:42], sel[:24], 'FAIL', 'declared exception, unused token'))
            else:
                print('   %-42s %-24s %-9s %s'
                      % (where[:42], sel[:24], 'token', TOKEN_EXCEPTIONS[cls][:34]))
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

    # (0) The two theme classes must still BE something. Every other pass in this
    # script checks that elements opt IN to the mechanism; none of them check that
    # the mechanism has a body. Comment out .emb-edge and .emb-bleed in theme.uss
    # and passes 1-6 all still say ok -- the elements carry the class, the class
    # just no longer does anything -- so the gate reports PASS on a screen with
    # zero clearance. Measured on effect-model-merge, 2026-08-23: both rules
    # commented out, audit exit 0. _rule_body() cannot catch this on its own
    # because its ^\. anchor simply misses a `/* .emb-edge {` and returns ''.
    theme_text = _read(THEME)
    theme_commented = _comment_lines(theme_text)
    for cls in (EDGE_CLASS, BLEED_CLASS):
        m = re.search(r'^\.%s\s*\{([^}]*)\}' % re.escape(cls), theme_text, re.M)
        live = m and (theme_text[:m.start()].count('\n') + 1) not in theme_commented
        if live and '--edge-safe' in m.group(1):
            print('   %-42s ok    .%s spends --edge-safe' % (_short(THEME, 'Assets/UI/'), cls))
            continue
        state = 'commented out' if m is None else 'does not spend --edge-safe'
        print('   %-42s FAIL  .%s %s' % (_short(THEME, 'Assets/UI/'), cls, state))
        findings.append(
            'theme.uss  .%s %s, so every element carrying it gets NO clearance.\n'
            '      The rest of this audit checks that elements opt IN to the mechanism; it '
            'cannot\n      see that the mechanism is gone. Restore the rule, or delete the '
            'class from the\n      %d element(s) that carry it and retire this gate.'
            % (cls, state, len(used.get(cls, []))))

    for f in _files(UI, 'uss'):
        text = _read(f)
        commented = _comment_lines(text)
        for i, line in enumerate(text.split('\n'), 1):
            if '--edge-safe' not in line or i in commented:
                continue
            where = '%s:%d' % (_short(f, 'Assets/UI/'), i)
            if f == THEME:
                print('   %-42s ok    %s' % (where, line.strip()[:44]))
                continue
            owner = _owning_class(text, i)
            if owner in TOKEN_EXCEPTIONS:
                print('   %-42s token .%s' % (where, owner))
                continue
            print('   %-42s FAIL  spent outside theme.uss, undeclared' % where)
            findings.append(
                '%s:%d  --edge-safe is spent outside theme.uss by .%s, which is not in '
                'TOKEN_EXCEPTIONS.\n      Use .%s or .%s, or declare the exception with '
                'the reason the classes do not fit.'
                % (_short(f, 'Assets/UI/'), i, owner or '?', EDGE_CLASS, BLEED_CLASS))

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


def pass6_specificity(used):
    """The theme only wins if nothing outranks it, on the SAME property.

    Both halves of this project's edge-clearance work were defeated this way
    before the check existed. First SafeAreaPanel wrote `style.padding*` INLINE,
    which beats any stylesheet. Then `#shop-overlay { padding: 0 24 24 }` -- an ID
    selector, specificity (1,0,0) against .emb-bleed's (0,1,0) -- kept the store
    flush to the screen edge while every other screen had cleared it. Neither
    reported anything; the store simply looked untouched.

    Two precision rules, both learned from this pass's own first run:

      PROPERTY. .emb-edge sets MARGIN and .emb-bleed sets PADDING, so only the
      matching property is a conflict. `.ms-gear { padding: 0 }` centres an icon
      inside a button and has nothing to do with the margin that moves it.

      TARGET. A selector targets its LAST simple selector, not its first.
      `#shop-overlay .shop-shell { padding: 0 }` styles the shell; reading it as
      a rule on the overlay reported four conflicts that were not there.
    """
    _head(6, 'SPECIFICITY - rules that would outrank the theme classes')
    findings = []
    prop_of = {EDGE_CLASS: 'margin', BLEED_CLASS: 'padding'}

    targets = {}
    for theme_cls, prop in prop_of.items():
        for f, line, classes, _indent in used.get(theme_cls, []):
            src = _read(f).split('\n')[line - 1]
            nm = re.search(r'name="([^"]+)"', src)
            key = (f, line)
            entry = targets.setdefault(
                key, {'name': nm.group(1) if nm else None, 'classes': classes, 'props': {}})
            entry['props'][prop] = theme_cls

    if not targets:
        print('   (no element carries a theme class)')
        return findings

    for (uf, uline), t in sorted(targets.items()):
        name = t['name']
        others = [c for c in t['classes']
                  if c not in (EDGE_CLASS, BLEED_CLASS, ROOT_CLASS)]
        hits = []
        for f in _files(UI, 'uss'):
            if f == THEME:
                continue
            text = _read(f)
            commented = _comment_lines(text)
            for m in re.finditer(r'(^[^{}/][^{}]*?)\{([^}]*)\}', text, re.M):
                sel, body = m.group(1).strip(), m.group(2)
                first = text[:m.start()].count('\n') + 1
                if first in commented or not sel:
                    continue
                # A selector targets its LAST simple selector.
                last = re.split(r'[\s>]+', sel.split(',')[-1].strip())[-1]
                key = last.lstrip('.#').split(':')[0].split('.')[0]
                by_id = last.startswith('#') and name and key == name
                by_cls = last.startswith('.') and key in others
                if not (by_id or by_cls):
                    continue
                # Strip comments from the BODY too, not only whole commented
                # rules: this pass flagged its own explanatory "No padding: ..."
                # note as a declaration. Third precision bug this check found in
                # itself, and the same lesson each time -- discussing a property
                # is not setting one.
                bare = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
                for prop, theme_cls in t['props'].items():
                    if re.search(r'(^|;|\s)%s\s*:' % prop, bare):
                        hits.append((f, first, sel, prop, theme_cls,
                                     'id' if by_id else 'class'))
        label = '%s:%d %s' % (_short(uf, 'Assets/UI/'), uline, name or '')
        if not hits:
            print('   %-52s ok    nothing outranks .%s'
                  % (label[:52], '/.'.join(sorted(t['props'].values()))))
            continue
        for f, first, sel, prop, theme_cls, kind in hits:
            print('   %-52s FAIL  %s sets %s' % (label[:52], sel[:20], prop))
            findings.append(
                '%s:%d  `%s` sets %s on an element carrying .%s (%s:%d).\n'
                '      That %s selector outranks the theme, so `%s: var(--edge-safe)` '
                'never applies and\n      nothing reports it. Remove the %s here, or drop '
                '.%s and declare the element.'
                % (_short(f, 'Assets/UI/'), first, sel, prop, theme_cls,
                   _short(uf, 'Assets/UI/'), uline, kind, prop, prop, theme_cls))
    return findings


def main():
    chrome, bleed = pass1_risk_surface()
    used, root_indent = pass2_structure()
    code = pass3_code_applied()
    findings = pass4_verdict(chrome, used, code)
    findings += pass5_policy(used, root_indent)
    findings += pass6_specificity(used)

    print('\n' + '=' * 104)
    print('%d edge-anchored chrome rule(s) | %d full-bleed layer(s) | '
          '%d element-internal | %d token exception(s)'
          % (len(chrome), len(bleed), len(ELEMENT_INTERNAL), len(TOKEN_EXCEPTIONS)))
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
