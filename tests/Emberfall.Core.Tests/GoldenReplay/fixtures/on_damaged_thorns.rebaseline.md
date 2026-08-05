# Rebaseline: on_damaged_thorns

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-014 owes under the standing rule
  "no new effect without a golden", covering the `ON_DAMAGED` raise that EF-013 landed in
  `DamagePipeline.ApplyDamage` (`interp.RaiseSelf(target.Minion!, Trigger.ON_DAMAGED, …)`,
  `DamagePipeline.cs:128`), enqueue-only onto the interpreter's shared queue.

  Note for anyone auditing EF-014's scope: the brief names **two** sites, `DamagePipeline.cs:31`
  (Aegis-absorbed) and `:52` (normal hit). Only the normal hit raises. The Aegis path deliberately
  returns *before* the raise, because an absorbed hit is not "damaged" (G0 ruling R-2(c), "no on-hit
  riders"). That is a decision EF-013 already made and recorded in the pipeline's comments; this
  fixture and its headless sibling now **enforce** it rather than leaving it as prose.

- **Why the new golden is correct:** baked by `OnDamagedThornsFixtureGenerator` (seed
  `1618033988749894`), whose in-scenario assertions all passed during generation. The leader boards
  `test_thornback` (1/5, `ON_DAMAGED → DEAL_DAMAGE 1`, `selector: ENEMY_HERO`) and then
  `test_aegis_thornback` — the identical card plus `AEGIS`. The follower casts `frost_ripple`,
  a 1-damage sweep over `ENEMY_MINIONS`, catching both in a **single volley**. The baked event stream
  is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 14 | `DamageApplied` i35→i23 amt 1, `None` | the bare thornback really takes the volley |
  | 15 | `DamageApplied` i35→i24 amt 0, `AegisAbsorbed` | the shielded twin is hit — shield eats it, 0 health lost |
  | 16 | `DamageApplied` i23→i2 amt 1, `None` | **exactly one** retaliation, from the unshielded minion only |

  Those three rows are the point of the fixture. They pin three properties that no per-hit unit test
  can reach:

  1. **The raise exists.** Row 3 is a minion's own `ON_DAMAGED` resolving after it took a real hit.
  2. **An Aegis-absorbed hit is not "damaged".** Rows 1 and 2 are the same volley against two
     otherwise-identical minions, and only one retaliates. A raise moved above the shield's early
     return would produce a second `DamageApplied` back at the caster and fail this fixture.
  3. **Enqueue-only — no drain between hits.** Both volley hits (14, 15) land *before* the
     retaliation (16). A drain-between-hits implementation interleaves it as seq 15, reordering the
     stream — the ordering decision recorded at length in `DamagePipeline.cs`.

  Both minions are 5 health against a 1-damage volley, so nothing dies: this fixture pins the damage
  trigger, and keeping death out of the log keeps it from doubling as a toll test
  (`toll_cascade` already owns that). Baked hash:
  `a8360284a1b54039312e4da52f95dfc34b6346dc3b053f63e89f2d54e1fffd82` (17 events, 8 intents). The
  match is deliberately still `InProgress` at the end of the log — a decided outcome would truncate
  the replay.

- **Link to the design/eng change that caused it:** EF-014 (NOR-54), `LAUNCH-WORKPLAN.md` Track E;
  raise site `src/Emberfall.Core/DamagePipeline.cs` landed by EF-013 (NOR-53, `366ea12`/`5db49e3`),
  which also flipped `ON_DAMAGED` Dead→Live in `CardValidator.TriggerLivenessTable` and in the
  `Trigger` enum markers (`Enums.cs`). Headless siblings in
  `tests/Emberfall.Core.Tests/Combat/DamagePipelineQueueTests.cs` — EF-014 adds
  `Aegis_Absorbed_Hit_Raises_No_ON_DAMAGED` there, the only enforcement of the site-2 decision above.

- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained two cards**, `test_thornback`
  and `test_aegis_thornback`, appended at the end. Both are fixture-only (`collectible: false`, not in
  the shipping `cards/**` tree) and exist solely to give this replay an `ON_DAMAGED` subscriber and its
  shielded control; no existing row was edited. The append is inert for the other four fixtures —
  decks are explicit id lists and `GameStateHasher` walks game state, never the card db — and the
  proof is that `opening_pyra_maren`, `full_match_pyra_wins`, `toll_cascade` and
  `turn_end_toll` all replay green **unrebaselined** with both cards present. This is the same
  append-never-insert contract `test_turn_end_toller` established for EF-012.

- **engineContractVersion changed?** no. `Trigger.ON_DAMAGED` already existed in the enum; EF-013
  added the raise site and EF-014 adds only a golden and a unit test — neither is a contract member.
  Stays `interfaces.v3`.

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
