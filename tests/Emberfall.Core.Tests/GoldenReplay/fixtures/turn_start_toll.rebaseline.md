# Rebaseline: turn_start_toll

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

## NOR-31 ongoing-effect canonical rebaseline (2026-08-05)

- **Which rule changed:** `GameState` gained the canonical `OngoingEffects` tail. Even an empty list writes a deterministic zero count, so every prior final-state digest changes while its event stream remains identical.
- **Why the new golden is correct:** all nine fixtures replayed with unchanged intents and unchanged semantic events; only the reviewed canonical encoding tail changed. The serializer field manifest, per-field mutation sweep, pinned digest, projected/authority wire bridge, and strict snapshot round-trip are green.
- **Linked change:** NOR-31 step 4, commit `5742ea5` (`feat(state): serialize and project ongoing effects`).
- **cardSetSnapshot changed?** no for this rebaseline; the dedicated ongoing fixture appends fixture-only cards separately.
- **engineContractVersion changed?** no; `interfaces.v4` remains the recorded contract label.


- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-011 owes under the standing rule
  "no new effect without a golden": `Trigger.ON_TURN_START` was declared in `Enums.cs` and raised
  nowhere, and `CommandProcessor.StartTurnSequence` now raises it
  with an explicit incoming-player `TriggerContext` after the thaw/mana reset and
  before `RulesEngine.DrawOne`.
- **Why the new golden is correct:** `TurnStartTollFixtureGenerator` defines the scenario with seed
  `3141592653589793`. Both players run
  `test_turn_start_toller` (1/3, `ON_TURN_START`, `TURN_OWNER:ANY`, `DEAL_DAMAGE 1`,
  `selector: ENEMY_HERO`). The baked
  event stream is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 4 → 5 → 6 | `TurnStarted` (i2), `DamageApplied` i23→i2 amt 1, `CardDrawn` i37 | the toll resolves on the turn start, and **before the draw** |
  | 9 → 10, 11 → 12 | `TurnStarted` (i1), `DamageApplied` i23→i2 then i33→i1, `CardDrawn` i27 | **both** boards fire on one turn start, **active side first**, still ahead of the draw |
  | 14 → 15, 16 → 17 | `TurnStarted` (i2), `DamageApplied` i33→i1 then i23→i2, `CardDrawn` i38 | the same sweep, order **flipped** with the active seat |

  Three properties are pinned here that nothing else pins:

  1. **The fixture card explicitly requests `TURN_OWNER:ANY`**, so both the controller's and the
     opponent's turn start qualify. `TurnStartTriggerTests` separately pins `CONTROLLER` and
     `OPPONENT`; changing ANY to either narrower condition drops half these `DamageApplied` rows.
  2. **The sweep order is `RaiseObservers`' canonical active-player-first walk**, so it reverses
     every turn — rows 2 and 3 are the same scenario with the seats swapped, and their `DamageApplied`
     pairs come out in opposite order. A fixed-seat ordering leaves those two rows identical.
  3. **The toll lands before the draw.** Every `DamageApplied` sits between its turn's `TurnStarted` and
     that turn's `CardDrawn`. This is the EF-010 seam's stated ordering — settle the cascade first, "or
     a corpse would draw fatigue" — and hoisting the raise below `DrawOne` reorders these rows and
     fails the fixture. The other half of that ordering (the raise sits *after* the thaw/mana reset)
     emits no event and so cannot be pinned here;
     `TurnStartTriggerTests.Turn_Start_Trigger_Fires_After_The_Mana_Reset` pins it instead, by
     granting temporary mana from the trigger and asserting it survives the reset that would
     otherwise zero it.

     The hostile mutation hoisted both `RaiseObservers` and `Drain` above the reset. Its targeted
     test output was:

     > Failed Turn_Start_Trigger_Fires_After_The_Mana_Reset
     > Expected: 2
     > But was: 0

  The scenario deliberately ends `InProgress`; a decided outcome would truncate the replay log.
- **Link to the design/eng change that caused it:** EF-011 (NOR-51),
  `dev-docs/project/LAUNCH-WORKPLAN.md`
  Track E; raise site `src/Emberfall.Core/CommandProcessor.cs` (`StartTurnSequence`); liveness
  flipped Dead→MinionOnly in `CardValidator.TriggerLivenessTable` and live-for-minions in the `Trigger` enum markers
  (`Enums.cs`); headless siblings in `tests/Emberfall.Core.Tests/TurnLoop/TurnStartTriggerTests.cs`.
- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained one card**,
  `test_turn_start_toller`, appended at the end. It is fixture-only (`collectible: false`, not in the
  shipping `cards/**` tree) and exists solely to give this replay an `ON_TURN_START` subscriber; no
  existing row was edited. Its condition is card-definition data, not mutable match state: decks
  contain explicit card-id lists, while `GameStateHasher` walks game state rather than the card db.
- **engineContractVersion changed?** no. The new condition enums describe immutable card-definition
  data and are not serialized into `GameState` or the wire protocol. Stays `interfaces.v3`.

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
