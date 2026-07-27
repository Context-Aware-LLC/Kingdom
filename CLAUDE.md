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

**The rule fires on CONCLUSIONS, not just changes (Sam + Claude, 2026-07-23).** A question
answered — by a test, by a code read, or by watching it happen — moves the tracker **the
moment it is answered**, never at end of session. The clause above attaches bookkeeping to a
*change*, so a conclusion with no diff to ride on slips through it entirely, and the only
remaining vessel is the handoff — which a frozen session eats, silently and through nobody's
fault.

**Why:** C-G1-6's "reborn bug" was closed on 2026-07-20 by Sam playing a match live and
watching the minion behave correctly. Right answer, right method, reached by the only person
who could reach it — but no commit to hang it on, so it landed nowhere. The row stayed
`⬜ OPEN` with an empty notes cell, and on 2026-07-23 most of a session went into re-deriving
it by inspection across four hypotheses. **Note which record survived:** the bisect test is
still in git, the panel's reasoning is still in the review — the human observation that
actually settled it is the one that evaporated. Observation is the most perishable record we
produce and often the most valuable. A green test writes itself into git; watching something
with your own eyes writes itself nowhere.

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

## Player-facing input consults the vulnerability index (Sam + Claude, 2026-07-23)

**HARD RULE. Before implementing anything that accepts client-authored input, scan the index:**
`…\Project Docs\Emberfall\docs\security\README.md` §4. It is a trigger-keyed table — a few
words per row — so the scan is seconds and a matching row is obvious. Match a row, read that
`VULN-nnn` in full and follow its binding rules.

**Reciprocal:** finding a latent vulnerability isn't done at the entry. A `LATENT` entry MUST
also leave a pointer comment on the line an implementer is forced to edit to arm it — the entry
records it, only the comment delivers it. Same sense as *Bookkeeping is part of done*.

**Why:** the person who arms a latent vulnerability is never the person looking for it. VULN-001
arms whenever Discover gets built, by someone reasoning about game rules, while the damage lands
in the render layer on the *opponent's* machine.

## Six card triggers are NO-GO until implemented (Sam, 2026-07-26)

**HARD RULE. Do not author a card using any of these.** They are declared in
`Enums.cs`'s `Trigger` enum and **raised nowhere in the engine**. A card using one
parses, validates, loads, and then does nothing — forever, silently, with no error.

> `ON_TURN_START` · `ON_TURN_END` · `ON_DAMAGED` · `ON_HEAL` · `ON_SPELL_CAST` · `AURA`

**The five that work:** `ON_PLAY`, `ON_CAST`, `ON_DEATH`, `ON_FRIENDLY_SUMMON`, and
`ON_ATTACK`. **`ON_ATTACK` is the trap** — it is raised for **weapons only**
(`CommandProcessor.cs:512-518`), so it is *valid* on a weapon and *dead* on a minion.
A membership check alone will not catch that.

No shipped or drafted card currently uses a dead trigger (verified 2026-07-26 across
all 73 shipped rows + both `card-drafts-emberward-tidecaller*.json`). The risk is
entirely forward-looking: two batch-3 drafts hit it before being caught by hand.

Lift a name from this list only when its raise site actually exists. `CardValidator`
has no rule for this (proposed as C21 — reject unraised triggers, and reject
`ON_ATTACK` on a non-`WEAPON`); until it does, **this list is the only guard.**

### On-damage riders: what a keyword can and cannot ride (Sam + Claude, 2026-07-26)

1. **`Keyword.FREEZE` (Frost) works on MINIONS as of 2026-07-26.** `DamagePipeline`
   now honours three riders — `LIFESTEAL`, `POISONOUS`, `FREEZE` — in the fixed order
   *Aegis absorb → health/armor → Lifedrain → Venom → Frost*. Frost freezes **heroes
   as well as minions** (unlike Venom), does **not** fire behind an Aegis shield, and
   does **not** ride Overwhelm overflow (G0 R-2(c)). **Caveat: the visual does not
   exist.** `SG_Frost.shadergraph` is on disk but **`M_Frost.mat` was never authored**,
   so `CardMaterialBinder`'s `_FrostAmount` writes (`:123-129`) land nowhere. A Frost
   card is mechanically correct and visually silent until that material exists.
2. **A hero attacking with a weapon carries NO keywords at all.** `attackerKws` is a
   literal empty `HashSet` for a hero (`CommandProcessor.cs:495-497`), and
   `WeaponInstance` (`:340-346`) has no keyword field to read from. **So no weapon can
   have Lifedrain, Venom, or Frost as an on-hit rider** — not just Freeze, *any* rider.
   A weapon's only expressive surface today is stats + `ON_PLAY` (equip battlecry) +
   `ON_ATTACK` with a **static** selector (the trigger fires with an empty target list,
   so "the minion this hits" is not addressable).

(2) is **still a hard rule — do not author a weapon with any rider keyword.** Sam
2026-07-26: acknowledged as a ball drop, deliberately **not** today's work. Fixing it
is a **hash-canonical change** — `WeaponInstance` is game state, so a new `Keywords`
field must enter the serializer/hasher and the golden replays re-bake with the R1 gate
as witness (M2-KICKOFF tier b). Not a three-line patch, unlike (1), which needed no
hash change because `IsFrozen` was already serialized.

