# Rebaseline: deathrattle_cascade

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **OBL-7 every-shuffle id re-mint** (`state-sync-and-security.md §5.2.1`,
  ruled in `engineering/a3-kickoff-review-2026-07-15.md` Q1, ratified by Sam 2026-07-15).
  `DeckOps.Shuffle` now re-mints deck-resident `InstanceId`s in post-shuffle deck order at every
  shuffle site, retiring the decklist-ordinal ids `BuildDeck` mints pre-shuffle. No gameplay rule
  changed: same RNG call order, same cascade batch ordering, same board/graveyard outcome.
- **Why the new golden is correct:** this fixture, unlike the other two, embeds `InstanceId`s in
  its *intent log* (`play_card` / `attack` reference the drawn Lantern-Bearers), so it was fully
  **regenerated** via `CascadeFixtureGenerator` (same seed `3141592653589793`, same scripted line)
  rather than hash-only re-baked — intents, event stream ids, and hash all re-recorded against the
  re-minted id space. The generator's in-scenario assertions (both Lantern-Bearers die, one Wisp
  per side) passed during regeneration. Old → new:
  `f8e659674fd1eca3ba630e27cade20c9b14c504e33a1dd278b4c1ea26224aea7` →
  `6c538f38509dd9e675dae445e02ebf4373cce8703b418185d084edcc2af7c4da`.
  (Note: this file's previous A2b entry cited `full_match_pyra_wins`' digests — the two files'
  old→new pairs were swapped at the A2b episode; corrected as of this entry.)
- **Link to the design/eng change that caused it:** `multiplayer/state-sync-and-security.md §5.2.1`
  (OBL-7), §7 convene record `engineering/a3-kickoff-review-2026-07-15.md`, invariant tests
  `tests/Emberfall.Core.Tests/Determinism/DeckIdReMintTests.cs`.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no — ruled **no bump** (value-not-shape change; Sam,
  2026-07-15, DECISION block in the A3 kickoff review record). Stays `interfaces.v3`.
