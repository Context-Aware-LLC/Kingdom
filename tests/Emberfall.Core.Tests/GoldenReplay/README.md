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
| `toll_cascade` | Death-resolution batch ordering (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7`): both players trade identical 2/2 TOLL minions, so two `ON_DEATH` effects fire in active-player-first board order. Baked by `CascadeFixtureGenerator` ([Explicit]). |
| `turn_end_toll` | `ON_TURN_END` (EF-012): that it resolves at all, that it fires on **both** players' turn ends rather than only its controller's, and that the observer sweep order flips with the active seat. Baked by `TurnEndTollFixtureGenerator` ([Explicit]). |
| `on_damaged_thorns` | `ON_DAMAGED` (EF-014): that a damaged minion's self-trigger resolves, and that an **Aegis-absorbed** hit does not raise it. Baked by `OnDamagedThornsFixtureGenerator` ([Explicit]). |
| `spell_cast_echo` | `ON_SPELL_CAST` (EF-016): that it resolves at all, that it fires on **both** players' casts, that the sweep runs active-side-first, that the spell's own `ON_CAST` resolves before the reactions — and, the reason the fixture exists, that a **hero power does not fire it** even though hero powers raise the same `ON_CAST`. Baked by `SpellCastEchoFixtureGenerator` ([Explicit]). |
| `weapon_identity` | Unified weapon identity: original hand card → `Zone.Weapon` → graveyard, populated canonical weapon state, Lifedrain/Venom/Frost rider behavior and order, Aegis suppression, and current `DOUBLESTRIKE` carrier state. Baked by `WeaponIdentityFixtureGenerator` ([Explicit]). |
| `unearth_choice` | Deterministic Unearth offers, correlated exact-set choice resolution, monotonic choice sequence, created-card events, and `AutoConfirm` selecting the first offered option. Baked by `UnearthChoiceFixtureGenerator` ([Explicit]). |
| `ongoing_effect_lifecycle` | Carrierless ongoing-effect creation, deterministic trigger order, one-shot expiration, and a permanent instance retained in final canonical state. Baked by `OngoingEffectFixtureGenerator` ([Explicit]). |

Fixtures whose scenario is scripted rather than hand-written have a paired `[Explicit]` generator in
this directory — never run by CI. Run one locally to re-produce and re-bake its fixture, then commit
the resulting `*.replay.json` + `*.rebaseline.md` pair.

## The fixture-only cards

`M1.cards.snapshot.json` carries `test_turn_end_toller` (EF-012), `test_thornback` +
`test_aegis_thornback` (EF-014), and `test_spell_cast_echo` + `test_spell_cast_probe` (EF-016), which
exist **only** to give those three fixtures the subscribers they need — an `ON_TURN_END` toller, an
`ON_DAMAGED` pair differing solely by `AEGIS`, and an `ON_SPELL_CAST` echo with a cheap spell
to trigger it. All are
`collectible: false` and deliberately absent from the shipping `cards/**` tree. Appending a definition
to the snapshot is inert for the other fixtures (decks are explicit id lists; the hasher walks game
state, never the card db), but that is a claim the unrebaselined run is what actually proves. **If you
add another fixture-only card, append it — never edit an existing row**, and re-run the full gate
unrebaselined before committing.

`test_rider_blade`, `test_rider_brute`, and `test_rider_aegis` are the fixture-only weapon-identity
trio. They are the first GoldenReplay rows to exercise a populated weapon slot.

`test_unearth_source` is the non-collectible fixture-only source for `unearth_choice`. Keeping it
out of `AllCollectible` makes the append inert for the offer pool used by the new fixture and for
every pre-existing replay.

`test_ongoing_permanent` and `test_ongoing_one_shot` are the non-collectible fixture-only spell
sources for `ongoing_effect_lifecycle`. They differ in lifetime and damage amount so the replay can
assert creation order, both trigger events, one expiration, and the permanent survivor.

## Rebaselining (never automatic)

CI runs **assert-only**. To deliberately re-bake a fixture after an intended rule change:

```
EMBERFALL_REBASELINE=<fixtureName>   dotnet test --filter Category=GoldenReplay
# or "all" to re-bake every fixture
```

This rewrites the one named `expected` block and drops/keeps the paired `<name>.rebaseline.md`, which the
committer **must** fill in. CI's stage-1 rebaseline-guard fails any PR that changes an `expected` block
without touching its paired `.rebaseline.md` (`dev-docs/quality/testing-and-ci.md:31`).
