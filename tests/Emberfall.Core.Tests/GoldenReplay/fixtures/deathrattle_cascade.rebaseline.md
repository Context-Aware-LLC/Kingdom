# Rebaseline: deathrattle_cascade

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** New fixture — no prior hash existed. This is the initial bake for the
  deathrattle cascade golden (M1 Phase B Step 5).
- **Why the new golden is correct:** The fixture was generated and immediately rebaselined by
  `CascadeFixtureGenerator.Write_DeathrattleCascade_Fixture`, which asserts the cascade outcome
  inline before writing: both Lantern-Bearers die simultaneously on a 2/2 trade, their ON_DEATH
  deathrattles fire in active-player-first board order (seq 19→20), and each side receives a
  Wisp token (seq 21→22, `flags: IsToken`). The event stream is human-auditable in the fixture.
- **Link to the design/eng change that caused it:** M1 Phase B Step 5 — `deathrattle_cascade`
  golden fixture; adds coverage for `EffectInterpreter.DeathCheck` cascade loop end-to-end through
  the `PlayCardIntent` + `AttackIntent` replay path.
- **cardSetSnapshot changed?** no
- **engineContractVersion changed?** no
