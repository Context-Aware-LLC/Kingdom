# Web card authoring

A browser front end for the card corpus that can run anywhere — including off this machine —
without Unity, and without any way to clobber a local editor.

```
python tooling/cardweb/build_thumbs.py          # once; 515.8 MB of art -> 4.2 MB of thumbnails
python tooling/cardweb/server.py                # http://127.0.0.1:8781
```

Edits accumulate in `staging/pending.json`. Nothing reaches the corpus until:

```
python tooling/scripts/reconcile_card_staging.py                 # dry run
python tooling/scripts/reconcile_card_staging.py --apply --rebake
```

## Why it stages instead of saving

`CardAuthoringWindow.SaveAll` writes each set file **whole, from what is in memory**. Two editors
therefore cannot share the corpus: the second to save does not merge badly, it silently reverts
everything it did not know about.

So this server has no code path that opens a set file for writing. A remote author's worst case
is a staged edit that reconciliation later reports as conflicted — never a lost row.

Each staged edit carries the row's **base**: what the row looked like when the author opened it.
Reconciliation compares that against what is on disk now and returns one of three verdicts, none
of them silent:

| | |
|---|---|
| `CLEAN` | row unchanged since staging — applies |
| `CONFLICT` | row changed locally — reported with both sides, applied only under `--force-conflicts` |
| `ORPHAN` | row no longer exists — reported, never applied |

Without the base this would be last-write-wins with extra steps.

## The three keys the browser never sees

`id`, `handle` and `artId` are not rendered, not sent, and not accepted back. The client holds an
opaque per-run `token`; art is served as `/thumb/<token>.webp` so not even a URL leaks a key.

That is load-bearing, not cosmetic:

- **`id`** — `NextIdInBand` says outright it "is not a reservation: two authors working at once
  would both be handed the same next number". Keeping the Unity window as the sole minting
  authority means a web client cannot propose an id, so the collision cannot happen.
