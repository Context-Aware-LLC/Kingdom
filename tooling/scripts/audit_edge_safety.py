#!/usr/bin/env python3
"""Audit UI screens for hardware edge-clearance handling.

A phone's camera cutout eats a strip of one screen edge. Anything anchored to
that edge lands under it: on 2026-08-21 the shop's Back button sat directly
beneath a Pixel 5a's front camera and could not be seen or pressed.

`theme.uss` holds the whole policy in three declarations:

    --edge-safe: <n>px;
    .emb-edge  { margin:  0 var(--edge-safe); }  -- chrome anchored to a screen side
    .emb-bleed { padding: 0 var(--edge-safe); }  -- a layer that PAINTS to the side

The value is deliberately not repeated here -- PASS 5 prints the live one, and a
number copied into a docstring is a number that goes stale silently.

HORIZONTAL ONLY, and the leading zero is the policy, not a formatting choice. The
cutout is on a SIDE: measured on a Pixel 5a 2026-08-30 in landscape, the display
cutout insets are Rect(0, 0 - 136, 0) -- right 136, top 0, bottom 0. Spending the
clearance on all four sides cost 104dp of a 411dp panel for a hazard that is not
on that axis. A screen that wants a vertical gutter authors one; PASS 6 still
reasons about a single shorthand because the zero is written into it.

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


WHY THE ROOTS NEED A PASS OF THEIR OWN. Everything above starts from PASS 1, and
PASS 1 only sees `position: absolute`. A screen whose edge chrome sits in ORDINARY
FLOW -- a header row inside a padded root -- produces no risk surface at all, so
every later pass agrees there is nothing to classify and the script prints PASS.
That is not a corner case: `.character-roster` was `padding: var(--space-6)` with
"Main Menu" and "View Collection" in a flow header, which put both 24dp from the
screen edge, inside the 52dp cutout, from #278 until 2026-08-30 -- with this audit
green over it the whole time. PASS 7 asks the one question the other six cannot:
for each screen ROOT, where does its horizontal clearance live?

SHAPE: seven passes, each consuming the one before it
-----------------------------------------------------
  PASS 1  USS   -> the RISK SURFACE   every rule that anchors to an edge
  PASS 2  UXML  -> the STRUCTURE      where each class is used, and how deep
  PASS 3  C#    -> the CODE-APPLIED   classes attached at runtime
  PASS 4  join  -> the VERDICT        1 classified by 2 and 3
  PASS 5  2+USS -> the POLICY         --edge-safe locality, .emb-edge placement
  PASS 6  2+USS -> the SPECIFICITY    rules that would outrank the theme classes
  PASS 7  2+USS -> the ROOTS          every screen root clears the edge, or names
                                      the element that does it instead

Each pass prints what it found and hands a value to the next, so a failure names
the stage that produced it and no stage's output has to be taken on trust. Run it
with no arguments; exit 1 on any finding.
"""

import io
import os
import re
import sys
import glob
import xml.etree.ElementTree as ET

UI = 'unity/Assets/UI'
SCRIPTS = 'unity/Assets/Scripts'
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
    'match-bar':
        'transparent bottom rail bounded by the two safe edges; centering its button within '
        'that rail preserves the screen center without shifting it by an extra margin',
    'fr-challenge-strip':
        'transparent, picking-ignored strip bounded by the two safe edges so the challenge card '
        'centres on the screen; only the card inside it takes clicks',
    'deck-drawer__manage':
        'its left edge is internal to the drawer; its right edge clears the screen token '
        'plus a 48px reserve under the neighboring Settings control',
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
        'a LEFT drawer since 2026-08-31 (it slid down from the top until then), so its '
        'left IS a screen edge and takes the token while its right is interior and '
        'takes the ordinary gutter; the Pixel 5a insets measured over USB are '
        '[136,0][0,63] / [0,0][136,63], so left/right flip and the TOP is 0 in both '
        'landscape rotations, which is the only orientation this game has, and the 63 '
        'at the bottom is the hidden gesture bar',
    'class-tab-bar':
        'the nine class tabs are the only INTERACTIVE elements that reach the screen\'s '
        'short edges. It is a flow child of the root, so it spends the token as a MARGIN '
        'on top of the root\'s own --space-6 -- deliberately over-paying the 52dp cutout, '
        'because unlike the wall\'s forged frame (decoration that bleeds on purpose) a tab '
        'under the hole-punch is a dead touch target',
    'emb-back-button':
        'the one canonical Back (theme.uss, 2026-09-27), applied ALONE by design so no screen '
        'restyles it. Its `margin: 0 var(--edge-safe)` is exactly .emb-edge\'s declaration, kept '
        'inside the class so the button needs no second class to clear the cutout -- and it '
        'lives in theme.uss, so PASS 5 still sees the token spent in one file',
}

