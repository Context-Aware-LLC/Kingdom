# Rebaseline: turn_end_toll

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-012 owes under the standing rule
  "no new effect without a golden": `Trigger.ON_TURN_END` was declared in `Enums.cs` and raised
  nowhere, and `CommandProcessor.ApplyEndTurn` now raises it
  (`interp.RaiseObservers(Trigger.ON_TURN_END, excluded: null)`) after the `TurnEnded` VFX emit and
  before the seat flip.
- **Why the new golden is correct:** baked by `TurnEndTollFixtureGenerator` (seed
  `2718281828459045`), whose in-scenario assertions all passed during generation. Both players run
  `test_turn_end_toller` (1/3, `ON_TURN_END → DEAL_DAMAGE 1`, `selector: ENEMY_HERO`). The baked
  event stream is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 3 → 4 | `TurnEnded` (i1), `DamageApplied` i23→i2 amt 1 | first player's toller resolves on its owner's turn end |
  | 8 → 9, 10 | `TurnEnded` (i2), `DamageApplied` i33→i1, then i23→i2 | **both** boards fire on one turn end, **active side first** |
  | 13 → 14, 15 | `TurnEnded` (i1), `DamageApplied` i23→i2, then i33→i1 | the same sweep, order **flipped** with the active seat |

  Rows 2 and 3 are the point of the fixture: they pin the two properties that are otherwise
  invisible — that `ON_TURN_END` is an "end of ANY turn" observer sweep across both boards rather
  than a controller-scoped "end of YOUR turn" (GDD §1b step 3, "fire all `ON_TURN_END` effects"),
  and that the sweep order is `RaiseObservers`' canonical active-player-first walk, so it reverses
  every turn. A controller-scoped reimplementation would drop half these `DamageApplied` rows; a
  fixed-seat ordering would leave rows 2 and 3 identical. Either regression fails this fixture.

  Baked hash: `ec7902a4228e584b5fc8f2ea7ef2668ad8be144e7e26a482099effbfadd150d0` (18 events,
  7 intents). The match is deliberately still `InProgress` at the end of the log — a decided
  outcome would truncate the replay.
- **Link to the design/eng change that caused it:** EF-012 (NOR-52), `LAUNCH-WORKPLAN.md`
  Track E; raise site `src/Emberfall.Core/CommandProcessor.cs` (`ApplyEndTurn`); liveness flipped
  Dead→Live in `CardValidator.TriggerLivenessTable` and in the `Trigger` enum markers
  (`Enums.cs`); headless siblings in `tests/Emberfall.Core.Tests/TurnLoop/TurnEndTriggerTests.cs`.
- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained one card**,
  `test_turn_end_toller`, appended at the end. It is fixture-only (`collectible: false`, not in the
  shipping `cards/**` tree) and exists solely to give this replay an `ON_TURN_END` subscriber; no
  existing row was edited. The append is inert for the other three fixtures — decks are explicit id
  lists and `GameStateHasher` walks game state, never the card db — and the proof is that
  `opening_pyra_maren`, `full_match_pyra_wins` and `toll_cascade` all replay green
  **unrebaselined** with the card present.
- **engineContractVersion changed?** no. `Trigger.ON_TURN_END` already existed in the enum; EF-012
  adds a raise site, not a contract member. Stays `interfaces.v3`.

## NOR-31 semantic-event contract rebaseline (2026-08-05)

- **Which rule changed?** No gameplay rule changed. Expected rows now identify semantic game facts;
  presentation cues are a separate optional field and are not part of replay comparison.
- **Why is this correct?** Across all nine fixtures, all 168 rows preserve sequence, source, target,
  amount, and flags. Event counts and every state hash are unchanged.
- **Link to the change:** NOR-31, Program step 10.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** yes, to `interfaces.v4`; replay schema changed to v3.

## NOR-31 silence-state hash rebaseline (2026-08-05)

- **Which rule changed?** `CardInstance.IsSilenced` is now canonical state, serialized and projected.
- **Why is this correct?** This fixture never applies SILENCE; its event stream and gameplay state are unchanged. The new false boolean participates in every card's canonical walk, so only the expected digest changes.
- **Link to the change:** NOR-31 silence-state implementation; `GameStateSerializer.WalkCard` and `CanonicalStateFixtures` mutation manifest.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no; replay intent/event schema is unchanged.
