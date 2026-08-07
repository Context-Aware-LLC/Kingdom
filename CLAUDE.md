# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of Blizzard's
*Hearthstone*. Fast to learn, deep to master, readable on one screen, built for live-service
content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules engine that runs
headless on the server. **There is no client-side prediction** (ADR-0004): the client mirrors
server snapshots wholesale and never speculates. Mobile-first, architected for Desktop

## What this file is

Constraints you **cannot infer by looking**. Everything below documents an *absence* or a
*consequence* — a trigger that parses but never fires, a rename that relocates every save —
because those are invisible on inspection and expensive to learn by hitting them.

**What is built, what exists, and how many of anything there are is deliberately NOT recorded
here.** Read the code, the data and the tests; they cannot drift from themselves. Do not add a
status summary, a file inventory, a directory map, or a count to this file — each one can only
become wrong, and this file is loaded into every session that touches the repo.

## Where things are

`src/` and `tests/` hold the deterministic rules core (solution `Emberfall.slnx` — note `.slnx`,
not `.sln`). `unity/Emberfall/` is the Unity project. `art/` and `art-bakeoff/` hold source art and
generated candidates. `tooling/scripts/` and `tools/ci/` hold automation. See `src/README.md` for
the engine map.

**The core is `netstandard2.1`, NOT .NET 10.** Every `Emberfall.Core.*` project, plus
`Emberfall.Net.Client` and `Emberfall.Net.Protocol`, targets `netstandard2.1` because **Unity
consumes them as local packages** and cannot load a `net10.0` assembly. Only `Emberfall.Net.Server`
and the `Emberfall.Tools.*` projects are `net10.0`. Reaching for a .NET 10 API or a newer C#
language feature inside the core compiles nowhere and breaks Unity's consumption of it — so check
the `.csproj` before using anything modern. Confirm with:
`git grep -h '<TargetFramework>' -- 'src/**/*.csproj' | sort | uniq -c`

## Source of truth

- **Your task brief is the source of truth.** For un-built behavior, ask the operator rather
  than inferring a decision from a document.
- **Specs and briefs are yours to read. Plans, trackers, status registers and records belong
  to the operator** — they are not instructions to you and do not outrank your brief.
- **A count, total or status figure in any document is a RECORD of what existed when it was
  written — never a target.** Never work backwards from one to decide how much to produce.
- **A disagreement between your brief and a document is a FINDING, not a problem to quietly
  solve.** Raise it and stop that track. Silently reconciling the two is the most damaging
  thing you can do, because the reconciliation never appears in your output.
- **Any deliverable that is a count, a diff, or an exhaustive check must be produced by a
  script whose output you quote** — never by reading and summarising. See the hard rule below.

## HARD RULE: quote the output, do not describe it

**If you ran something to get an answer, paste what it printed.** The script is not the
deliverable. Its stdout is the deliverable. Copy it in verbatim, including the parts that are
inconvenient.

**Never state a number, a count, or a "none found" that you did not read off a command's
output in the same session.** "No duplicates found" and "all entries are valid" are the most
dangerous sentences you can write, because they look like verification and cost nothing to
fabricate.

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
**after** the thaw/mana reset and **before** the draw, so a start-of-turn effect sees an unfrosted
board with mana already refilled, and a lethal start-of-turn cascade ends the match instead of
drawing into a finished game. A resolved spell creates a carrierless ongoing observer for an
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
(`Project Docs\Emberfall\docs\dev-docs\quality\security.md`). Both are a few lines, so the scan takes
seconds and a match is obvious. Match one, read that `VULN-nnn` in full, and follow its binding rules.
This index is a specification, not a tracker. (It was a numbered `§4` table before the 2026-07-31 docs
reorganisation; the sections are named now.)

**Reciprocal:** a `LATENT` entry must also leave a pointer comment on the line an implementer is
forced to edit to arm it. The entry records the vulnerability; only the comment delivers it —
because the person who arms a latent vulnerability is never the person looking for it.

## Every claim here that can expire carries a RETIREMENT TRIGGER in the code

Generalised from the reciprocal rule above, after a rule in this file outlived its reason and cost
a session (`art-bakeoff/` was the only copy of the art until the finals were placed; nothing
prompted anyone to retire the rule once they were).

**A statement about the current state of the world is not documentation — it is a claim with an
expiry date, and the expiry has to be attached to the work that causes it.** Writing "retire this
when X lands" *here* does not work: the person who lands X is never the person reading this file.

