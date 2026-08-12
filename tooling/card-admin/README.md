# Card Corpus Admin Panel

An offline browser panel for exploring the Emberfall card corpus, plus the script that feeds it.

## Files

- `build_data.py` — parses `unity/Emberfall/Assets/CardData/{Emberfall,Rekindling}.cards.json` and writes:
  - `data.json` — canonical machine-readable aggregate (per-card records + corpus-wide and
    per-class rollups).
  - `data.js` — a thin `window.CARD_DATA = …` mirror the panel loads (browsers block
    `fetch('data.json')` under `file://`, so the panel reads the `.js` mirror instead).
- `index.html` — self-contained static panel. No server, no CDN; opens straight from disk.

## Regenerate

Any time the card data changes, re-run from the repo root:

```
python tooling/card-admin/build_data.py            # regenerate
python tooling/card-admin/build_data.py --check    # report staleness, write nothing, exit 1 if stale
```

The printed summary (totals, type split, per-class counts) is the run's deliverable — read it.
`CARD_ADMIN_REGENERATED=n` / `CARD_ADMIN_STALE_OUTPUTS=n` is the count to quote.

This aggregate is one of three artifacts a card-row edit staleness. To do all three at once, use
`python tooling/scripts/rebake_card_data.py --write` — it calls this script for you.

## Use

Open `tooling/card-admin/index.html` in any browser. It opens on a corpus-wide overview across
all classes; pick a class to drill down, then narrow by cost range, card type, rarity, and/or
keyword. Every view shows the type split, keyword frequency, targeting-selector frequency, cost
distribution, and (for minions) the attack/health distribution within the active filter.
