# Rebaseline: full_match_pyra_wins

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** N/A — **initial baseline** (first bake against the M1-foundation core).
- **Why the new golden is correct:** Drives a complete match to a win condition (opening → both mulligans →
  first player's turn 1 → P1 concede → `MatchOutcome.Player0Win`), exercising the turn-loop spine and the
  WinCheck path. Verified against the spec and the `MatchTests.Concede_*` unit test.
- **Link to the design/eng change that caused it:** initial commit of the determinism core
  (branch `feat/emberfall-core-determinism`).
- **cardSetSnapshot changed?** no — first pin of `M1.cards.snapshot.json`.
- **engineContractVersion changed?** no — `interfaces.v2`.

> _Combat / Deathrattle coverage (the `deathrattle_cascade` fixture, golden-replay.md §7) lands with the
> effect interpreter in M1 Phase B — see `tests/Emberfall.Core.Tests/GoldenReplay/README.md`._
