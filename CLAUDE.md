# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** 

Inspired by Blizzard's *Hearthstone*. Fast to learn, deep to master, readable on one screen, built for live-service content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules engine that runs headless on the server. 
**There is no client-side prediction** (ADR-0004): the client mirrors server snapshots wholesale and never speculates. Mobile-first, architected for Desktop

## What this file is

Constraints you **cannot infer by looking**.
Everything below documents an *absence* or a *consequence* — a trigger that parses but never fires, a rename that relocates every save — because those are invisible on inspection and expensive to learn by hitting them.

**What is built, what exists, and how many of anything there are is deliberately NOT recorded here.**
Read the code, the data and the tests; they cannot drift from themselves. Do not add a status summary, a file inventory, a directory map, or a count to this file — each one can only become wrong, and this file is loaded into every session that touches the repo.

## Where things are

`src/` and `tests/` hold the deterministic rules core (solution `Emberfall.slnx` — note `.slnx`, not `.sln`). `unity/Emberfall/` is the Unity project.
`art/` and `art-pipeline/` hold source art and
generated candidates.
`tooling/scripts/` and `tools/ci/` hold automation.
See `src/README.md` for the engine map.

**The core is `netstandard2.1`, NOT .NET 10.**
Every `Emberfall.Core.*` project, plus `Emberfall.Net.Client` and `Emberfall.Net.Protocol`, targets `netstandard2.1` because:
**Unity consumes them as local packages** and cannot load a `net10.0` assembly. Only `Emberfall.Net.Server`
and the `Emberfall.Tools.*` projects are `net10.0`. Reaching for a .NET 10 API or a newer C#
language feature inside the core compiles nowhere and breaks Unity's consumption of it — so check
the `.csproj` before using anything modern. Confirm with:
`git grep -h '<TargetFramework>' -- 'src/**/*.csproj' | sort | uniq -c`

**A friend assembly declared in a `.csproj` does not exist as far as Unity is concerned.** Every
`src/` project is built by two toolchains: dotnet reads the `.csproj`, Unity reads the `.asmdef`
and never opens the `.csproj` at all. An `.asmdef` has no friend mechanism, so
`<InternalsVisibleTo>` items are silently dropped — **`dotnet build` and CI stay green while
Unity cannot compile the consumer.** Declare friends as `[assembly: InternalsVisibleTo(...)]` in
a `.cs` file, which both toolchains compile. This cost a session on 2026-08-06: an `internal`
field read across two assemblies took down every Unity assembly downstream of it, surfacing as a
Burst *"Failed to resolve assembly: 'Emberfall.AI'"* and a vanished `Emberfall/` menu — neither of
which points at an access modifier. The live declarations are in
`src/Emberfall.Core.Contracts/AssemblyInfo.cs`; that file carries the retirement trigger.

## Source of truth

- **Your task brief is the source of truth.** For un-built behavior, ask the operator rather than inferring a decision from a document.
- **Specs and briefs are yours to read. Plans, trackers, status registers and records belong to the operator** — they are not instructions to you and do not outrank your brief.
- **A count, total or status figure in any document is a RECORD of what existed when it was written — never a target.** Never work backwards from one to decide how much to produce.
- **A disagreement between your brief and a document is a FINDING, not a problem to quietly solve.** Raise it and stop that track. Silently reconciling the two is the most damaging thing you can do, because the reconciliation never appears in your output.
- **Any deliverable that is a count, a diff, or an exhaustive check must be produced by a script whose output you quote** — never by reading and summarising. See the hard rule below.

## HARD RULE: quote the output, do not describe it

**If you ran something to get an answer, paste what it printed.** The script is not the deliverable. Its stdout is the deliverable. Copy it in verbatim, including the parts that are inconvenient.

**Never state a number, a count, or a "none found" that you did not read off a command's output in the same session.** "No duplicates found" and "all entries are valid" are the most dangerous sentences you can write, because they look like verification and cost nothing to fabricate.

