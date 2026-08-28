# Handoff — cardweb: admin tab + editor comments

**Written 2026-08-27.** Continuation brief for the web card authoring tool.
Read `tooling/cardweb/README.md` first — it explains *why* the tool stages instead of saves, and
that reasoning constrains both tasks below.

## Where things stand

Live at **https://cards.outofcontext.to** (Basic auth, user `emberfall`; the password is a Fly
secret on app `emberfall-cardweb` and is **write-only — it cannot be read back**).

Landed: `830f9fa8` (the tool), `b00e0bfd` (the deploy).

```
tooling/cardweb/       server.py · corpus.py · canon.py · build_thumbs.py · static/
tooling/scripts/       reconcile_card_staging.py
deploy/cardweb/        Dockerfile · fly.toml
```

Three invariants the next session must not break:

1. **The server never writes a set file.** `CardAuthoringWindow.SaveAll` writes each file whole
   from memory, so two live editors cannot share the corpus. Everything goes through
   `staging/pending.json` and `reconcile_card_staging.py`.
2. **The browser never receives `id`, `handle` or `artId`.** It holds an opaque per-run token;
   art is served as `/thumb/<token>.webp`. Keeps `NextIdInBand` the sole minting authority.
3. **`canon.py` must keep passing its selftest.** `python tooling/cardweb/canon.py` → 293/293
   byte-identical. Reconcile refuses to write if it fails.

---

## Task 1 — fold the card-admin panel in as a tab

`tooling/card-admin/` is a static analytics panel: corpus-wide and per-class rollups, type split,
rarity distribution, keyword and selector frequency, cost and stat curves. It runs off `file://`
with no server.

**Shape:** `index.html` + `styles.css` + `admin-panel.js` (18 KB), fed by `window.CARD_DATA` from
a generated `data.js`. `build_data.py` produces `data.json` (canonical) and `data.js` (the mirror,
because browsers block `fetch()` under `file://`).

**The `file://` workaround stops being necessary the moment this is served over HTTP.** Inside
cardweb, `admin-panel.js` can `fetch('/api/admin-data')` and `data.js` becomes dead weight for
this path. Do not delete it — `build_data.py` is wired into `rebake_card_data.py` and the panel
still has a standalone `file://` mode worth keeping.

**Suggested approach**

- Add a tab strip to `static/index.html`: **Cards** (the existing grid) / **Corpus** (the panel).
- Serve the rollup from a new `GET /api/admin-data` that runs `build_data.py`'s aggregation over
  the already-loaded `Corpus` rather than reading `data.json` off disk — otherwise the tab shows a
  stale rollup whenever the corpus has moved and nobody re-ran the rebake. That means importing
  `rollup()`/`normalize()` from `build_data.py`, the same way `corpus.py` imports `art_file()`.
- `styles.css` and `app.css` will collide (both style bare `header`, `h1`, `select`). Scope the
  admin panel's rules under a wrapper class before merging, or keep it in an `<iframe>` — the
  iframe is uglier but is genuinely less work and cannot leak styles either direction.

**Already fixed for you (2026-08-27):** `build_data.py` listed two set files by hand and had never
been given `CORE`, so the panel reported **284** cards against a 293-row corpus. It now globs.
Re-run `python tooling/card-admin/build_data.py` after any card-data change, or let
`rebake_card_data.py --write` do it.

---

## Task 2 — editor comments, keyed by card id

**Sam's ruling (2026-08-27):** the comment does not have to reach `cards.json`. It only has to be
tied to the card's id so the authoring tools can show it in the right place. Visible to the web
tool and the Unity authoring window; invisible to the game.

### Do NOT use the `_comment` field. It is a trap.

`_comment` is in `CardJson.CardProperties`, so a row carrying it parses fine. Two things make it
the wrong home anyway — both verified 2026-08-27:

**It already means something else.** It is the annotation convention for TEST FIXTURE rows —
12 occurrences in `tests/Emberfall.Core.Tests/Fixtures/CardSets/M1.cards.snapshot.json`, each
explaining why that fixture card exists. Reusing it for editor commentary would overload a
convention that is load-bearing somewhere else.

**It does not survive a round-trip.** Outside the allowlist entry and those fixtures it appears
nowhere: it is never read into `CardDefinition`, and `CardJsonRowIO.Serialize` never writes it
back. A comment stored there survives exactly until the next Unity `SaveAll`, which rebuilds
every row from `CardDefinition` and **silently drops it**. Zero rows of the *shipping* corpus use
it — fixtures get away with it precisely because they are hand-written and never round-tripped
through the authoring window.

So it looks like the obvious home for this, and it is the worst one.

(Related: `canon.py`'s `serialize_row` raises on any field not in `CANONICAL_ORDER`, so a row
carrying `_comment` would currently stop the reconcile step rather than corrupt anything. That is
the desired failure, but it means putting comments on rows breaks reconcile until both are
updated.)

### Use a sidecar

```
tooling/cardweb/comments/comments.json     # or one file per set, if it grows
{ "151": [ { "author": "sam", "at": "...", "text": "..." } ] }
```

Keyed on the **numeric id** — the only permanent handle (`[[card-ids-are-permanent-and-format-axis]]`).
Not the handle: nothing may resolve against it.

Why a sidecar rather than the corpus, beyond the `_comment` trap:

- A new card field means touching `CardJson`, `CardDefinition`, `CardJsonRowIO.Serialize`,
  `canon.py`, and every one of the **11** call sites that parse rows
  (`git grep -l "CardJson.FromElement\|CardJson.Deserialize" -- src unity tests tooling`).
- Card rows ship to the client. Editor commentary is not player-facing content and should not be
  in a shipped bundle.
- A sidecar cannot move the canonical hash, so it stays clear of the state-shape hazard in
  `CLAUDE.md`.

**Server work:** `GET /api/comments` (all, resolved to this run's tokens the way `staging_view()`
already does for staged edits — copy that pattern, it exists precisely because tokens are salted
per run and the file is keyed on id), `POST /api/comments` `{token, text}`, and probably
`DELETE`/resolve. Reuse `_lock`; comments are a second writer to a file on the same volume.

**Deploy:** put it under `/data` next to `staging/` via an env override — comments must survive
machine suspension for the same reason staged edits do. `EMBERFALL_COMMENTS_DIR`, matching the
existing `EMBERFALL_STAGING_DIR`.

**UI:** a comments block in the editor drawer under the read-only rules, and a marker on the grid
card the way `.staged` already works.

**Unity side (the half that makes it worth doing):** `CardAuthoringWindow` needs to read the same
sidecar and show it. It is a plain JSON file next to the corpus — no engine change, no schema
change. Until that lands, comments are visible only in the browser, which is still useful but is
half the feature Sam asked for.

---

## Watch out for

- **`.dockerignore` is an allowlist opening with `*`.** Anything new the image needs must be
  re-included by name or `COPY` produces an empty directory **with no error**. This already bit
  once (`gen_card_prompt_briefs.py`).
- **The thumbnail cache is gitignored.** `python tooling/cardweb/build_thumbs.py` before the first
  deploy from a fresh clone, or every card renders art-less.
- **`/api/validate` shells to `dotnet`**, which is not in the container. Local-only; it returns an
  honest error in production rather than failing.
- **This working copy hosts more than one live session.** Stage and commit by explicit path
  (`git commit --only <paths>`), and check `git branch --show-current` first — the branch moved
  underneath twice while the tool was being built.
