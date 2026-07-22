# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of
Blizzard's *Hearthstone*. Fast to learn, deep to master, readable on one screen,
built for live-service content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules
engine that runs headless on the server. **There is no client-side prediction**
(ADR-0004): the client mirrors server snapshots wholesale and never speculates.
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

**Planning docs are NOT in this repo.** They were federated out in `3c77296` and now
live at `C:\Users\User\Documents\Project Docs\Emberfall\docs\`, which is its own git
repo registered as the federated `emberfall-docs` gbrain source. This repo keeps code
and is registered as an unfederated source. Same convention across all three main
projects (Emberfall, Pocket Secretary, Time Clock): planning docs live under
`Documents\Project Docs\<Project>\docs\`, code repos keep code.

Docs source (`…\Project Docs\Emberfall\docs\`):

- `project/` — `PROJECT-PLAN.md` (charter, scope, milestones), `M1-KICKOFF.md`,
  `M1-CLOSEOUT.md`, `M2-KICKOFF.md`, `M2-G0-REVIEW.md`, `STAFFING-PROPOSAL.md`,
  `BRIEF-*.md`
- `decisions/` — `ADR-000N-*.md` + M2 decision memos
- `design/` — GDD, card schema, heroes, sample cards, economy, balance guardrails
- `engineering/` — ARCHITECTURE, card-system, game-state-and-actions, interfaces,
  per-workstream design docs, `spec-reconciliation-2026-07.md`
- `multiplayer/` — match lifecycle, netcode architecture
- `techart/` — pipeline, card rendering, board/VFX, frame/rarity, perf budget,
  `shaders/` (per-shader specs, `SG_*` naming)
- `ui/` — design system, UX flows
- `audio/`, `testing/`, `tooling/` — audio direction, test strategy, card authoring
  tool / pipeline automation / validation+import specs

This repo (code):

- `src/`, `tests/` — the deterministic rules core (.NET 10, `Emberfall.slnx`)
- `unity/Emberfall/` — the Unity project
- `art/`, `art-bakeoff/` — source art + bake-off outputs
- `deploy/` — droplet/server deploy scaffolding
- `tooling/scripts/`, `tools/ci/` — automation scripts and CI helpers
- `artifacts/` — generated run outputs (golden-replay results, scans)

## Bookkeeping is part of done (Sam + Claude, 2026-07-17)

**HARD RULE. There is no "done" until the record is updated.** If a change discharges a
condition, closes a workstream row, or supersedes a spec version, **the tracker moves in
the same change** — not next session, not "when we get to it." Landing work without
landing its bookkeeping is not a partial success; it is not done.

Trackers that must stay current:

- `M2-KICKOFF.md` §3 register — every row carries a **Status**. **Never overwrite the
  scope column**: scope is what we asked for, Status is what happened.
- `M2-G0-REVIEW.md` §5 discharge log — §1–§4 are gate-day minutes, left as written; §5
  is the live status.
- `engineering/spec-reconciliation-2026-07.md` — the drift tracker.

The pattern to copy is `engineering/b1-match-server-design.md`, whose landing sequence
lists the kickoff-row update as an explicit step. Every landing sequence should carry one.

**Why:** an overnight loop (~2026-07-15/16) discharged four G0 conditions correctly and
updated no tracker. Three days later an orientation pass read the stale docs and concluded
a spec was missing that was on disk, and that C-G0-5 was one miss from firing E4 when it
had never been missed. **A stale record is indistinguishable from a miss** — same
re-derivation cost, plus false alarms. The work was honest; the paper trail lied. This is
M1's skipped-gate failure mode in a new costume. See gbrain
`decisions/bookkeeping-is-part-of-done`.

**Autonomous loops:** not banned — this rule is what makes a ban unnecessary. But keep
loops out of **gate state and change control** (WS A1 / F1), where the artifact *is* the
record and an unattended error compounds. Spec'd implementation work with a real test is
fine to loop. Final ruling deferred to the **G1 panel**.

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