- **`handle`** — nothing may resolve against it (Sam, 2026-08-15: "the test is that you can change
  a handle and have nothing break").
- **`artId`** — the art pipeline's permanent join key. A changed artId silently strands seeds and
  briefs; `art_key()` in `gen_card_prompt_briefs.py` lists the 18 that were stranded for real.

A consequence worth stating plainly: **the web tool cannot create cards.** A new row needs an id,
a handle and an artId, and all three are minted where the art pipeline can see them.

## The Corpus tab

The second tab is `tooling/card-admin/` — the corpus analytics panel — served in an **iframe**
rather than merged in. Both stylesheets style bare `header`, `h1` and `select`, and both define
`:root` custom properties of the same names with different values; a document boundary costs one
element and cannot leak a rule either direction, and the panel keeps its standalone `file://`
mode unchanged.

The server serves that directory's `index.html`, `styles.css` and `admin-panel.js` **verbatim**
under `/admin/`, so every relative reference in the panel's markup resolves without a rewrite.
There is one copy of that markup and it is the one both modes render — an id renamed there
cannot desynchronise from a duplicate here, because there is no duplicate.

Two things are not verbatim, both deliberate:

- **`/admin/data.js` is generated per request**, not read off disk. The file on disk is
  `build_data.py`'s mirror and is only ever as current as the last rebake, so serving it would
  put a rollup of some earlier corpus in a tab sitting next to the live grid. Nothing about a
  stale number looks stale.
- **`/admin/styles.css` gets a few embed-only lines appended**, hiding the panel's own page
  title under the tab strip that already names it. The `file://` page reads the file off disk
  and never sees them.

`GET /api/admin-data` is the same rollup as JSON. The aggregation is imported from
`build_data.py` (`rollup()`, `normalize()`) rather than reimplemented — two rollups over one
corpus would disagree eventually and the one that disagreed would be the one nobody re-derived.
`id` and `artId` are stripped on the way out, filtered on `IDENTITY_FIELDS` rather than an
allowlist of panel fields, so a field the panel starts reading arrives on its own while a new
identity key is withheld on its own.

## Editor comments

Notes on a card, visible to this tool and to the Unity authoring window, invisible to the game.
Sam's ruling (2026-08-27): the comment does not have to reach `cards.json` — it only has to be
tied to the card's id so the authoring tools can show it in the right place.

They live in `comments/comments.json`, keyed on the **numeric id**, which is the only permanent
handle. `tooling/cardweb/comments.py` is the authority on the format;
`unity/.../Editor/CardComments.cs` is the second reader of it and the two change together.

**`_comment` is not the home for this, and it looks like it is.** It parses on a row — it is in
`CardJson.CardProperties` — but it already means something else (it annotates test *fixture*
rows in `M1.cards.snapshot.json`, 12 of them), and it does not round-trip: nothing reads it into
`CardDefinition` and `CardJsonRowIO.Serialize` never writes it back, so a comment stored there
survives exactly until the next Unity `SaveAll` and is then dropped in silence. A real new field
would mean editing `CardJson`, `CardDefinition`, `Serialize`, `canon.py` and all eleven row-parse
call sites, to ship editor chatter inside a client bundle. A sidecar also cannot move the
canonical hash, so it stays clear of the state-shape hazard in `CLAUDE.md` entirely.

The author is taken from the **authenticated credential**, never from the request body — a byline
anyone can forge is worth nothing, and the byline is the whole point of the field.

### Bringing deployed comments home

Comments accumulate in two places: the Fly volume, where the web tool writes them, and the repo,
which is the copy `CardAuthoringWindow` reads. The container cannot reach the repo, so this is a
**merge**, not a copy — which is why every comment carries a random id:

```
fly ssh sftp get /data/comments/comments.json pulled.json -a emberfall-cardweb
python tooling/cardweb/comments.py merge pulled.json     # union on (card id, comment id)
python tooling/cardweb/comments.py show                  # counts
```

The merge is deliberately **additive**: a comment present locally and absent in the incoming file
is kept. The two files are the same log seen from two machines, and a delete on one is
indistinguishable from a comment the other has not seen yet.

## What it can and cannot edit

Editable: name, type, cost, attack/health/durability, class, rarity, keywords, tribe, text,
flavour, tooltip, set, collectible, factionId.

**Not** editable: `rules` and `soundEffects`, shown read-only. A trigger that parses is not a
trigger that fires and liveness is per source type — a browser form would cheerfully produce
cards that validate and do nothing. Rules stay a Unity job.

## `canon.py`, and why it is the risky part

`CardJsonRowIO.Serialize` is the only thing that writes a card row and it lives in
`Emberfall.Editor`, an Editor-only assembly. `CardJson` in Core is a **reader only**. So nothing
outside Unity can write a card row, and the reconcile step has to reproduce the format exactly —
field order, omit-when-empty, and System.Text.Json's default escaping, which is *not*
`json.dumps`:

```
"  -> "   (not \")        &  -> &        '  -> '
+  -> +                   <  -> <        >  -> >
`  -> `                   \  -> \\            non-ASCII -> \uXXXX, UPPERCASE
```

That table was probed directly against net10.0, not read out of the docs.

**Verify, do not trust.** `python tooling/cardweb/canon.py` re-serializes every row of every set
file and compares bytes; reconcile runs it and refuses to write if it fails. Run it after any
change to `canon.py` **or** to `CardJsonRowIO.Serialize` — a field added to the C# writer and not
to `CANONICAL_ORDER` surfaces there and nowhere else.

> **RETIREMENT TRIGGER:** if `Serialize` ever moves into `Emberfall.Core.Cards` where a `net10.0`
> tool could call it directly, delete `canon.py` and have reconcile shell out to that instead.
> Two implementations of one format is a standing cost, paid here only because there is currently
> no second way to write a row.

## Serving it off this machine

The server reads the corpus and writes only to `staging/`, so a remote deployment needs the three
`*.cards.json` files and the `.thumbs/` tree (4.2 MB) — not the 518 MB of source art, and not a
writable checkout. Pull `staging/pending.json` back to a machine that has the repo and reconcile
there.

If you put it on a hostname: `emberfall.outofcontext.to` was retired 2026-07-30 because it pointed
at Fly's **shared** IPv4 and a neighbouring tenant's open port 3389 got attributed to the domain.
Read `deploy/walking-skeleton/README.md` before reinstating anything under that domain.

### Accounts

HTTP Basic, and with **no** credential configured the server **refuses to bind anything but
loopback** rather than publishing the unreleased corpus — a refusal in `main()`, not a warning in a
README nobody reads at 2am. Two env forms, and they combine:

| | |
|---|---|
| `CARDWEB_USER` / `CARDWEB_PASSWORD` | one pair; the original form, still live |
| `CARDWEB_USERS` | `user:password` entries, comma- or newline-separated |

**Passwords live in the environment and nowhere in this repo** — in production they are a
`fly secrets` value. A credential in a tracked file outlives every rotation of it.

```
fly secrets set CARDWEB_USERS='alice:...,bob:...' -a emberfall-cardweb
```

The username is not decoration: it is the **byline on every comment**, taken from the verified
credential and never from the request body. Give people separate accounts or the comment log
cannot say who wrote what.

Both comparisons in `_credential()` are constant time, and the second one is the one that is easy
to lose: the loop compares **every** configured entry and never breaks early, because a dict lookup
that misses returns before any password check and thereby leaks which usernames exist. `&` rather
than `and` for the same reason — `and` short-circuits.

Live at `https://cards.outofcontext.to`; the secret is **write-only and cannot be read back**, so
rotating means setting a new value, never recovering the old one.

```
fly deploy -c deploy/cardweb/fly.toml .
```

### Four things that will bite a deploy

- **`.dockerignore` is an allowlist opening with `*`.** Anything new the image needs must be
  re-included by name or `COPY` produces an empty directory **with no error**. This has already
  bitten twice (`gen_card_prompt_briefs.py`, then `tooling/card-admin/`).
- **The thumbnail cache is gitignored.** Run `build_thumbs.py` before the first deploy from a
  fresh clone, or every card renders art-less.
- **`/api/validate` shells to `dotnet`**, which is not in the container. Local-only; in
  production it returns an honest error rather than failing.
- **Both writable files must be on the volume.** `EMBERFALL_STAGING_DIR` and
  `EMBERFALL_COMMENTS_DIR` point into `/data`; scale-to-zero makes machine suspension routine, so
  anything on the rootfs is one idle period away from gone.