**Why this is a hard rule and not an obvious one.** On 2026-07-29 an agent was asked to
inventory the card data. It wrote `tooling/scripts/card_inventory.py` — a correct script, still
in this repo, which prints 143/42/73 for a total of 258. It then reported 114/47/72 for a total
of 233, in a formatted table with bold totals, contradicted its own per-class breakdown, closed
with two confident all-clears over data it had never read, and marked the issue **done**. The
run recorded **succeeded**, exit 0.

It had the right answer in front of it and typed a different one. Having the tool is not the
safeguard. **Reading its output is the safeguard.**

## Continuous AURA

**`AURA` is a continuous derived-stat recompute, not a queued event.** It is currently limited
to unsilenced board minions with `ALWAYS` + `SOURCE_BOUND` `MODIFY_STATS` rules targeting an
automatic minion set (`SELF`, friendly/enemy/all minions, or adjacent). Player/random choices,
filters, counts, bindings, non-minion targets, keyword grants, and every other source/action
shape fail closed in `AuraCapabilities` and `CardValidator`.

**`ON_HEAL` is a SELF trigger on the healed minion, and minion-only** (EF-015) —
`DamagePipeline.ApplyHeal` raises it with `RaiseSelf`, mirroring `ON_DAMAGED` at the sibling site. It
means "whenever *this* minion is healed", never "whenever anything is healed"; a card that reacts to
somebody else being healed cannot be expressed today. Two silences are deliberate and both are load-
bearing: an **overheal raises nothing** (a target already at max restores nothing, so nothing was
healed — the same "the no-op is not the event" precedent as an Aegis-absorbed hit), and a **hero heal
raises nothing**, because heroes carry no card effects. That second one is why the reentrant LIFEDRAIN
heal inside `ApplyDamage` — the path the trigger was expected to fire on — fires it on nobody: lifedrain
always heals the source's own *hero*. Dead on `SPELL`/`WEAPON` permanently, not pending a lifecycle:
neither has health to restore.

**`ON_TURN_START` is live for board minions, equipped weapons, and resolved ongoing spell effects
with an explicit turn-owner condition** (EF-011 / NOR-31).
Every such effect must declare `condition: {type: TURN_OWNER, value: CONTROLLER|OPPONENT|ANY}`;
missing ownership fails validation and runtime fails closed. It is raised in `StartTurnSequence`
**after** the mana reset and **before** the draw, so a start-of-turn effect sees a board with mana
already refilled, and a lethal start-of-turn cascade ends the match instead of drawing into a
finished game. **The board it sees may still be frosted** — since NOR-355 the thaw runs at the
END of the frozen player's turn in `ApplyEndTurn`, not at the start of the next one. A resolved spell creates a carrierless ongoing observer for an
explicit `ONE_SHOT`, `FINITE`, or `PERMANENT` future rule; ordinary hand, deck, and graveyard cards
never observe.

**`ON_TURN_END` fires on BOTH players' turn ends** (EF-012) — it is an observer sweep over every
permanent on both boards, not "at the end of *your* turn". A card that should only fire on its
controller's turn end cannot be expressed today.

**`ON_SPELL_CAST` fires on BOTH players' casts and NOT on hero powers** (EF-016) — it is an
observer sweep over both boards, so it means "whenever *any* spell is cast", not "whenever *you*
cast". It is raised only from `ApplyPlayCard`'s `CardType.SPELL` arm; a hero power raises the same
`ON_CAST` through a carrierless `HERO_POWER` source context and deliberately does not fire this. **Subscribers
are snapshotted at cast time**, so a minion the spell itself kills still reacts — that is
`RaiseObservers`' shape, shared with `ON_FRIENDLY_SUMMON` and `ON_TURN_END`.

**`ON_ATTACK` is the trap** — it is raised for **weapons only**, so it is *valid* on a weapon
and *dead* on a minion. A membership check alone will not catch that.

