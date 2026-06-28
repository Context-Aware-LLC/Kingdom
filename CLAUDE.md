# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of
Blizzard's *Hearthstone*. Fast to learn, deep to master, readable on one screen,
built for live-service content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules
engine that runs headless on the server and on the client for prediction.
Desktop-first, architected for mobile.

## Current status

**M0/M1 — specification phase.** This repo is currently **design + engineering
specs in markdown**, not yet game code. Work is produced by a simulated studio:
**Project Shepherd** directs specialist agents (Game Designer, Technical Artist,
Shader Graph Artist, Software Architect, UI Designer, and future hires).

Last completed: shader workstream (10 shaders in `techart/shaders/`) and a
mana-tray reconciliation across `engineering/interfaces.md §5.3`,
`ui/design-system.md §4.4`, and `techart/shaders/SG_ManaFill.md`. Natural next
hire was a Game Audio Engineer.

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
