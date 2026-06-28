# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of
Blizzard's *Hearthstone*. Fast to learn, deep to master, readable on one screen,
built for live-service content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules
engine that runs headless on the server and on the client for prediction.
Desktop-first, architected for mobile.

## Current status

**M1 — vertical slice, engine in progress.** The repo is design/engineering specs
in markdown **plus** the first real game code: the deterministic rules core under
`src/` and `tests/` (solution `Emberfall.slnx`, .NET 10 — note `.slnx` not `.sln`).
Specs are still the source of truth for un-built behavior; the studio of specialist
agents (directed by **Project Shepherd**) continues to produce them.

**Built (M1 engine steps 1–5, branch `feat/emberfall-core-determinism`):** the
UnityEngine-free, zero-float `Emberfall.Core.Contracts`/`.Cards`/`.Core` assemblies,
splitmix64 `DeterministicRandom`, canonical float-free `GameStateHasher`, the **green
R1 golden-replay gate**, and the 9-stage CI pipeline (`.github/workflows/ci.yml`,
5 hard-blocking pure-.NET stages). `dotnet test Emberfall.slnx` → 25 green. See
`src/README.md` for the map.

**Deferred to M1 Phase B:** effect interpreter, combat, the ~20 card behaviors, and
the `deathrattle_cascade` golden fixture (`CommandProcessor` rejects PlayCard/Attack/
HeroPower with `NotImplemented` for now). Prior spec milestone: shader workstream
(10 shaders in `techart/shaders/`) + the mana-tray reconciliation.

## Repo map

- `PROJECT-PLAN.md` — charter, scope, engine decision, milestones
- `M1-KICKOFF.md` — milestone 1 kickoff
- `STAFFING-PROPOSAL.md`, `BRIEF-*.md` — studio staffing + role briefs
- `design/` — GDD, card schema, heroes, sample cards, economy, balance guardrails
- `engineering/` — ARCHITECTURE, card-system, game-state-and-actions, interfaces
- `multiplayer/` — match lifecycle, netcode architecture
- `techart/` — pipeline, card rendering, board/VFX, frame/rarity, perf budget,
  `shaders/` (per-shader specs, `SG_*` naming)
- `tooling/` — card authoring tool, pipeline automation, validation/import
- `ui/` — design system, UX flows

## Conventions

- Specs are the source of truth until code exists. When specs disagree, the
  **Software Architect** ruling in `engineering/interfaces.md` wins; reconcile
  downstream specs to it rather than forking behavior.
- Shaders use the `SG_<Name>` naming convention.
- Keep cross-references explicit (e.g. "see `interfaces.md §5.3`") when one
  spec depends on another.

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool.

- Product ideas/brainstorming → /office-hours
- Strategy/scope → /plan-ceo-review
- Architecture → /plan-eng-review
- Design system/plan review → /design-consultation or /plan-design-review
- Full review pipeline → /autoplan
- Bugs/errors → /investigate
- QA/testing → /qa or /qa-only
- Code review/diff check → /review
- Ship/deploy/PR → /ship or /land-and-deploy
- Save progress → /context-save
- Resume context → /context-restore
- Author a backlog-ready spec/issue → /spec
