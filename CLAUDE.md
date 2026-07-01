# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of
Blizzard's *Hearthstone*. Fast to learn, deep to master, readable on one screen,
built for live-service content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules
engine that runs headless on the server and on the client for prediction.
Desktop-first, architected for mobile.

## Current status

**M1 — vertical slice, Phase C (Unity presentation) in progress.** The repo is
design/engineering specs in markdown **plus** real game code: the deterministic
rules core under `src/` and `tests/` (solution `Emberfall.slnx`, .NET 10 — note
`.slnx` not `.sln`), and a Unity project under `unity/Emberfall/`. Specs are
still the source of truth for un-built behavior; the studio of specialist agents
(directed by **Project Shepherd**) continues to produce them.

**Built — Phase A (engine steps 1–5):** the UnityEngine-free, zero-float
`Emberfall.Core.Contracts`/`.Cards`/`.Core` assemblies, splitmix64
`DeterministicRandom`, canonical float-free `GameStateHasher`, the **green R1
golden-replay gate**, and the 9-stage CI pipeline (`.github/workflows/ci.yml`,
5 hard-blocking pure-.NET stages).

**Built — Phase B (engine steps 1–5, all complete):** the effect interpreter,
combat resolution (`DamagePipeline`, keywords: Taunt/Charge/Rush/Windfury/
Overwhelm/Vanguard/Stealth/Divine Shield/Frozen), hero power, ~20 card
behaviors + JSON data rows, and the `deathrattle_cascade` golden fixture.
`CommandProcessor` (`src/Emberfall.Core/CommandProcessor.cs`) now fully
validates+applies `PlayCard`/`Attack`/`HeroPower`; only `DiscoverChoiceIntent`
remains `NotImplemented`. `dotnet test Emberfall.slnx` → 88 green.

**Built — Phase C (in progress):** step 1, the Unity project scaffold (UPM
packages, Session/Data/AI/Presentation layers, `GameBootstrap`, `MatchRunner`
wired to `LocalMatchHost` + `IIntentSource`s), the C# shader-binding layer
(`CardMaterialBinder`, `ShaderIds`), `ManaTrayView`, and all 10 Shader Graph
assets + 3 HLSL sub-graphs in `unity/Emberfall/Assets/Shaders/` (closing the
shader workstream in `techart/shaders/`).

See `src/README.md` for the engine map.

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