**`ON_DEATH` fires on a weapon as well as a minion** (EF-020) — `RulesEngine.BreakWeapon` raises it
on **durability exhaustion** and on **equip-replacement**. The non-obvious half: it *enqueues* like a
minion's, so on a replacement the **new weapon is already equipped** when the old one's rattle
resolves — an `ON_DEATH` that equips will break the replacement, not itself.

Lift a name from this list only when its raise site actually exists. `CardValidator`
rule **C21** now enforces this (EF-021): it rejects a dead trigger (`C21-TRIGGER-DEAD`) and
rejects source types without a wired lifecycle. The source of truth is the shared
`EffectSourceCapabilities` relation — **assign the exact source/trigger pair in the same commit
that lands its raise path**.

**Liveness is per source type, never per trigger name — generalise the `ON_ATTACK` trap above.**
Every member of the `Trigger` enum is tabled against at least one source kind, so
`EffectSourceCapabilities.SupportsAny` is true for all of them and **`C21-TRIGGER-DEAD` is
currently unreachable**: it can only fire for an enum member added without a table entry. The
rejection an author actually meets is source-scoped — `C21-TRIGGER-WEAPON-ONLY`,
`C21-TRIGGER-MINION-ONLY`, `C21-TRIGGER-SOURCE-UNSUPPORTED`. Reading "the trigger is live" as
"live on my card type" is the mistake this rule exists to catch. The enum's own retirement
trigger covers this paragraph: adding a trigger is what makes the dead branch reachable again.
Do not promote a type-scoped trigger to Live until every accepted source type has a runtime path.
This prose list is no longer the only guard, but keep it in sync with the table.

**This list does not live here alone.** Each member of the `Trigger` enum in
`Emberfall.Core.Contracts/Enums.cs` is marked with its exact liveness on its own line, with the
intended raise site and the retirement trigger. That is the copy an implementer actually meets.
If the two ever disagree, **the enum markers win** — they sit next to the code and this file does
not.

## What a weapon can and cannot do

A weapon is the original `CardInstance` moved from hand into `Zone.Weapon`; it keeps that identity
until `BreakWeapon` moves the same object to the graveyard. Its keywords, enchantments and current
durability are ordinary card state and survive those public-zone transitions.

Weapon attacks carry `LIFEDRAIN`, `VENOM`, `FROST`, and `DOUBLESTRIKE`. The first three enter the
existing fixed damage-rider order; `DOUBLESTRIKE` raises the hero's attack budget to two.
`SILENCE`, `BACKDRAFT`, and `REINFORCE` remain enum slots only for wire/hash ordinal stability:
their behavior and generated markers come from typed actions, so they are illegal as carrier or
granted keywords. `CardValidator`'s fail-closed weapon-carrier table rejects every other keyword
with no weapon behavior; weapon-ness alone is not a rejection reason.

A weapon's expressive surface includes **stats**, **`ON_PLAY`** (equip herald), **`ON_ATTACK` with
a static selector** — that trigger fires with an empty target list, so "the minion this hits" is not
addressable — **`ON_DEATH`** (EF-020), and persistent observer rules such as `ON_TURN_START`,
`ON_TURN_END`, `ON_FRIENDLY_SUMMON`, and `ON_SPELL_CAST`. Observer ordering is board minions
left-to-right, then the equipped weapon, for the incoming/active side before the other side.

**A weapon toll resolves with the weapon already in its controller's graveyard**, and on an
equip-replacement with the *replacement* already in the slot. A `SELF`-selector effect on one
therefore targets nothing: a weapon is not in `Zone.Board`.

Changing a weapon's runtime shape is **hash-canonical** because `PlayerState.Weapon` is game state.
Update the canonical serializer, projection, snapshot codec, shared field/mutation manifest, pinned
digest, and affected golden fixtures under the rebaseline guard in one reviewed change.

## Player-facing input consults the vulnerability index

