# Emberfall.Core — the deterministic rules core

This is the **`UnityEngine`-free, zero-float, server-authoritative rules engine** that the whole
architecture hangs off (`dev-docs/architecture/system-overview.md:5`). The identical DLLs run headless on the
dedicated server, in `dotnet test` CI, and (compiled to the same `netstandard2.1` target) inside the
Unity client. This drop is the **M1 determinism foundation** — steps 1–5 of the M1 engine plan:
firewall + RNG + canonical serialization + the R1 golden-replay gate + CI.

## Assemblies (the firewall layout — `dev-docs/architecture/system-overview.md:9`)

| Project | Target | UnityEngine? | Holds |
|---|---|:--:|---|
| `Emberfall.Core.Contracts` | netstandard2.1 | **No** | Enums, DTOs, `PlayerIntent` (+ `[ReplayTag]`), `GameEvent`, `CommandResult`, the `GameState` POCO graph, **`DeterministicRandom` (splitmix64)**, core interfaces. |
| `Emberfall.Core.Cards` | netstandard2.1 | **No** | `CardDefinition`, `EffectDef`, the single shared `CardJson` parser, `ICardDatabase`/`ICardValidator`. |
| `Emberfall.Core` | netstandard2.1 | **No** | `MatchEngine.BeginMatch` (core-driven opening), `CommandProcessor` (Validate→Apply→Emit turn loop), `LocalMatchHost`. |

Each project ships both a `.csproj` (for license-free `dotnet` CI) and a Unity `.asmdef` with
`noEngineReferences: true` (firewall layer 1). The `Emberfall.Core.Tests` project (`tests/`) holds the
headless determinism base: golden-replay (R1), the assembly-firewall + banned-API scans, contract, and
unit layers.

## The determinism contract (what's enforced here)

- **splitmix64 RNG, raw-state exposed and hashed** — the one entropy source; its cursor is part of the
  state hash so "right now, wrong future" desyncs are caught (`dev-docs/architecture/determinism-and-projection.md:16`).
- **Canonical, float-free `GameState` serialization** — fixed field order, little-endian via
  `BinaryPrimitives`, UTF-8 length-prefixed strings, the one `HashSet<Keyword>` imposed into a total
  order → SHA-256. Platform-independent by construction; CI bakes on Windows and asserts on Linux.
- **Golden-replay (R1 gate)** — `(seed, intent-log)` → identical final-state hash **and** ordered event
  stream, with first-divergence failure reports. `tests/.../GoldenReplay/`.
- **Assembly firewall + banned-API** — reflection scan for zero `UnityEngine` refs + no float-in-state.

## Build & test

```bash
dotnet build Emberfall.slnx -c Release
dotnet test  Emberfall.slnx -c Release           # all layers
dotnet test  Emberfall.slnx --filter Category=GoldenReplay   # just the R1 gate
EMBERFALL_REBASELINE=<name> dotnet test --filter Category=GoldenReplay   # deliberate re-bake
```

## What's intentionally NOT here yet (M1 Phase B)

The **effect interpreter, combat, and the ~20 card behaviors** (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M1-KICKOFF.md §3 Phase B`) — and with
them the `deathrattle_cascade` golden fixture (`hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7`). The processor rejects
`PlayCard`/`Attack`/`HeroPower` with `NotImplemented` until that lands. This drop deliberately scopes to
the determinism spine so the R1 gate and CI firewall are green from commit one, per the kickoff's
"front-load R1" mandate. `IMatchHost.DeltaSince`/`StateDelta` (reconnection) are likewise M2.