# Screen ROOTS that deliberately run edge to edge, keyed by the root's own screen
# class. A root not carrying .emb-bleed and not spending --edge-safe horizontally
# itself has to appear here, and an entry is not free: `carriers` names the
# elements that hold the clearance INSTEAD, and PASS 7 checks each one still
# carries .emb-edge / .emb-bleed or is a declared TOKEN_EXCEPTIONS spender. Strip
# the class off `.mm-footer` and the main menu's exception stops being true and
# fails here -- which is the point. A reason with nothing checkable under it is
# how `.character-roster` stayed 24dp from the cutout with this audit green.
ROOT_EXCEPTIONS = {
    'main-menu': (
        'a full-bleed backdrop with no flow content at its edges; the rule says so itself '
        '("Edge clearance is NOT this padding\'s job"). Every remaining edge-anchored '
        'element is absolute and tagged',
        ['mm-command', 'mm-top-rail', 'mm-footer']),
    'collection': (
        'the card wall is the background layer and bleeds by ruling (the wall is headerless, '
        '2026-08-18); the controls are an absolute overlay that spends the token itself, and '
        'the class rail -- the one piece of FLOW chrome at this root since 2026-08-31 -- '
        'spends it as a margin',
        ['col-query-toolbar', 'col-query', 'craft-modal', 'class-tab-bar']),
    'pack-vault-screen': (
        'the rack, stage and rail live in the padded vault shell; Back is the canonical '
        '.emb-back-button, which spends the token itself, and the reveal overlay owns its own '
        'clearance',
        ['vault-shell', 'emb-back-button', 'reveal-overlay']),
    'shop-screen': (
        'the root holds only a backdrop; all store content is inside the overlay. This is the '
        'screen #shop-overlay\'s id selector kept flush to the edge until PASS 6 caught it',
        ['shop-overlay']),
    'ms-root': (
        'a scrim host with no flow content of its own; the gear and the panel are absolute',
        ['ms-gear']),
    'fr-root': (
        'the root of the friends overlay, laid over whichever screen is up; it has no flow '
        'content of its own, and everything in it that reaches a screen edge is absolute',
        ['fr-button', 'fr-challenge-strip']),
}

ELEMENT_INTERNAL = {
    'tear-half':
        'non-interactive torn wrapper pieces parented to the tapped pack face by '
        'PackTearAnimator.Play; left/top are pack-local coordinates',
    'vault-count':
        'count bubble parented to a pack miniature by PackVaultController.BuildMini and '
        'PackRackDrag.CreateGhost; its negative offsets hang off the miniature\'s corner, not the screen',
    'vault-ghost':
        'the dragged pack, added to the screen root by PackRackDrag.CreateGhost with '
        'picking-mode Ignore and placed under the pointer from script each move; it goes where '
        'the finger is, so an edge inset would put it somewhere the finger is not',
    'emb-scroll-fade':
        'the "there is more" fade over the bottom band of a scroll surface; its left/right/bottom '
        'offsets are against the FRAME it is pinned inside (see .deck-chooser-frame in '
        'collection.uss), never the screen, and it carries picking-mode="Ignore". It lives in '
        'theme.uss because the ruling that creates it -- no visible scrollbars -- is game-wide, '
        'not because the element is screen chrome',
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
    'craft-illustration-scrim':
        'the scrim over the craft modal\'s backdrop illustration; '
        'CollectionScreen.uxml:121 parents it to #craft-modal, which already spends '
        '--edge-safe on all four sides (see TOKEN_EXCEPTIONS above). Clearing the edge '
        'twice would inset it from an inset',
}

# These controls inherit their coordinate origin from a protected parent. Each
# declared chain is checked in the XML; moving a control or untagging its carrier
# must invalidate the declaration rather than silently suppress an edge finding.
PARENT_CLEARANCE = {
    ('mm-vault-dock', 'MainMenu/MainMenuScreen.uxml'):
        (('mm-command',), EDGE_CLASS),
    ('mm-vault-count', 'MainMenu/MainMenuScreen.uxml'):
        (('mm-vault-dock', 'mm-command'), EDGE_CLASS),
}


def _has_parent_clearance(path, cls):
    relative = os.path.relpath(path, UI).replace(os.sep, '/')
    declaration = PARENT_CLEARANCE.get((cls, relative))
    if not declaration:
        return False
    chain, clearance_class = declaration
    root = ET.parse(path).getroot()
    parents = {child: parent for parent in root.iter() for child in parent}
    nodes = [node for node in root.iter() if cls in node.get('class', '').split()]
    if not nodes:
        return False
    for node in nodes:
        for expected in chain:
            node = parents.get(node)
            if node is None or expected not in node.get('class', '').split():
                return False
        if clearance_class not in node.get('class', '').split():
            return False
    return True


