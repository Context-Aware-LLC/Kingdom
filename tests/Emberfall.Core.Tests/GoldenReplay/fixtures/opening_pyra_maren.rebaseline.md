# Rebaseline: opening_pyra_maren

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** N/A — **initial baseline** (first bake against the M1-foundation core:
  `MatchEngine.BeginMatch` + `CommandProcessor` mulligan handling).
- **Why the new golden is correct:** Covers the RNG-dense opening (coin flip → deck shuffle → 3/4 deal →
  `token_spark_coin` insertion → mulligan entry → first player's start-of-turn draw), matching the spec
  sequence (interfaces.md §2.1) and corroborated by the determinism unit tests (`MatchTests`).
- **Link to the design/eng change that caused it:** initial commit of the determinism core
  (branch `feat/emberfall-core-determinism`).
- **cardSetSnapshot changed?** no — first pin of `M1.cards.snapshot.json`.
- **engineContractVersion changed?** no — `interfaces.v2`.
