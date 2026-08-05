# Rebaseline: spell_cast_echo

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — **this is a NEW fixture, not a re-bake.** Nothing was rebaselined;
  there is no old hash to compare against. It is the golden EF-016 owes under the standing rule
  "no new effect without a golden": `Trigger.ON_SPELL_CAST` was declared in `Enums.cs` and raised
  nowhere, and `CommandProcessor.ApplyPlayCard` now raises it
  (`interp.RaiseObservers(Trigger.ON_SPELL_CAST, excluded: null)`) from the `CardType.SPELL` arm
  only, immediately after the spell's own `RaiseSelf(card, Trigger.ON_CAST, targets)`.
- **Why the new golden is correct:** baked by `SpellCastEchoFixtureGenerator` (seed
  `1618033988749894`), whose in-scenario assertions all passed during generation. Both players run
  `test_spell_cast_echo` (1/3, `ON_SPELL_CAST → DEAL_DAMAGE 1`, `selector: ENEMY_HERO`) and
  `test_spell_cast_probe` (0-cost SPELL, `ON_CAST → DEAL_DAMAGE 2`, `selector: ENEMY_HERO`). The
  baked event stream is exactly what the raise site's contract predicts:

  | seq | event | meaning |
  |---|---|---|
  | 6 → 7, 8 | `CardPlayed` (i64), `DamageApplied` i64→i1 amt 2, then `DamageApplied` i43→i2 amt 1 | the spell's own `ON_CAST` resolves **first**; the echo reacts after it — and that echo belongs to the player who did **not** cast |
  | 13 → 14 | `HeroPowerUsed` (i1), `DamageApplied` i84→i2 amt 1, **and nothing else** | **THE TRAP.** A hero power raises the same `Trigger.ON_CAST` through an ephemeral `HERO_POWER` card (i84). Two echoes are standing on the boards. Neither fires |
  | 18 → 19, 20, 21 | `CardPlayed` (i65), `DamageApplied` i65→i1, then i63→i1, then i43→i2 | one cast, **both** boards swept, **active side first** (i63 is the caster's echo, i43 the opponent's) |

  Rows 2 and 3 are the point of the fixture. Row 2 pins the gate that the whole task turns on: the
  raise lives in `ApplyPlayCard`'s `CardType.SPELL` arm and nowhere else, so "whenever a spell is
  cast" excludes hero powers. Hooking generic `ON_CAST` instead — the obvious and wrong
  implementation — would add two `DamageApplied` rows at seq 14 and fail this fixture forever. Row 3
  pins the scope and the order: an "any spell" observer sweep across both boards rather than a
  controller-scoped "whenever YOU cast", in `RaiseObservers`' canonical active-player-first walk.
  Row 1 pins queue order — `RaiseSelf` before `RaiseObservers` into a FIFO queue — which totals
  alone cannot show, because both effects damage the same hero.

  Baked hash: `2f5e482b4db3513cf8e998726f2da62f46911c0957c0177510b42ca700d3148f` (22 events,
  10 intents). The match is deliberately still `InProgress` at the end of the log — a decided
  outcome would truncate the replay. Both seats are `EMBERWARD` on purpose, so the hero-power leg
  is `searing_bolt` (1 damage, `ANY_CHARACTER`) whichever seat wins the coin flip; a mixed pairing
  would make the trap proof depend on the flip.
- **Link to the design/eng change that caused it:** EF-016 (NOR-56), `LAUNCH-WORKPLAN.md`
  Track E; raise site `src/Emberfall.Core/CommandProcessor.cs` (`ApplyPlayCard`, `CardType.SPELL`);
  liveness flipped Dead→Live in `CardValidator.TriggerLivenessTable` and in the `Trigger` enum
  markers (`Enums.cs`); headless siblings in
  `tests/Emberfall.Core.Tests/CardBehavior/SpellCastTriggerTests.cs`.
- **cardSetSnapshot changed?** **yes — `M1.cards.snapshot.json` gained two cards**,
  `test_spell_cast_echo` and `test_spell_cast_probe`, appended at the end. Both are fixture-only
  (`collectible: false`, not in the shipping `cards/**` tree) and exist solely to give this replay
  an `ON_SPELL_CAST` subscriber and a cheap spell to trigger it; no existing row was edited. The
  append is inert for the other fixtures — decks are explicit id lists and `GameStateHasher` walks
  game state, never the card db — and the proof is that `opening_pyra_maren`,
  `full_match_pyra_wins`, `toll_cascade`, `turn_end_toll` and `on_damaged_thorns` all replay
  green **unrebaselined** with the two cards present, on a tree rebased onto `origin/master` after
  EF-014 landed its own two fixture-only cards into the same snapshot.
- **engineContractVersion changed?** no. `Trigger.ON_SPELL_CAST` already existed in the enum;
  EF-016 adds a raise site, not a contract member. Stays `interfaces.v3`.

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

## NOR-31 effect-source identity rebaseline (2026-08-05)

- **Which rule changed?** Hero powers no longer mint a throwaway `CardInstance`. Their immutable
  `EffectSourceContext` uses the already-public hero identity, the acting player as controller, the
  hero-power definition/rule reference, `HeroPower` source kind, and no current carrier.
- **Why is this correct?** Sequence 14 remains the same one point of Searing Bolt damage with no
  spell-cast observers. Its source changes from synthetic `i84` to hero `i1`, matching sequence 13's
  `HeroPowerUsed` fact. The final state also no longer advances `NextInstanceCounter` for an object
  that never entered canonical state. All other 21 events are byte-for-byte unchanged.
- **Hashes:** the silence-state baseline `c07361118ec41cdec8bdd646fb668204819900e27a69e2e4c214df4b6ba816c1`
  changes to the combined digest `99ae8e81ff1fc4c8e8648a542dbe07e4ce93a51b8b335520b2ee8485542d7050`.
- **Link to the change:** NOR-31 Program step 3, explicit effect-source contexts.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no; the semantic event shape is unchanged.