**Before implementing anything that accepts client-authored input, scan the `Review triggers` and
`Latent vulnerabilities` sections of the security index**
(`docs\dev-docs\quality\security.md`). Both are a few lines, so the scan takes
seconds and a match is obvious. Match one, read that `VULN-nnn` in full, and follow its binding rules.
This index is a specification, not a tracker. (It was a numbered `§4` table before the 2026-07-31 docs
reorganisation; the sections are named now.)

**Reciprocal:** a `LATENT` entry must also leave a pointer comment on the line an implementer is
forced to edit to arm it. The entry records the vulnerability; only the comment delivers it —
because the person who arms a latent vulnerability is never the person looking for it.

## Every claim here that can expire carries a RETIREMENT TRIGGER in the code

Generalised from the reciprocal rule above, after a rule in this file outlived its reason and cost
a session (`art-pipeline/` was the only copy of the art until the finals were placed; nothing
prompted anyone to retire the rule once they were).

**A statement about the current state of the world is not documentation — it is a claim with an
expiry date, and the expiry has to be attached to the work that causes it.** Writing "retire this
when X lands" *here* does not work: the person who lands X is never the person reading this file.

So: if you write a constraint in this file that some future change will falsify, you must also
leave a `RETIREMENT TRIGGER` comment **on the line that change is forced to touch**, naming what
to update. Conversely — **if you are editing a line that carries one, discharging it is part of
your change, not follow-up work.** The commit that falsifies the claim is the commit that fixes it.

**Do not list the placed markers here — a list of them is itself a record that drifts.** One went
stale within days of being written, naming `CardValidator`, which never carried a marker at all.
Ask the repo instead, and note that they are spread across the engine, the Unity presentation layer
and `tooling/`, not just `src/`:

```
git grep -n "RETIREMENT TRIGGER" -- src tooling tools unity
```

**A claim with no retirement trigger and no expiry is fine.** Most of this file is that — a hazard
that is simply true. Only pin the ones that a known, named change will invalidate.

## Generated art lands in `art-pipeline/`
`art-pipeline/` is gitignored in full intentionally

**The default path is candidates-first.**
`gen_card_art.py` drops candidates into `art-pipeline/card-art/<setId>/<art_file>/cand_<provider>_N.png` with a contact sheet per (setId, class), and a human picks before anything is promoted. Drafted briefs sit beside them as `draft.txt`, so art you like has its prompt next to it.

**Candidates and drafts share one key: the `setId`.** A class-keyed candidate group used to live
here, which put a card's `draft.txt` and its candidates in different directories for 92 of 260
cards — the adjacency the candidates folder exists to provide, silently absent. Retired 2026-08-06.

**Expansion at the directory level, class at the filename level (Sam, 2026-08-06):**
`Assets/Art/Cards/<setId>/<classToken>[_<role>]_<name>.png`, and `<setId>/[<role>_]<name>.png` for
NEUTRAL — **the absence of a class token is what means neutral.** `<role>` is `hero_power` or
`token`. Class tokens are abbreviations that do not match their class names: STONEWARDEN is
`stone`, TEMPEST is `storm`. **An `artId` IS the filename** — there is no `art/` namespace and no
`art_` prefix; both were dropped because the namespace had one value across all 260 rows and so
distinguished nothing. `ArtIds.ToFileName` stays a `Replace('/','_')` against a future namespaced
id, but nothing today has a separator to flatten.

**CORE is hero powers only — in the DATA as well as the art (Sam, 2026-08-15; this replaces the
2026-08-08 rule, which was art-only and is now retired).** A card is `setId: CORE` if and only if
it is a hero power. Nothing else may carry it.

Under the old rule a card could be data-tagged `setId: CORE` while its art shipped under
`Assets/Art/Cards/EMBERFALL/` — one card, two set answers, and the only thing reconciling them was
a hard-coded exception in `tooling/scripts/audit_card_art_names.py`. On 2026-08-15 that split was
closed by moving the data to match the art: 24 non-hero-power rows went `CORE` -> `EMBERFALL`, and
their baked `CardDefinitionSO` assets moved from `Assets/Cards/CORE/` to `Assets/Cards/EMBERFALL/`.
`Assets/Cards/CORE/` now holds hero powers only. **If you are here to add that exception back to
`audit_card_art_names.py`, don't — check whether it is still reachable first.**

