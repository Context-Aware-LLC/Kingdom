# Golden-Replay Fixtures — the R1 gate

This directory is the **R1 determinism gate** (`dev-docs/quality/testing-and-ci.md:24`, M1-KICKOFF §4 criterion 6).
A fixture is a checked-in `(seed, intent-log)` plus the expected `(finalStateHash, GameEventStream)`.
Replaying it against a **fresh** core must reproduce both **exactly** — any divergence fails the build.

## Layout

```
fixtures/
  <name>.replay.json        one ReplayFixture (seed, decks, intents, expected block)
  <name>.rebaseline.md      the §5.2 justification, paired 1:1 with the fixture
cardsets/
  M1.cards.snapshot.json    pinned card data the fixtures replay against (§3.3)
```

## Current fixtures

| Fixture | Covers |
|---|---|
| `opening_pyra_maren` | The RNG-dense opening: coin flip → shuffle → 3/4 deal → `token_spark_coin` insertion → mulligan → first start-of-turn draw. |
| `full_match_pyra_wins` | A complete match to a win condition (turn-loop spine → concede → `Player0Win`). |
| `deathrattle_cascade` | Death-resolution batch ordering (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7`): both players trade identical 2/2 DEATHRATTLE minions, so two `ON_DEATH` effects fire in active-player-first board order. Baked by `CascadeFixtureGenerator` ([Explicit]). |
| `turn_end_toll` | `ON_TURN_END` (EF-012): that it resolves at all, that it fires on **both** players' turn ends rather than only its controller's, and that the observer sweep order flips with the active seat. Baked by `TurnEndTollFixtureGenerator` ([Explicit]). |

Fixtures whose scenario is scripted rather than hand-written have a paired `[Explicit]` generator in
this directory — never run by CI. Run one locally to re-produce and re-bake its fixture, then commit
the resulting `*.replay.json` + `*.rebaseline.md` pair.

## The one fixture-only card

`M1.cards.snapshot.json` carries `test_turn_end_toller`, which exists **only** to give `turn_end_toll`
an `ON_TURN_END` subscriber — it is `collectible: false` and deliberately absent from the shipping
`cards/**` tree. Appending a definition to the snapshot is inert for the other fixtures (decks are
explicit id lists; the hasher walks game state, never the card db), but that is a claim the
unrebaselined run is what actually proves. **If you add another fixture-only card, append it — never
edit an existing row**, and re-run the full gate unrebaselined before committing.

## Rebaselining (never automatic)

CI runs **assert-only**. To deliberately re-bake a fixture after an intended rule change:

```
EMBERFALL_REBASELINE=<fixtureName>   dotnet test --filter Category=GoldenReplay
# or "all" to re-bake every fixture
```

This rewrites the one named `expected` block and drops/keeps the paired `<name>.rebaseline.md`, which the
committer **must** fill in. CI's stage-1 rebaseline-guard fails any PR that changes an `expected` block
without touching its paired `.rebaseline.md` (`dev-docs/quality/testing-and-ci.md:31`).
