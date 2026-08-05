# Rebaseline: toll_cascade

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **D-P1 sentinel reservation** (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md §5.2.1`,
  ruled at the A3 design-pass review `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-projector-design.md` §10, ratified by Sam
  2026-07-15): `InstanceId(0)` is the reserved redaction sentinel and is never minted — the
  `GameState` mint counter now starts at 1. (The same commit lands D-OQ1 bounce re-mint, which
  this fixture does not exercise — no RETURN effect in its scripted line.) No gameplay rule
  changed: same cascade batch ordering, same board/graveyard outcome.
- **Why the new golden is correct:** regenerated via `CascadeFixtureGenerator` (same seed
  `3141592653589793`, same scripted line) because its intent log embeds `InstanceId`s
  (`play_card i22→i23`, `attack i22/i32→i23/i33`); every id shifts uniformly by +1,
  insertions equal deletions, event count and kinds identical. The generator's in-scenario
  assertions (both Lantern-Bearers die, one Wisp per side) passed during regeneration.
  Old → new:
  `6c538f38509dd9e675dae445e02ebf4373cce8703b418185d084edcc2af7c4da` →
  `ffc7c5a4e587a7015d5cb872f8661ebdc1762004fb70bb774ac63fce5f9e0409`.
- **Link to the design/eng change that caused it:** `hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md
  §5.2.1` (D-P1/D-OQ1), `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-projector-design.md` §4/§10, invariant tests
  `tests/Emberfall.Core.Tests/Determinism/SentinelReservationTests.cs`.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no — value-not-shape (same ruling class as OBL-7).
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
