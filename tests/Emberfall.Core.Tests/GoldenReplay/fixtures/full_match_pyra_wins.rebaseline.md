# Rebaseline: full_match_pyra_wins

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **D-P1 sentinel reservation** (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md §5.2.1`,
  ruled at the A3 design-pass review `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-projector-design.md` §10, ratified by Sam
  2026-07-15): `InstanceId(0)` is the reserved redaction sentinel and is never minted — the
  `GameState` mint counter now starts at 1. (The same commit lands D-OQ1 bounce re-mint, which
  this fixture does not exercise — no RETURN effect in its intent log.) No gameplay rule
  changed: same turn-loop spine, same `Player0Win` outcome.
- **Why the new golden is correct:** every `InstanceId` in the replay shifts uniformly by +1;
  the diff shows only id lines and the hash — insertions equal deletions, event count and
  kinds identical. Old → new:
  `408546a31aa1e3f43c06ad7030821182b492c5e24eb8900f69da7c394f89081b` →
  `6fbb8cd899ca9d27c74dfcc10f861de22e61f6c32b789f2a6a3f85867a9c124c`.
- **Link to the design/eng change that caused it:** `hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md
  §5.2.1` (D-P1/D-OQ1), `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-projector-design.md` §4/§10, invariant tests
  `tests/Emberfall.Core.Tests/Determinism/SentinelReservationTests.cs`.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no — value-not-shape (same ruling class as OBL-7).
  Stays `interfaces.v3`.