def _read(p):
    with io.open(p, encoding='utf-8') as source:
        return source.read()


def _files(root, ext):
    return sorted(p.replace(os.sep, '/') for p in
                  glob.glob('%s/**/*.%s' % (root, ext), recursive=True))


def _short(p, marker):
    return p.split(marker)[-1]


def _rule_body(path, cls):
    m = re.search(r'^\.%s\s*\{([^}]*)\}' % re.escape(cls), _read(path), re.M)
    return m.group(1) if m else ''


def _owning_class(text, lineno):
    """The class whose rule block contains 1-indexed `lineno`, or None.

    The owner is the selector's SUBJECT -- its last compound -- because that is the element
    the declarations land on. friends.uss scopes every rule under `.fr-root`, and reading the
    first class instead charged the whole stylesheet's spending to the root.
    """
    for m in re.finditer(r'(^[.#][^{]*)\{([^}]*)\}', text, re.M):
        first = text[:m.start()].count('\n') + 1
        last = text[:m.end()].count('\n') + 1
        if first <= lineno <= last:
            compounds = re.split(r'[\s>+~]+', m.group(1).split(',')[0].strip())
            named = [c for c in compounds if c[:1] in '.#'] or compounds[:1]
            return named[-1].lstrip('.#').split(':')[0].split('.')[0]
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
            # (?<![-\w]) and not \b. A leading \b matches INSIDE `padding-top:`,
            # `margin-left:` and `border-top-width:`, because `-` ends a word, so
            # any of those read as an EDGE OFFSET. That promoted .mm-modal -- a
            # full-bleed backdrop whose only new declaration was a padding
            # longhand -- into PASS 4's chrome verdict, which then demanded
            # .emb-edge on two centred modal panels (2026-08-30). Only the four
            # BARE offset properties anchor anything to an edge.
            offs = dict(re.findall(r'(?<![-\w])(top|right|bottom|left)\s*:\s*([^;]+);', body))
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

    EVERY class="..." ON THE LINE, not the first. This was re.search until
    2026-08-30, and a UXML line carrying two elements -- a parent and a child
    written inline, which is how the nine class tabs were authored -- hid the
    SECOND one completely. PASS 4 then reported .class-tab-frame as "unused - no
    UXML or C# applies it" while the UXML applied it nine times, which is a
    verdict that reads like permission to delete a live class. Nothing about the
    output said the class had been dropped rather than examined.

    `indent` is per LINE, so co-authored elements share the parent's depth. That
    is an approximation, and it is the right one: PASS 4 uses indent to ask
    whether a rule sits at screen level or inside a panel, and an element written
    inline with its parent is by construction inside it.
    """
    _head(2, 'STRUCTURE - class usage and depth in %s/**/*.uxml' % UI)
    used, root_indent, sites = {}, {}, 0
    for f in _files(UI, 'uxml'):
        for i, line in enumerate(_read(f).split('\n'), 1):
            indent = len(line) - len(line.lstrip())
            for m in re.finditer(r'class="([^"]*)"', line):
                classes = m.group(1).split()
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
                    if EDGE_CLASS not in s[2] and BLEED_CLASS not in s[2]
                    and not _has_parent_clearance(s[0], cls)]
        if sites and not untagged:
            print('   %-42s %-24s %-9s %s'
                  % (where[:42], sel[:24], 'ok', 'direct or verified parent clearance'))
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


# ── PASS 7 ──────────────────────────────────────────────────────────────────
def pass7_roots(used):
    """Every `.emb-root` screen clears the horizontal edge, or names what does.

    THE HOLE THIS FILLS. PASS 1 is the source of every later pass and it only
    matches `position: absolute`, so a screen with edge chrome in ORDINARY FLOW
    contributes nothing to classify and the audit passes over it in silence.
    That is how `.character-roster` shipped a `padding: var(--space-6)` root with
    "Main Menu" in a flow header: 24dp from a 52dp cutout, green here throughout.

    Three ways a root can be clean, in the order they are checked:

      BLEED    it carries .emb-bleed. The ordinary answer.
      TOKEN    its own rule spends --edge-safe on padding-left/right or
               margin-left/right (or a shorthand whose horizontal slot is the
               token). For a root that needs a different vertical value.
      NAMED    it appears in ROOT_EXCEPTIONS, and every carrier that entry names
               still holds .emb-edge / .emb-bleed or is a TOKEN_EXCEPTIONS
               spender. The reason is prose; the carriers are the check.

    A shorthand is read POSITIONALLY -- `padding: a b` and `padding: a b c` put
    the horizontal in slot 2, `padding: a b c d` in slots 2 and 4, `padding: a` in
    all four. `.character-roster` deliberately writes its vertical as longhands so
    no shorthand here can silently re-set what .emb-bleed supplies; this pass
    would catch it if one did.
    """
    _head(7, 'ROOTS - where each screen root\'s horizontal clearance lives')
    findings = []

    tokened = {c for c in TOKEN_EXCEPTIONS}

    roots = {}
    for f, line, classes, _indent in used.get(ROOT_CLASS, []):
        screen = [c for c in classes
                  if c not in (ROOT_CLASS, EDGE_CLASS, BLEED_CLASS)
                  and not c.endswith('--hidden')]
        roots[(f, line)] = (screen[0] if screen else None, classes)

    if not roots:
        print('   (no .%s found -- PASS 2 saw no screens)' % ROOT_CLASS)
        return findings

    for (f, line), (screen, classes) in sorted(roots.items()):
        label = '%s:%d' % (_short(f, 'Assets/UI/'), line)
        if BLEED_CLASS in classes:
            print('   %-52s bleed .%s' % (label[:52], BLEED_CLASS))
            continue
        if screen is None:
            findings.append('%s  a .%s element carries no screen class, so PASS 7 cannot '
                            'name it.\n      Give the root its screen class.'
                            % (label, ROOT_CLASS))
            print('   %-52s FAIL  no screen class' % label[:52])
            continue

        body = ''
        for uss in _files(UI, 'uss'):
            body = _rule_body(uss, screen)
            if body:
                break
        bare = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
        horiz = False
        for m in re.finditer(r'(?:^|;)\s*(padding|margin)(-left|-right)?\s*:([^;]*)', bare):
            side, val = m.group(2), m.group(3).strip()
            if side:
                horiz = horiz or '--edge-safe' in val
                continue
            parts = val.split()
            slots = {1: [0], 2: [1], 3: [1], 4: [1, 3]}.get(len(parts), [])
            horiz = horiz or any('--edge-safe' in parts[i] for i in slots)
        if horiz:
            print('   %-52s token .%s spends --edge-safe horizontally' % (label[:52], screen))
            continue

        if screen not in ROOT_EXCEPTIONS:
            findings.append(
                '%s  `.%s` is a screen root with neither .%s nor --edge-safe of its own,\n'
                '      so anything in its FLOW sits at whatever its padding happens to be -- the\n'
                '      cutout is %s deep and no other pass looks at flow content. Add .%s, spend\n'
                '      the token here, or declare it in ROOT_EXCEPTIONS naming what clears the\n'
                '      edge instead.'
                % (label, screen, BLEED_CLASS, _edge_safe_value() or '52px', BLEED_CLASS))
            print('   %-52s FAIL  .%s clears nothing' % (label[:52], screen))
            continue

        reason, carriers = ROOT_EXCEPTIONS[screen]
        carried = {c for c in used if any(
            sf == f and t in cl for sf, _l, cl, _i in used[c]
            for t in (EDGE_CLASS, BLEED_CLASS))}
        dead = [c for c in carriers if c not in carried and c not in tokened]
        if dead:
            findings.append(
                '%s  `.%s` is excused in ROOT_EXCEPTIONS because %s carr%s the clearance\n'
                '      instead -- and %s no longer do%s. The exception is now the only thing\n'
                '      holding this screen off the cutout, and it is prose. Re-tag the element or\n'
                '      retire the entry.'
                % (label, screen, ', '.join('.' + c for c in carriers),
                   'y' if len(carriers) == 1 else 'y',
                   ', '.join('.' + c for c in dead),
                   'es' if len(dead) == 1 else ''))
            print('   %-52s FAIL  carrier(s) %s untagged'
                  % (label[:52], ', '.join(dead)[:24]))
            continue
        print('   %-52s named %s' % (label[:52], reason[:44]))
    print('   => %d screen root(s); %d declared in ROOT_EXCEPTIONS'
          % (len(roots), len(ROOT_EXCEPTIONS)))
    return findings


def _edge_safe_value():
    m = re.search(r'--edge-safe\s*:\s*([^;]+);', _read(THEME))
    return m.group(1).strip() if m else None


def main():
    chrome, bleed = pass1_risk_surface()
    used, root_indent = pass2_structure()
    code = pass3_code_applied()
    findings = pass4_verdict(chrome, used, code)
    findings += pass5_policy(used, root_indent)
    findings += pass6_specificity(used)
    findings += pass7_roots(used)

    print('\n' + '=' * 104)
    print('%d edge-anchored chrome rule(s) | %d full-bleed layer(s) | '
          '%d element-internal | %d token exception(s) | %d root exception(s)'
          % (len(chrome), len(bleed), len(ELEMENT_INTERNAL),
             len(TOKEN_EXCEPTIONS), len(ROOT_EXCEPTIONS)))
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
