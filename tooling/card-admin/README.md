# Card Corpus Admin Panel

An offline browser panel for exploring the Emberfall card corpus, plus the script that feeds it.

## Files

- `build_data.py` — parses `unity/Emberfall/Assets/CardData/{M1,M2,M3}.cards.json` and writes:
  - `data.json` — canonical machine-readable aggregate (per-card records + corpus-wide and
    per-class rollups).
  - `data.js` — a thin `window.CARD_DATA = …` mirror the panel loads (browsers block
    `fetch('data.json')` under `file://`, so the panel reads the `.js` mirror instead).
- `index.html` — self-contained static panel. No server, no CDN; opens straight from disk.

## Regenerate

Any time the card data changes, re-run from the repo root:

```
python tooling/card-admin/build_data.py
```

The printed summary (totals, type split, per-class counts) is the run's deliverable — read it.

## Use

Open `tooling/card-admin/index.html` in any browser. It opens on a corpus-wide overview across
all classes; pick a class to drill down, then narrow by cost range, card type, rarity, and/or
keyword. Every view shows the type split, keyword frequency, targeting-selector frequency, cost
distribution, and (for minions) the attack/health distribution within the active filter.