So: if you write a constraint in this file that some future change will falsify, you must also
leave a `RETIREMENT TRIGGER` comment **on the line that change is forced to touch**, naming what
to update. Conversely — **if you are editing a line that carries one, discharging it is part of
your change, not follow-up work.** The commit that falsifies the claim is the commit that fixes it.

Placed so far: the `Trigger` enum (`Enums.cs`) and the per-effect loop in `CardValidator`.

**A claim with no retirement trigger and no expiry is fine.** Most of this file is that — a hazard
that is simply true. Only pin the ones that a known, named change will invalidate.

## Generated art lands in `art-pipeline/`

**`art-pipeline/` superseded `art-bakeoff/` (Sam, 2026-08-06).** `art-bakeoff/` is gone from disk
entirely; only its `.gitignore` line and about twenty **legacy one-off generators** (card frames,
card backs, hero columns, versus plates) still name it. The **card-art pipeline** —
`gen_card_art.py`, `gen_card_prompt_briefs.py`, `card_art_editor.py`, `Run-ArtBakeoff.ps1` — writes
to `art-pipeline/`. Everything below is about the card-art pipeline. Both folders are gitignored.

**The default path is candidates-first.** `gen_card_art.py` drops candidates into
`art-pipeline/card-art/<group>/<art_file>/cand_<provider>_N.png` with a per-class contact sheet, and
a human picks before anything is promoted. Drafted briefs sit beside them as `draft.txt`, so art you
like has its prompt next to it.

**`<group>` is NOT reliably the `setId`.** It comes from `OUT_GROUP` keyed by card *class*, falling
back to `setId` only when the class is unlisted. **Resolving a candidate into a project path is the
importer's job**, and the folder a candidate sits in does not tell you where it belongs.

**Direct-to-Unity generation is in play (Sam, 2026-08-06)** — for the current run, generation writes
straight into `Assets/Art/Cards/<setId>/` and skips the candidates step. **The hazard this creates:
there is no candidate to compare against, so a re-roll overwrites promoted art in place and the
previous image is gone.** Overwrite-in-place is still the right mechanic for the `.meta` guid (below),
but the candidates folder was the thing preventing a re-roll from destroying the image it replaced.
Confirm with Sam before re-rolling a card that already has promoted art.
RETIREMENT TRIGGER: `gen_card_art.py` carries the marker; discharge this paragraph when the run ends.

**The destination is keyed by the card's `setId`, not by the batch or the candidate group:**
`Assets/Art/Cards/<setId>/art_<cardId>.png`, where the filename is the row's `artId` with `/`
flattened to `_`. `CardArtPostprocessor` keys import settings off `Assets/Art/Cards/<setId>/`, so
a file in the wrong set folder **imports with the wrong settings and nothing complains.** Read
the row's `setId` before copying — it does not always match the batch folder it sits in.

**Replacing existing art: overwrite the `.png` in place. Never delete the `.png` + `.meta`
pair.** The `.meta` carries the guid the Addressables entry points at; deleting it mints a new
one and silently orphans that entry. Overwriting keeps it. New art still needs its generated
`.meta` committed — a separate and easy-to-forget step.

**`art-pipeline/` is not load-bearing, and it is fully untracked (Sam, 2026-07-31, restated for the
renamed folder 2026-08-06).** Every final version already lives under `Assets/`, so the whole folder
could vanish and no build, test or asset would change. It is kept because it is good to look at.

**What that changes: nothing there is a deliverable, and losing a candidate is not an incident** —
do not treat it as recoverable state, do not audit it, do not raise a deletion as a finding.
**What it does not change: still don't delete it.** "Not precious" is not "please remove." It is
Sam's to clear when he wants the space. Deleting on his behalf remains outside your scope, and a
re-roll still lands *beside* the previous candidate rather than on top of it — overwriting a set
nobody has looked at yet is a different thing from clearing one he has.

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

**The Unity Editor is a single-writer resource.** One Editor, and one terminal designated as the
Unity terminal before work starts. A domain reload takes ~45 s, and every other session calling in
during that window is told *"Unity not detected"* — which reads like a broken relay rather than
contention, and sends people debugging the wrong thing. Two further non-obvious ones: *"connection
revoked"* means a second copy of `relay_win.exe` is running and must be killed, and compile errors
arrive over MCP typed as `"Log"`, not `"Error"`, so a filter on severity silently hides them.