The same ruling retired the `core_` id prefix. An id names its owner: `neutral_` for a NEUTRAL
card, the class token otherwise. `core_` survives on no card, and a new one is a mistake — the
prefix said "belongs to the CORE set", which is now true only of hero powers, and those are
already prefixed `hero_power_`.

**RETIREMENT TRIGGER:** if a non-hero-power card is ever legitimately tagged `setId: CORE` again,
this paragraph and the `Assets/Cards/CORE/`-is-hero-powers-only claim both die with it.

**The set folder decides which BUNDLE a file ships in, not its import settings.**
`CardArtPostprocessor` keys on `Assets/Art/Cards/` — the root, one rule for every set — so import
settings are identical wherever under it a file sits. What the `<setId>` folder does decide is
Addressables group membership: `AddressablesSetup` builds one `CardArt_<setId>` group per folder
found on disk. A file in the wrong set folder therefore **still renders correctly and still passes
CardCi** — the address is a flat `card_art/<fileName>` with no set in it — while shipping in the
wrong bundle. Nothing anywhere reports the mismatch between a row's `setId` and its art's folder;
`tooling/scripts/audit_card_art_names.py` is the only thing that looks. Read the row's `setId`
before copying.

**`Assets/Art/UI/` subfolders are the opposite — purely organisational.** `EditorUiSpriteProvider`
searches all of `Assets/Art/UI` recursively by sprite NAME, `AddressablesSetup` addresses that tree
as `ui/<fileName>`, and the import rule keys on `Assets/Art/UI/` itself. Renaming a subfolder under
it changes no address and no import setting. Do not reason from `Art/Cards/` to `Art/UI/`.

**Replacing existing art: overwrite the `.png` in place. Never delete the `.png` + `.meta` pair.**
The `.meta` carries the guid the Addressables entry points at; deleting it mints a new one and silently orphans that entry. Overwriting keeps it. New art still needs its generated `.meta` committed — a separate and easy-to-forget step.

**`art-pipeline/` is not load-bearing, and it is fully untracked (Sam, 2026-07-31, restated for the
renamed folder 2026-08-06).** Every final version already lives under `Assets/`, so the whole folder
could vanish and no build, test or asset would change. It is kept because it is good to look at.

**What that changes: nothing there is a deliverable, and losing a candidate is not an incident** —
do not treat it as recoverable state, do not audit it, do not raise a deletion as a finding.
**What it does not change: still don't delete it.**
"Not precious" is not "please remove." It is Sam's to clear when he wants the space. Deleting on his behalf remains outside your scope, and a re-roll still lands *beside* the previous candidate rather than on top of it — overwriting a set nobody has looked at yet is a different thing from clearing one he has.

## Never commit a changed `productName`

**Before committing `unity/Emberfall/ProjectSettings/ProjectSettings.asset`, `git diff` it and
confirm `productName` is still exactly `Emberfall`.**

`Application.persistentDataPath` is `AppData/LocalLow/<companyName>/<productName>`, so that one
string keys the player's **entire collection**. Rename the product and the game silently boots
against a different, empty collection root. Committing such a rename **permanently relocates the
save location for every build**: the real collection becomes unreachable and any existing
tester's collection orphans, with no error and no way for the player to tell what happened.

**This is not a typo risk — a tool does it to you.** Running a Unity **multiplayer scenario**
rewrites `productName` to its role name, so expect it after every scenario run.
**`Emberfall/Build/Restore Product Name` puts it back** (set through `PlayerSettings`, since
Unity holds project settings in memory and overwrites a file edited underneath it). The rename
is harmless while it stays in the working tree; only the commit is damaging.

## Conventions

- Shaders use the `SG_<Name>` naming convention.
- Keep cross-references explicit (e.g. "see `hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.3`") when one spec depends on
  another.

