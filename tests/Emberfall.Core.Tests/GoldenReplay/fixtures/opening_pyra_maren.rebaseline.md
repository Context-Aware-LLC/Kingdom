# Rebaseline: opening_pyra_maren

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** **OBL-7 every-shuffle id re-mint** (`state-sync-and-security.md §5.2.1`,
  ruled in `engineering/a3-kickoff-review-2026-07-15.md` Q1, ratified by Sam 2026-07-15).
  `DeckOps.Shuffle` now re-mints the `InstanceId` of every deck-resident card in post-shuffle deck
  order at every shuffle site (`MatchEngine.BeginMatch`, `ApplyMulligan`). Previously
  `MatchEngine.BuildDeck` minted ids in decklist order *before* the shuffle, so a card's id was a
  stable ordinal into the cost-sorted decklist — an identity leak on every visible card and via id
  gaps. No gameplay rule changed: same RNG call order, same zones, same card outcomes.
- **Why the new golden is correct:** ids of dealt/drawn cards shift from the build range into the
  post-re-mint range (the fixture's single first-draw event moved `src: i3 → i25`; all other event
  lines unchanged), and the state hash moves because `CardInstance.Id` and `NextInstanceCounter`
  are hash-canonical fields. Old → new:
  `ee125dbf0804139717a7af3568b3d45254d10e3e7d2dc4ee4b8bef13ddd5f894` →
  `566033d5907cfd09b9d959a6754600b6a5e8511e415a183a3a74a9bd6e08c976`.
- **Link to the design/eng change that caused it:** `multiplayer/state-sync-and-security.md §5.2.1`
  (OBL-7), §7 convene record `engineering/a3-kickoff-review-2026-07-15.md`, invariant tests
  `tests/Emberfall.Core.Tests/Determinism/DeckIdReMintTests.cs`.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** no — ruled **no bump** (value-not-shape change; Sam,
  2026-07-15, DECISION block in the A3 kickoff review record). Stays `interfaces.v3`.
