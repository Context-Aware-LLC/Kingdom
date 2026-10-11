#!/usr/bin/env python3
"""Validate every deck fixture in the balance corpus against DeckValidator rules L1-L4.

Mirrors src/Emberfall.Core.Collection/DeckValidator.cs so a hand-authored corpus deck can be
checked without a Unity seat or a dotnet build. It is a *pre-flight* check, not a substitute for
the engine's own validator: if the two ever disagree, DeckValidator.cs wins.

    python tooling/balance-corpus/validate_decks.py

Exit code 0 = every deck passes. Non-zero = at least one rule failed; the failures print above
the summary. Quote this script's stdout rather than describing it (see CLAUDE.md).
"""
import json
import os
import sys
from collections import Counter

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CARD_DATA = os.path.join(REPO, 'unity', 'Assets', 'CardData')
CORPUS = os.path.join(REPO, 'tests', 'Emberfall.Core.Tests', 'Fixtures', 'BalanceCorpus')

DECK_SIZE = 30
COPY_CAP = 2
LEGENDARY_CAP = 1


def load_cards():
    cards = {}
    for name in ('Emberfall.cards.json', 'Rekindling.cards.json'):
        with open(os.path.join(CARD_DATA, name), encoding='utf-8') as fh:
            for card in json.load(fh):
                cards[card['id']] = card
    return cards


def validate(path, cards):
    with open(path, encoding='utf-8') as fh:
        deck = json.load(fh)

    allowed = {'NEUTRAL', deck['primaryClass'], deck['secondaryClass']}
    entries = deck['entries']
    failures = []

    total = sum(e['count'] for e in entries)
    if total != DECK_SIZE:
        failures.append('L1 size: %d cards, expected %d' % (total, DECK_SIZE))

    seen = Counter(e['id'] for e in entries)
    for card_id, times in seen.items():
        if times > 1:
            failures.append('duplicate entry: %s appears %d times' % (card_id, times))

    curve = Counter()
    for entry in entries:
        card_id, count = entry['id'], entry['count']
        card = cards.get(card_id)
        if card is None:
            failures.append('unknown card id: %s' % card_id)
            continue
        cap = LEGENDARY_CAP if card['rarity'] == 'LEGENDARY' else COPY_CAP
        rule = 'L3' if card['rarity'] == 'LEGENDARY' else 'L2'
        if count > cap:
            failures.append('%s copy cap: %s (%s) x%d exceeds %d'
                            % (rule, card_id, card['rarity'], count, cap))
        if card['class'] not in allowed:
            failures.append('L4 class: %s is %s, not in %s'
                            % (card_id, card['class'], sorted(allowed)))
        if not card.get('collectible'):
            failures.append('not collectible: %s' % card_id)
        if card.get('lifecycle', 'PUBLISHED') != 'PUBLISHED':
            failures.append('L7 not published (%s): %s' % (card['lifecycle'], card_id))
        curve[card['cost']] += count

    ordered = [(cards[e['id']]['cost'], e['id']) for e in entries if e['id'] in cards]
    if ordered != sorted(ordered):
        failures.append('entries are not sorted by (cost, id)')

    return deck, total, curve, failures


def main():
    cards = load_cards()
    paths = sorted(os.path.join(CORPUS, f) for f in os.listdir(CORPUS)
                   if f.endswith('.deck.json'))
    if not paths:
        print('no deck fixtures found in %s' % CORPUS)
        return 1

    bad = 0
    for path in paths:
        deck, total, curve, failures = validate(path, cards)
        name = os.path.basename(path)
        curve_str = ' '.join('%d:%d' % (c, curve[c]) for c in sorted(curve))
        print('%-34s %s/%s  cards=%d  curve[%s]  %s'
              % (name, deck['primaryClass'], deck['secondaryClass'], total, curve_str,
                 'OK' if not failures else 'FAIL'))
        for failure in failures:
            print('    ! %s' % failure)
            bad += 1

    print('')
    print('%d deck(s) checked, %d rule failure(s)' % (len(paths), bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
