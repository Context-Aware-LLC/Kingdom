# Rebaseline: opening_pyra_maren

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **D-P1 sentinel reservation** (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md §5.2.1`,
  ruled at the A3 design-pass review `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-projector-design.md` §10, ratified by Sam
  2026-07-15): `InstanceId(0)` is the reserved redaction sentinel and is never minted — the
  `GameState` mint counter now starts at 1. (The same commit lands D-OQ1 bounce re-mint, which
  this fixture does not exercise — no RETURN effect in its intent log.) No gameplay rule
  changed: same RNG call order, same zones, same outcomes.
- **Why the new golden is correct:** every `InstanceId` in the replay shifts uniformly by +1
  (hero0 1, hero1 2, …); the diff shows only id lines and the hash — insertions equal
  deletions, event count and kinds identical. Old → new:
  `566033d5907cfd09b9d959a6754600b6a5e8511e415a183a3a74a9bd6e08c976` →
  `38e1d7486d24b4f43f5dfa96c21226b262d63413854495d2e3f191c5b382130c`.
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
