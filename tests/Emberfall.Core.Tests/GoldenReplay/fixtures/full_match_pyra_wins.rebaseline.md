# Rebaseline: full_match_pyra_wins

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **OBL-7 every-shuffle id re-mint** (`state-sync-and-security.md §5.2.1`,
  ruled in `engineering/a3-kickoff-review-2026-07-15.md` Q1, ratified by Sam 2026-07-15).
  `DeckOps.Shuffle` now re-mints deck-resident `InstanceId`s in post-shuffle deck order at every
  shuffle site, retiring the decklist-ordinal ids `BuildDeck` mints pre-shuffle. No gameplay rule
  changed: same RNG call order, same turn-loop spine, same `Player0Win` outcome.
- **Why the new golden is correct:** identical replay behavior; only id *values* moved (the
  fixture's single draw event moved `src: i6 → i25`; every other event line unchanged), and the
  state hash follows because `CardInstance.Id` and `NextInstanceCounter` are hash-canonical.
  Old → new:
  `4f664233c6b30d70b7435ba057a17fdcb2686e288a3dd81cdf472f6ab4e468de` →
  `408546a31aa1e3f43c06ad7030821182b492c5e24eb8900f69da7c394f89081b`.
  (Note: this file's previous A2b entry cited `deathrattle_cascade`'s digests — the two files'
  old→new pairs were swapped at the A2b episode; corrected as of this entry.)
- **Link to the design/eng change that caused it:** `multiplayer/state-sync-and-security.md §5.2.1`
  (OBL-7), §7 convene record `engineering/a3-kickoff-review-2026-07-15.md`, invariant tests
  `tests/Emberfall.Core.Tests/Determinism/DeckIdReMintTests.cs`.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no — ruled **no bump** (value-not-shape change; Sam,
  2026-07-15, DECISION block in the A3 kickoff review record). Stays `interfaces.v3`.
