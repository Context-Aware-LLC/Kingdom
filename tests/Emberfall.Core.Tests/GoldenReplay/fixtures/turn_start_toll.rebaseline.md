# Rebaseline: turn_start_toll

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-011 owes under the standing rule
  "no new effect without a golden": `Trigger.ON_TURN_START` was declared in `Enums.cs` and raised
  nowhere, and `CommandProcessor.StartTurnSequence` now raises it
  (`interp.RaiseObservers(Trigger.ON_TURN_START, excluded: null)`) after the thaw/mana reset and
  before `RulesEngine.DrawOne`.
- **Why the new golden is correct:** baked by `TurnStartTollFixtureGenerator` (seed
  `3141592653589793`), whose in-scenario assertions all passed during generation. Both players run
  `test_turn_start_toller` (1/3, `ON_TURN_START → DEAL_DAMAGE 1`, `selector: ENEMY_HERO`). The baked
  event stream is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 4 → 5 → 6 | `onTurnStart` (i2), `onDamage` i23→i2 amt 1, `onDraw` i37 | the toll resolves on the turn start, and **before the draw** |
  | 9 → 10, 11 → 12 | `onTurnStart` (i1), `onDamage` i23→i2 then i33→i1, `onDraw` i27 | **both** boards fire on one turn start, **active side first**, still ahead of the draw |
  | 14 → 15, 16 → 17 | `onTurnStart` (i2), `onDamage` i33→i1 then i23→i2, `onDraw` i38 | the same sweep, order **flipped** with the active seat |

  Three properties are pinned here that nothing else pins:

  1. **The raise is an "any turn start" observer sweep across both boards**, not a controller-scoped
     "start of YOUR turn" — the same scope ruling `ON_TURN_END` took in EF-012, for the same reason
     (`RaiseObservers` is the engine's one observer sweep; a controller filter would be a new,
     unspecified rule). A controller-scoped reimplementation drops half these `onDamage` rows.
  2. **The sweep order is `RaiseObservers`' canonical active-player-first walk**, so it reverses
     every turn — rows 2 and 3 are the same scenario with the seats swapped, and their `onDamage`
     pairs come out in opposite order. A fixed-seat ordering leaves those two rows identical.
  3. **The toll lands before the draw.** Every `onDamage` sits between its turn's `onTurnStart` and
     that turn's `onDraw`. This is the EF-010 seam's stated ordering — settle the cascade first, "or
     a corpse would draw fatigue" — and hoisting the raise below `DrawOne` reorders these rows and
     fails the fixture. The other half of that ordering (the raise sits *after* the thaw/mana reset)
     emits no event and so cannot be pinned here;
     `TurnStartTriggerTests.Turn_Start_Trigger_Fires_After_The_Mana_Reset` pins it instead, by
     granting temporary mana from the trigger and asserting it survives the reset that would
     otherwise zero it.

  Baked hash: `53e7b3bfc64dd33ebb1dd3c8077c345a0d7dacf0a13384881c81cafdfc232c3a` (18 events,
  7 intents). The match is deliberately still `InProgress` at the end of the log — a decided
  outcome would truncate the replay.
- **Link to the design/eng change that caused it:** EF-011 (NOR-51), `LAUNCH-WORKPLAN.md`
  Track E; raise site `src/Emberfall.Core/CommandProcessor.cs` (`StartTurnSequence`); liveness
  flipped Dead→Live in `CardValidator.TriggerLivenessTable` and in the `Trigger` enum markers
  (`Enums.cs`); headless siblings in `tests/Emberfall.Core.Tests/TurnLoop/TurnStartTriggerTests.cs`.
- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained one card**,
  `test_turn_start_toller`, appended at the end. It is fixture-only (`collectible: false`, not in the
  shipping `cards/**` tree) and exists solely to give this replay an `ON_TURN_START` subscriber; no
  existing row was edited. The append is inert for the pre-existing fixtures — decks are explicit id
  lists and `GameStateHasher` walks game state, never the card db — and the proof is that all of
  them replay green **unrebaselined** with the card present (their `.replay.json` files are
  byte-unchanged in this commit).
- **engineContractVersion changed?** no. `Trigger.ON_TURN_START` already existed in the enum; EF-011
  adds a raise site, not a contract member. Stays `interfaces.v3`.