## Two things about this working copy that will cost you a day

**Stage every commit by explicit path. Never `git add -A`.** More than one session or worktree can
be live in this repo at once, so a broad add sweeps another session's in-progress hunks into your
commit. Run `git branch --show-current` first, `git add <explicit paths>` second, and read
`git status` afterwards to confirm what you deliberately left alone. In a project spanning two
repos, always `git -C <absolute path>` — a shell's working directory persists between commands, and
assuming otherwise has already caused one push to the wrong repo.

**Staging by explicit path is NOT sufficient — commit by explicit path too: `git commit --only
<paths>`.** A bare `git commit` commits *the whole index*, and in this working copy the index is
not yours alone: another session stages its own work there, and anything it left staged rides into
your commit no matter how carefully you added. Cost 2026-08-12 — a bot-roster commit that touched
eight server files shipped with another session's staged deletion of
`unity/.../Presentation/ClickableBoardSlot.cs` inside it, mid-rework, with nothing in the `git add`
output hinting at it. `git status` before committing shows the difference: `D ` in the **left**
column is someone else's staged change, ` D` in the right column is an unstaged one. **This is a
different trap from the hook one** documented in `.githooks/pre-commit` (a hook's `git add` defeats
even `--only`, via the temp index) — that one is about writes *during* the commit, this one is
about what was already sitting in the index before it. Both end with your name on someone else's
file.

**The Unity Editor is a single-writer resource — but only against `-batchmode`.** An open Editor
holds `unity/Emberfall/Temp/UnityLockfile`, and anything spawning its own Editor blocks on it. That
is what `tools/ci/unity-verify.ps1` does, so **the verify half of the gate still needs the Editor
closed** and still reports the lock as an error rather than clearing it. A domain reload takes ~45 s
and nothing else gets in during that window.

**But the eight checks themselves do not need a closed Editor — only the wrapper does.**
`HeadlessVerify.RunAll` is a loop over eight plain statics (`A5WireVerify.Run`,
`CardSetValidator.RunAndLog`, `UxmlIntegrityCheck.Check`, `CharacterRosterVerify.Check`,
`PlayerAccountTools.SelfCheck`, `AddressablesGuard.Check`, `DeviceLayoutVerify.Check`,
`InputGestureVerify.Run`); the *only* reason it needs a dedicated Editor is the
`EditorApplication.Exit()` on its last line. Call them yourself through `unity command eval` — same
`logMessageReceived` capture, no `Exit` — and you get the PASS/FAIL table against the Editor that is
already open. **Never `eval` `RunAll` itself: it will close the operator's Editor.**

This is evidence, not a substitute. The gate runs them `-batchmode -nographics` on a cold domain
after a forced reimport; an `eval` runs them in a warm graphical Editor, so a check that only fails
on a cold domain still slips through. Use it to keep moving while the Editor is open; still run the
real gate before a push. Verified 2026-08-21 (all eight PASS both ways).

**Drive the open Editor instead of spawning one, whenever there is one.** The Unity CLI
(`~/AppData/Local/Unity/bin/unity.exe`, not on PATH — call it by absolute path) talks to a running
Editor over the project's `com.unity.pipeline` package, so it takes no lock and contends with
nobody. `unity command menu --path "<menu item>"` replaces
`Unity.exe -batchmode -executeMethod`, and every command takes `--json`.

- **`unity status` lies when the Editor is busy.** Mid-compile it returns `STATUS_NO_INSTANCES` —
  *"Open Unity and install the Pipeline package"* — with the package installed and the Editor open,
  sending you to redo work you have already done. **`unity pipeline list` is the honest one**: it
  reports `hasPipelinePackage`, `isRunning`, and `pipelineServer.isReachable` separately, which is
  what actually distinguishes "not installed" from "still compiling".
