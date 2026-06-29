# Rebaseline: full_match_pyra_wins

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** M1 Phase B step 3 — HeroPower support. Same root cause as `opening_pyra_maren`:
  `MatchEngine.BeginMatch` now sets `HeroInstance.HeroPowerId`, which is included in `WalkHero`.
- **Why the new golden is correct:** Heroes now correctly carry their hero power IDs. This fixture's sequence
  (opening → mulligans → P1 concede) is unchanged; only the final state hash shifts due to the populated
  `HeroPowerId` fields.
- **Link to the design/eng change that caused it:** M1 Phase B step 3 commit (HeroPower / Weapon support).
- **cardSetSnapshot changed?** yes — same additions as `opening_pyra_maren.rebaseline.md`.
- **engineContractVersion changed?** no — `interfaces.v2`.

> _Combat / Deathrattle coverage (the `deathrattle_cascade` fixture, golden-replay.md §7) lands with the
> effect interpreter in M1 Phase B — see `tests/Emberfall.Core.Tests/GoldenReplay/README.md`._
