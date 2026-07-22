# evidence/

**Tracked, permanent gate evidence. This directory is deliberately NOT `artifacts/`.**

`artifacts/` is gitignored on purpose — it holds CI output that is regenerated every run and
is worthless the moment the run ends. This directory is the opposite: files here record a
thing that happened once, at a time and against a build, and cannot be regenerated. They are
committed so a gate condition can be discharged against a file rather than against someone's
recollection.

Do not add this path to `.gitignore`. That mistake is the entire reason it exists as a
separate directory: on 2026-07-21 the C-G1-4 desync capture was produced correctly, landed in
`Application.persistentDataPath`, and was invisible to git — so a condition that was satisfied
in reality read as OPEN in the record. See `M2-G1-REVIEW.md` §3 C-G1-4 and its §5 delta note.

Keep it small. Evidence, not logs.

## desync/

Client-side desync captures written by `NetworkedGameBootstrap` via
`ClientOptions.OnDesyncCapture` when a received snapshot fails `VerifyIntegrity`. Each capture
is a pair:

- `*.txt` — the header: match id, seat, state version, envelope seq, wire vs local hash
- `*.frame` — the raw wire bytes of the frame that failed, for offline replay

Naming: `desync_<matchId>_s<seat>_v<stateVersion>_e<envelopeSeq>.{txt,frame}`

### `desync_d9ee43cdf52e4a2486d1c03a8d3f092a_s1_v11_e31`

**2026-07-21 — C-G1-4 discharge evidence. Synthetic, not a real engine divergence.**

Produced by `DesyncFaultInjector` (compile-guarded dev tool, `UNITY_EDITOR ||
DEVELOPMENT_BUILD`) flipping a single byte on the receive path. The hashes show it plainly —
they are identical from the second byte onward:

```
wireHash  = acbf6a8487bb7894fe3f304eee8f3fb897d64e5db7cd7993e3a7aac2346086c9
localHash = 5cbf6a8487bb7894fe3f304eee8f3fb897d64e5db7cd7993e3a7aac2346086c9
            ^^
```

This is the fault-injection half of C-G1-4's runtime verification. The other half — a clean
live match against the droplet, hashing green with the same instrumentation active — leaves no
artifact by construction, because nothing failed.

**Also relevant to the open resync bug.** This capture is `stateVersion = 11`, the same version
whose re-send never arrived after the injected mismatch, leaving both clients soft-locked with
the connection alive on heartbeats. It is the on-disk record of that moment, not merely a
checkbox.
