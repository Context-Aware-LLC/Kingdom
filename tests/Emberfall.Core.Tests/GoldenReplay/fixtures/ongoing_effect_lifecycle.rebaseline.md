# Rebaseline: ongoing_effect_lifecycle

_Required by dev-docs/quality/testing-and-ci.md:31 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** this is a new NOR-31 fixture. Resolved spell rules with future
  observer triggers now create carrierless ongoing-effect instances with explicit lifetimes.
- **Why the new golden is correct:** the replay casts a PERMANENT and a ONE_SHOT spell, then
  advances one turn. Creation order is i84 then i85; both trigger in that order; i85 expires;
  i84 remains in final canonical state. The source cards themselves remain in graveyard.
- **Link to the design/eng change that caused it:** NOR-31 step 4 runtime/state commits
  `8a8d02a`, `e45205b`, and `5742ea5` on the integration lane.
- **cardSetSnapshot changed?** yes — appended two non-collectible fixture-only spells;
  no shipping card row changed.
- **engineContractVersion changed?** no — remains `interfaces.v4`.
