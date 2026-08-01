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

## Current fixtures (M1 foundation)

| Fixture | Covers |
|---|---|
| `opening_pyra_maren` | The RNG-dense opening: coin flip → shuffle → 3/4 deal → `token_spark_coin` insertion → mulligan → first start-of-turn draw. |
| `full_match_pyra_wins` | A complete match to a win condition (turn-loop spine → concede → `Player0Win`). |

## Tracked gap — `deathrattle_cascade`

`hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7` requires a third fixture, `deathrattle_cascade`, covering death-resolution batch
ordering. It depends on the **effect interpreter + combat** (`DEAL_DAMAGE`, `SUMMON`, Deathrattle), which
is **M1 Phase B** (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M1-KICKOFF.md §3 Phase B`) — not part of this determinism-foundation drop (steps 1–5:
firewall, RNG, canonical serialization, golden-replay harness, CI). The harness, hasher, and CI gate are
in place now; this fixture lands **with** the interpreter under the standing rule **"no new effect without
a golden."** Until then the gate is green on the opening + full-match fixtures, and the harness is ready to
accept the third the moment combat exists.

## Rebaselining (never automatic)

CI runs **assert-only**. To deliberately re-bake a fixture after an intended rule change:

```
EMBERFALL_REBASELINE=<fixtureName>   dotnet test --filter Category=GoldenReplay
# or "all" to re-bake every fixture
```

This rewrites the one named `expected` block and drops/keeps the paired `<name>.rebaseline.md`, which the
committer **must** fill in. CI's stage-1 rebaseline-guard fails any PR that changes an `expected` block
without touching its paired `.rebaseline.md` (`dev-docs/quality/testing-and-ci.md:31`).