- **Compile results are structured and correctly typed.** `unity command recompile_status` returns
  `failed` plus the error text, and `unity command console --level error` really does filter
  errors. Verified 2026-08-20 against a deliberate break, and it is why the old MCP hazard here —
  compile errors arriving typed `"Log"`, so a severity filter hid them — is gone rather than
  merely undocumented. **`console` is a `command` subcommand, not a top-level one** — bare
  `unity console` is `unknown command 'console'`, which reads like a missing feature rather
  than a wrong invocation (CLI 0.5.0-exp.1, 2026-08-21).
- **The Editor was not started with `-automated`**, and the Pipeline server says so on startup: a
  modal dialog can stall the command channel. Relaunch with `-automated` before anything that can
  raise one.
- **UNTESTED: Safe Mode.** A project that *boots* with compile errors drops the Editor into Safe
  Mode, where the Pipeline package does not load and the CLI cannot connect at all — it will look
  like a connection fault, not a compile fault. Breaking a file in an *already running* Editor does
  not reproduce this and is not evidence against it. If the CLI goes silent, run `unity pipeline
  list` and suspect Safe Mode before anything else.

**RETIREMENT TRIGGER:** the Unity MCP relay and its two skills were removed 2026-08-20 in favour of
the above; if an MCP path is ever reinstated, this section and `.claude/skills/` change together.

## Ask the graph before you grep

There is a persistent knowledge graph of this repo — ~4,600 nodes and ~25,000 edges over
csharp, sql, python, javascript, powershell, bash, c and cpp — built by `code-review-graph`.
It answers *where is this defined, what calls it, what does it call, which flow is it in*. Grep
answers *which files contain this string*. Reaching for the second when you wanted the first is
the default failure, because grep always looks like it worked.

**Its callers/callees stop at the assembly boundary, and it does not say so.** C# node keys are
path-scoped (`...\CardDefinition.cs::TargetSpec.IsKnown`) while `CALLS` targets are stored as
bare names (`FromSeconds`, `nameof`), so cross-project calls do not resolve: measured
2026-08-20, **edges joining `Emberfall.Net.Server` to `Emberfall.Core` numbered 0**, and 67% of
all edges point at a target that is not a node. Treat "Called by" as *within this project*, and
never as an exhaustive list. Everything derived from those edges inherits the hole —
`caller_count` claims 2327 symbols have none, `test_coverage` claims 16 of 3924 are tested, 92%
of `risk_score` is the 0.3 default, and **`code-review-graph dead-code` is therefore unusable**.
`tooling/scripts/graph_view.py` carries the numbers and a retirement trigger.

**You should not normally have to do anything.** `.claude/settings.json` registers a
`PreToolUse` hook on `Grep|Glob|Read|Bash` that runs `code-review-graph enrich`, which injects
the graph's answer ahead of the tool result. On a `Read` it returns every symbol in the file
with its callers, callees and flows. It costs about 0.9 s and it cannot block a tool call —
the `|| true` is load-bearing, because **a `PreToolUse` hook that exits 2 blocks the tool**, and
`timeout` returns 124 while a DB lock during a concurrent pre-commit `update` is a real
possibility.

**The hazard is that its absence is invisible.** The hook opens with
`command -v code-review-graph >/dev/null 2>&1 || exit 0`, so on any machine where the tool is
not installed — a fresh clone, a WSL agent checkout — you get no context, no warning and no
error. It looks exactly like a repo with nothing interesting to say about itself. **If you have
run several searches and never once seen a `[code-review-graph]` block, assume it is not
installed rather than that the graph is empty**, and check:

```
code-review-graph status
```

Judge that by **node count, not exit code**. A broken install — grammars invisible to its
`python -I` probe, which is what `pip install --user` produces — reports `449 files, 2 nodes,
10 edges` and exits 0. Install it as `uv tool install "code-review-graph[communities,embeddings]"`,
never with `pip --user`. The graph refreshes itself from `.githooks/pre-commit` on every commit,
and `built_at_commit` trailing HEAD by exactly one commit is correct — the hook runs before the
commit exists. Do not "fix" that lag.
