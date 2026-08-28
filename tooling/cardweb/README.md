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

**There is no authentication.** Bind to `127.0.0.1` (the default) unless you have put something in
front of it.
