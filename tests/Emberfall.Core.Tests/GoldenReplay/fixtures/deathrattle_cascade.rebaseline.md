# Rebaseline: deathrattle_cascade

_Required by golden-replay.md §5.2 — a hash flip is guilty until proven a deliberate rule change._

- **Which rule changed:** none — no rules change. **A2b PendingChoice seam (decision 3, signed):**
  added `GameState.PendingChoice` as a nullable tail field (always null in M2); the canonical walk
  gained `WalkPendingChoice` appended after `Mulligan1Done`. The null case writes exactly one
  trailing present-flag byte (`0x00`), shifting every digest uniformly. Event stream unchanged
  (verify: only the `stateHash` line moved in this fixture).
- **Why the new golden is correct:** the replay behavior is byte-for-byte identical up to the new
  trailing flag byte; the shift is the single budgeted rebaseline episode of
  `engineering/a2-serializer-design.md §4.2`. Old → new:
  `5878f6713439f54f850f51b4c56c98aab5bc93658a2e45afa8d46c232343a7d7` →
  `4f664233c6b30d70b7435ba057a17fdcb2686e288a3dd81cdf472f6ab4e468de`.
- **Link to the design/eng change that caused it:** `engineering/a2-serializer-design.md §4.2`,
  §7 convene record `engineering/a2b-scoped-review-2026-07-15.md`, M2-KICKOFF §6 decision 3.
- **cardSetSnapshot changed?** no.
- **engineContractVersion changed?** yes — `interfaces.v2` → `interfaces.v3` (hash-canonical
  Contracts change; the tag is the Contracts serialization-schema counter per the §7 ruling item 6).
