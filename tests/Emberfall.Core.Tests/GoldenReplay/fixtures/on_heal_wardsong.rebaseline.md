# Rebaseline: on_heal_wardsong

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-015 owes under the standing rule
  "no new effect without a golden", covering the `ON_HEAL` raise this task adds to
  `DamagePipeline.ApplyHeal` (`interp.RaiseSelf(target.Minion!, Trigger.ON_HEAL, …)`), enqueue-only
  onto the interpreter's shared queue, on the same terms EF-013 set for `ON_DAMAGED`.

  **The shape decision this fixture also records.** The brief calls EF-015 decision-free, and it is
  decision-free on ordering — EF-013 already settled that — but not on *who reacts*. `ON_HEAL` is a
  **SELF** trigger on the healed minion (`RaiseSelf`), not an observer sweep (`RaiseObservers`, the
  shape `ON_SPELL_CAST` and `ON_TURN_END` use). It mirrors `ON_DAMAGED` at the sibling site exactly,
  so it reads "whenever THIS minion is healed". Rationale is recorded at length on `ApplyHeal` in
  `DamagePipeline.cs`; the short form is that an observer sweep on the LIFEDRAIN path would enqueue
  one effect per permanent on both boards per swing, and widening later is additive where narrowing
  later would not be.

- **Why the new golden is correct:** baked by `OnHealWardsongFixtureGenerator` (seed
  `2718281828459045`), whose in-scenario assertions all passed during generation. The leader boards two
  `test_heal_wardsong` (1/3, `ON_HEAL → DEAL_DAMAGE 1`, `selector: ENEMY_HERO`); the follower's
  `frost_ripple` wounds both to 2/3; the leader then boards a **third, untouched** wardsong and casts
  `test_heal_salve`, a 2-point heal over `FRIENDLY_MINIONS`, catching all three in one sweep. The baked
  event stream is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 19 | `onPlay` i28 | the third wardsong joins at full health — the control |
  | 20 | `onPlay` i24 | the salve is cast; it sweeps all three friendly minions |
  | 21 | `onHeal` i24→i23 amt **1** | wounded wardsong #1 restored — clamped from the requested 2 |
  | 22 | `onHeal` i24→i26 amt **1** | wounded wardsong #2 restored — clamped likewise |
  | 23 | `onDamage` i23→i2 amt 1 | ping from #1's own `ON_HEAL` |
  | 24 | `onDamage` i26→i2 amt 1 | ping from #2's own `ON_HEAL` — and the stream ends |

  Those rows are the point of the fixture. They pin three properties that no per-heal unit test can
  reach:

  1. **The raise exists.** Rows 23–24 are two minions' own `ON_HEAL` resolving after their health was
     really restored.
  2. **An ineffective heal is not a heal.** The salve swept **three** targets and the stream carries
     **two** `onHeal` events and **two** pings. i28 was at full health, so `ApplyHeal` returned at its
     `healed <= 0` guard — above both the emit and the raise. Moving the raise above that guard
     produces a third `onDamage` and fails here. This is the heal-side twin of `on_damaged_thorns`'
     Aegis control, and the only fixture that enforces it.
  3. **Enqueue-only — no drain between targets.** Both heals (21, 22) land *before* either ping
     (23, 24). A drain-between-targets implementation interleaves them as
     `onHeal, onDamage, onHeal, onDamage`, reordering the stream — the ordering decision recorded at
     length in `DamagePipeline.cs` and inherited from EF-013.

  The clamp is pinned as a side effect worth naming: the salve requests 2 against 1 missing health, and
  each `onHeal` carries the **effective** 1. Nothing dies anywhere in the log — 3-health minions against
  a 1-damage volley — so this fixture stays about the heal trigger rather than doubling as a toll
  test (`toll_cascade` owns that). Baked hash:
  `24e4e68b544d280a5a4a71d058d631f2fe1ad8ce7ae5a640c72724ecf17f3a97` (25 events, 11 intents). The match
  is deliberately still `InProgress` at the end of the log — a decided outcome would truncate the replay.

- **Link to the design/eng change that caused it:** EF-015 (NOR-55), `LAUNCH-WORKPLAN.md` Track E;
  raise site `src/Emberfall.Core/DamagePipeline.cs`, on the queue EF-013 (NOR-53) introduced. The same
  commit flips `ON_HEAL` Dead→MinionOnly in `CardValidator.TriggerLivenessTable` and updates the
  `Trigger` enum markers (`Enums.cs`), discharging the retirement trigger both carried. Headless siblings
  in `tests/Emberfall.Core.Tests/Combat/HealPipelineQueueTests.cs` — including
  `ON_HEAL_Is_A_Self_Trigger_Not_An_Observer_Sweep`, the only enforcement of the shape decision above,
  and `Lifedrain_Heal_Inside_ApplyDamage_Raises_No_ON_HEAL`, which pins the reentrant path the brief
  flags as raising nothing today (its target is always a hero, and heroes carry no card effects).

  **Note on the brief's line number:** it cites `DamagePipeline.cs:111`. That drifted when EF-013 and
  EF-014 landed; the heal path is `ApplyHeal`, further down the same file. Same site, stale coordinate.

- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained two cards**, `test_heal_wardsong`
  and `test_heal_salve`, appended at the end. Both are fixture-only (`collectible: false`, not in the
  shipping `cards/**` tree) and exist solely to give this replay an `ON_HEAL` subscriber and a no-target
  sweep to heal it with; no existing row was edited. `FRIENDLY_MINIONS` rather than `ANY_FRIENDLY` so the
  interpreter auto-resolves the sweep and the recorded intent carries no target ids. The append is inert
  for the other fixtures — decks are explicit id lists and `GameStateHasher` walks game state, never the
  card db — and the proof is that every pre-existing fixture replays green **unrebaselined** with both
  cards present. This is the same append-never-insert contract `test_turn_end_toller` established for
  EF-012.

- **engineContractVersion changed?** no. `Trigger.ON_HEAL` already existed in the enum; EF-015 adds its
  raise site, a golden and unit tests — none is a contract member. Stays `interfaces.v3`.

## NOR-31 semantic-event contract rebaseline (2026-08-05)

- **Which rule changed?** No gameplay rule changed. Expected rows now identify semantic game facts;
  presentation cues are a separate optional field and are not part of replay comparison.
- **Why is this correct?** Across all nine fixtures, all 168 rows preserve sequence, source, target,
  amount, and flags. Event counts and every state hash are unchanged.
- **Link to the change:** NOR-31, Program step 10.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** yes, to `interfaces.v4`; replay schema changed to v3.
