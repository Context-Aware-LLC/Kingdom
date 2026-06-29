# Rebaseline: opening_pyra_maren

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** M1 Phase B step 3 — HeroPower support. `MatchEngine.BeginMatch` now sets
  `HeroInstance.HeroPowerId` from the class→hero-power-id mapping (`EMBERWARD` → `"hero_power_searing_bolt"`,
  `TIDECALLER` → `"hero_power_tidal_ward"`). `WalkHero` has always hashed `HeroPowerId`; populating it shifts
  the state hash. Prior hash used empty-string `HeroPowerId` — an incomplete placeholder.
- **Why the new golden is correct:** Heroes now correctly carry their hero power IDs. The state is complete.
  All 12 new `HeroPowerTests` and the 57 pre-existing tests pass against this new hash.
- **Link to the design/eng change that caused it:** M1 Phase B step 3 commit (HeroPower / Weapon support).
- **cardSetSnapshot changed?** yes — added hero power definitions (`hero_power_searing_bolt`,
  `hero_power_tidal_ward`, `hero_power_toll_the_bell`, `hero_power_quicken`) and `token_tide_wisp` token
  to `M1.cards.snapshot.json`.
- **engineContractVersion changed?** no — `interfaces.v2`.
