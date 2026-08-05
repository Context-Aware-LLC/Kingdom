# Rebaseline: unearth_choice

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** This is a new NOR-31 fixture for the first authoritative Unearth
  implementation; no pre-existing expected block was changed.
- **Why the new golden is correct:** With the pinned seed, the first `UnearthOffered` has sequence
  3 and is resolved by the recorded exact option `core_veilstalker` plus correlation sequence 3.
  The second offer has sequence 6 and `AutoConfirm` creates its first ordered option. Both choices
  clear `PendingChoice`, mint one hand instance, and emit owner-private `CardCreatedInHand` events;
  the eight-row event stream and final canonical hash pin that behavior.
- **Link to the design/eng change that caused it:** NOR-31 Unearth runtime, correlated intent,
  pending-choice projection, and canonical-state continuity implementation.
- **cardSetSnapshot changed?** yes — `M1.cards.snapshot.json` appends only
  `test_unearth_source`, a fixture-only `collectible:false` spell. It cannot enter the eligible
  offer pool and is absent from every other fixture's explicit deck, so existing event streams are
  unaffected.
- **engineContractVersion changed?** no — remains `interfaces.v4`; the existing typed intent
  contract gained an implemented replay-harness tag, not a new engine-contract version.