## Known and accepted: Gravekeep's hero power is strictly dominated (Sam, 2026-07-26)

`hero_power_toll_the_bell` (2 mana, Restore 2 to your hero) is strictly dominated by VERDANT's
`hero_power_quicken` (2 mana, Restore 3) — same cost, same verb, same target, less healing, no
compensating text.

**Not being fixed. Do not re-raise it as a finding.** It is a hero-identity change owned by
`heroes.md` §3, not a card data patch, and all balance tuning is halted until more classes are
deck-legal (`balance-survey-2026-07-26.md` §9d). Recorded so the next pass recognises it as a
known-accepted item rather than re-deriving it. The fix when it happens is most likely
`HEAL 2 → DRAW 1`, which restores the value identity `heroes.md` §3 describes instead of making
two class powers identical.

## The freeze cue: everything is wired, the shader draws nothing (2026-07-26)

**`SG_Frost` renders no pixels at any `_FrostAmount`, including 1.0.** That is the only thing left
between a frozen minion and a visible cue. Do not go looking for a wiring bug — the wiring is done
and verified (`ee4b4bb`):

- `CardLiveState:92` sets `AuraSet.Frozen` from `ci.IsFrozen` ✔
- `CardMaterialBinder` tweens `_FrostAmount` 0↔1 over 0.30s on that change ✔
- `CreatePresenter` builds a **`FrostOverlay`** quad wearing `M_Frost`, at z −0.008 — in front of
  the art and frame, behind every TMP label, so ice never covers the numbers ✔
- `M_Frost` → `Shader Graphs/SG_Frost`, 13 properties, `_FrostAmount` present ✔
- Four cards already freeze via the `FREEZE` **action** (`tide_frostbind`, `tide_siren`,
  `tide_deepfrost_spear`, `tide_hollowshell_warden`) ✔

**What remains is graph authoring.** The material has **no shader keywords enabled** —
`_FROST_REFRACTION` and `_QUALITY_DESKTOP` are both off — which is the leading suspect for an empty
output, unconfirmed. Someone has to open `SG_Frost.shadergraph`. The moment it draws anything, the
cue appears in a match with no further code.

**Verify with `Emberfall/Preview Card Frost (offscreen)`** → `artifacts/frost-preview/`. It sweeps
0 → 1 over a real card and drives the **real** overlay quad, so it fails loudly if the quad ever
stops being built. `Keyword.FREEZE` is still never read by `DamagePipeline`; freeze works only as
the `FREEZE` action (see the rider-gaps rule above).

## Generated art lands in `art-bakeoff/`; the importing session resolves where it belongs (Sam, 2026-07-26)

**Generation never writes into the Unity project.** Every generator drops candidates into
`art-bakeoff/card-art/<batch>/art_<cardId>/cand_*.png` — some folder or another, by batch, not by
where the asset will live. **Resolving that into a project path is the job of whoever does the
import, after the generation step.** Do not expect the generating session to have gotten it right;
it does not have the information.

**The destination is keyed by the card's `setId`, not by the batch:**
`Assets/Art/Cards/<setId>/art_<cardId>.png`, where the filename is the row's `artId` with `/`
flattened to `_`. `CardArtPostprocessor` keys import settings off `Assets/Art/Cards/<setId>/`, so a
file in the wrong set folder imports with the wrong settings **and nothing complains.** Read the
row's `setId` before copying — `ember_cinder_lance` is `CORE`, not `EMBERFALL`, despite sitting in
an EMBERFALL-era batch folder.

**Replacing existing art: overwrite the `.png` in place. Never delete the `.png` + `.meta` pair.**
The `.meta` carries the guid the Addressables entry points at; deleting it mints a new one and
silently orphans that entry. Overwriting keeps it. New art still needs its generated `.meta`
committed — that is a separate, easy-to-forget step.

**Never delete a candidate from `art-bakeoff/`** — it is keep-forever seed material and Sam curates
it himself.

## Never commit a changed `productName` (Sam + Claude, 2026-07-27)

**HARD RULE. Before committing `unity/Emberfall/ProjectSettings/ProjectSettings.asset`, `git diff` it
and confirm `productName` is still exactly `Emberfall`.**

`Application.persistentDataPath` is `AppData/LocalLow/<companyName>/<productName>`, so that one string
is what keys the player's **entire collection** — `profile.json`, `characters.json`, `decks.json` and
`accounts.json`. Rename the product and the game silently boots against a different, empty collection
root. Committing such a rename **permanently relocates the save location for every build**: the real
collection becomes unreachable and any existing tester's collection orphans, with no error and no way
for the player to tell what happened.

**This is not hypothetical, and it is not a typo risk — a tool does it to you.** Running a Unity
**multiplayer scenario** rewrites `productName` to `Emberfall (Client)` (its role name). On 2026-07-27
that sent both test clients to `Context Aware/Emberfall (Client)/`, where they minted a fresh account
and asked a returning player for his name. Expect it to happen again after every scenario run.

**`Emberfall/Build/Restore Product Name` puts it back** (set through `PlayerSettings`, since Unity
holds project settings in memory and overwrites a file edited underneath it). The rename itself is
harmless while it stays in the working tree; only the commit is damaging.

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
