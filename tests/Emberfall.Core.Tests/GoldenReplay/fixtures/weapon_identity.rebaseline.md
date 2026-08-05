# Rebaseline: weapon_identity

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

## NOR-31 ongoing-effect canonical rebaseline (2026-08-05)

- **Which rule changed:** `GameState` gained the canonical `OngoingEffects` tail. Even an empty list writes a deterministic zero count, so every prior final-state digest changes while its event stream remains identical.
- **Why the new golden is correct:** all nine fixtures replayed with unchanged intents and unchanged semantic events; only the reviewed canonical encoding tail changed. The serializer field manifest, per-field mutation sweep, pinned digest, projected/authority wire bridge, and strict snapshot round-trip are green.
- **Linked change:** NOR-31 step 4, commit `5742ea5` (`feat(state): serialize and project ongoing effects`).
- **cardSetSnapshot changed?** no for this rebaseline; the dedicated ongoing fixture appends fixture-only cards separately.
- **engineContractVersion changed?** no; `interfaces.v4` remains the recorded contract label.


- **Which rule changed:** this is a **new fixture**, not a re-bake. NOR-31 Program step 2
  deletes the four-field `WeaponInstance` wrapper. Playing a weapon now moves the original
  `CardInstance` from hand to `Zone.Weapon`; breaking it moves that same instance to graveyard.
  The populated weapon canonical/wire branch therefore carries the full card shape: controller,
  zone, current stats/durability, keywords, enchantments, and runtime flags.

  All eight pre-existing GoldenReplay fixtures remain green **unrebaselined**. They never equip a
  weapon, so their nullable weapon branch is still the same absent flag. The independent pinned
  canonical seed intentionally does equip one and moved from `23b5920205…` to `7e02a1ff01…`.

- **Why the new golden is correct:** `WeaponIdentityFixtureGenerator` uses seed
  `1618033988749894` and asserts the transition identities while recording the public command log.
  The played blade is hand id `i29`; the final weapon-death event is also sourced by `i29`, and the
  same object is in graveyard after the third swing. No replacement weapon id is minted.

  The blade carries current canonical `LIFEDRAIN`, `VENOM`, `FROST`, and `DOUBLESTRIKE` names.
  The event stream pins the existing rider order and controls:

  - the first two-damage hit heals the wielder, frosts the target, then Venom settles a six-health
    minion's death;
  - an Aegis-absorbed hit emits amount 0 with `AegisAbsorbed` and no heal/frost/death rider; and
  - the face hit heals, frosts the enemy hero, then breaks the three-durability blade.

  The baked hash is `669f9dd244c3b57a234b48b8e2dd73138ae105890028580e39bfebf1ffebae3d`.
  The match remains `InProgress`, so no terminal outcome truncates the stream.

- **Link to the design/eng change that caused it:** NOR-31 Program step 2 in
  `refactor-audit/REFACTOR-PLAN-for-review.md`; runtime changes in `GameState`, `RulesEngine`, and
  `CommandProcessor`; state-contract changes in `GameStateSerializer`, `GameStateProjector`, and
  `SnapshotJsonCodec`; focused acceptance in `WeaponIdentityTests`.
- **cardSetSnapshot changed?** yes. `M1.cards.snapshot.json` appends three fixture-only,
  `collectible:false` rows: `test_rider_blade`, `test_rider_brute`, and `test_rider_aegis`. Existing
  rows are untouched, decks are explicit, and the eight older fixtures prove the append inert.
- **engineContractVersion changed?** no; it remains `interfaces.v3`. This is a sanctioned game-state
  migration, not a change to the public engine interface vocabulary.

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
