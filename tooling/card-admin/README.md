# Card Corpus Admin Panel

An offline browser panel for exploring the Emberfall card corpus, plus the script that feeds it.

## Files

- `build_data.py` — parses EVERY `unity/Assets/CardData/*.cards.json` (it globs; CORE
  split out 2026-08-23 and a hand-listed subset had already gone stale once) and writes:
  - `data.json` — canonical machine-readable aggregate (per-card records + corpus-wide and
    per-class rollups).
  - `data.js` — a thin `window.CARD_DATA = …` mirror the panel loads (browsers block
    `fetch('data.json')` under `file://`, so the panel reads the `.js` mirror instead).
- `index.html` + `styles.css` + `admin-panel.js` — the static panel. No server, no CDN, no
  build step; opens straight from disk over `file://`.

## Regenerate

Any time the card data changes, re-run from the repo root:

```
python tooling/card-admin/build_data.py               # regenerate, then bring the live server in line
python tooling/card-admin/build_data.py --no-deploy   # regenerate; report the server's corpus, deploy nothing
python tooling/card-admin/build_data.py --check       # report staleness, write nothing, touch no network
```

The printed summary (totals, type split, per-class counts) is the run's deliverable — read it.
`CARD_ADMIN_REGENERATED=n` / `CARD_ADMIN_STALE_OUTPUTS=n` is the count to quote.

## The live server

A card edit stales a second thing that is not in this repo at all: the corpus **baked into the
server image**. `Dockerfile.server` COPYs the CardData directory in, frozen at `fly deploy`, so an
edited card shows its new face on the client while the server keeps resolving the deployed rule --
no error on either side. Card 1067 did exactly that on 2026-08-28, four days and 34 rules stale.

So a plain run also names the corpus and closes the gap:

```
Local corpus digest:  e710fb7bd6212814
Live corpus digest:   1f0c9a44b1d3e207
Live server is BEHIND the working tree -- cards will resolve by their deployed rules.
SERVER_CORPUS=stale
```

`SERVER_CORPUS=` is the line to quote: `in-sync`, `stale`, `deployed`, `deploy-failed`,
`deploy-unconfirmed`, or `unreachable`. The digest is `CorpusDigest` in
`src/Emberfall.Net.Server/Data/`, reported on `/healthz`, and computed here by `corpus_digest()`.
**The two implementations are mirrored by hand and must move together** --
`CorpusDigestTests.Python_Computes_The_Same_Digest_Over_The_Same_Directory` is what holds them so.

Deploying is not fatal-on-failure only when the server is unreachable; a deploy that was attempted
and did not land exits non-zero. `fly deploy` uploads the WORKING TREE, not `HEAD`, so an
uncommitted change under `src/` rides along -- the run prints what it is about to send.

This aggregate is one of three artifacts a card-row edit leaves stale. To do all three at once, use
`python tooling/scripts/rebake_card_data.py --write` — it calls this script for you.

## Use

Open `tooling/card-admin/index.html` in any browser. It opens on a corpus-wide overview across
all classes; pick a class to drill down, then narrow by cost range, card type, rarity, tribe,
and/or keyword. Every view shows the type split, rarity distribution, keyword frequency,
targeting-selector frequency, cost distribution, and (for minions) the attack/health distribution
within the active filter.

**Rarity distribution** is the one block that does not simply follow the class filter. Its top bar
is the active scope, but the *by class* matrix below it deliberately ignores the class selection —
comparing classes is the point, so picking one highlights its row rather than hiding the other
eight. Every other filter still applies, and the header says so.

The Counts / Share % toggle switches what a row means. **Counts** scales every class against the
largest one, so bar lengths compare absolute size. **Share %** normalises each row to its own
total, so the *mix* compares across classes of different sizes. The number beside each class name
is the denominator in both modes.

Rarity colors are lifted from the game's own `--rarity-*` tokens
(`unity/Assets/UI/Deckbuilder/theme.uss`) so a bar reads as the rarity a player sees on
the frame. RARE-blue and EPIC-purple are near-identical under deuteranopia, so nothing here encodes
rarity by color alone — the order is fixed low-to-high and every segment is named and counted in
the labelled row beside it.
