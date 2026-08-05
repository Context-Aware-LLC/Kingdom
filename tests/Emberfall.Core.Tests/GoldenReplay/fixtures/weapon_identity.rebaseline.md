# Rebaseline: weapon_identity

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

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

  The blade carries current canonical `LIFESTEAL`, `POISONOUS`, `FREEZE`, and `WINDFURY` names.
  The event stream pins the existing rider order and controls:

  - the first two-damage hit heals the wielder, freezes the target, then Venom settles a six-health
    minion's death;
  - an Aegis-absorbed hit emits amount 0 with `AegisAbsorbed` and no heal/freeze/death rider; and
  - the face hit heals, freezes the enemy hero, then breaks the three-durability blade.

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
