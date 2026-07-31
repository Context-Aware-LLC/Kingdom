# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** in the lineage of Blizzard's
*Hearthstone*. Fast to learn, deep to master, readable on one screen, built for live-service
content cadence (card sets, balance patches, cosmetics).

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules engine that runs
headless on the server. **There is no client-side prediction** (ADR-0004): the client mirrors
server snapshots wholesale and never speculates. Mobile-first, architected for Desktop

## What this file is

Constraints you **cannot discover by looking**. Everything below documents an *absence* or a
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

## Six card triggers are NO-GO until implemented

**Do not author a card using any of these.** They are declared in `Enums.cs`'s `Trigger` enum
and **raised nowhere in the engine**. A card using one parses, validates, loads, and then does
nothing — forever, silently, with no error.

> `ON_TURN_START` · `ON_TURN_END` · `ON_DAMAGED` · `ON_HEAL` · `ON_SPELL_CAST` · `AURA`

**The five that work:** `ON_PLAY`, `ON_CAST`, `ON_DEATH`, `ON_FRIENDLY_SUMMON`, `ON_ATTACK`.

**`ON_ATTACK` is the trap** — it is raised for **weapons only**, so it is *valid* on a weapon
and *dead* on a minion. A membership check alone will not catch that.

**`ON_DEATH` is the mirror trap** — `RulesEngine.BreakWeapon` emits a VFX event when a weapon
breaks and raises no trigger at all. **A Deathrattle on a weapon never fires.**

Lift a name off this list only when its raise site actually exists. `CardValidator` has no rule
for any of this; until it does, **this list is the only guard.**

**This list does not live here alone.** Each member of the `Trigger` enum in
`Emberfall.Core.Contracts/Enums.cs` is marked `LIVE` or `DEAD` on its own line, with the intended
raise site and the retirement trigger. That is the copy an implementer actually meets. If the two
ever disagree, **the enum markers win** — they sit next to the code and this file does not.

## What a weapon can and cannot do

A hero attacking with a weapon carries **no keywords at all** — `attackerKws` is a literal empty
set for a hero, and `WeaponInstance` has no keyword field to read from. **No weapon can carry
Lifedrain, Venom or Frost as an on-hit rider.** Do not author one.

A weapon's entire expressive surface today is **stats**, **`ON_PLAY`** (equip battlecry), and
**`ON_ATTACK` with a static selector** — that trigger fires with an empty target list, so "the
minion this hits" is not addressable.

Giving weapons keywords is a **hash-canonical change**, not a small patch: `WeaponInstance` is
game state, so a new field must enter the serializer and hasher, and the golden replays re-bake
with the R1 gate as witness.

## Player-facing input consults the vulnerability index

**Before implementing anything that accepts client-authored input, scan §4 of the security
index** (`Project Docs\Emberfall\docs\security\README.md`). It is a trigger-keyed table, a few
words per row, so the scan takes seconds and a matching row is obvious. Match a row, read that
`VULN-nnn` in full, and follow its binding rules. This index is a specification, not a tracker.

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

Placed so far: the `Trigger` enum (`Enums.cs`), the per-effect loop in `CardValidator`, and
`WeaponInstance` (`GameState.cs`).

**A claim with no retirement trigger and no expiry is fine.** Most of this file is that — a hazard
that is simply true. Only pin the ones that a known, named change will invalidate.

## Generated art lands in `art-bakeoff/`

**Generation never writes into the Unity project.** Generators drop candidates into
`art-bakeoff/card-art/<batch>/art_<cardId>/cand_*.png`, organised by batch rather than by where
the asset will live. **Resolving a candidate into a project path is the importer's job**, after
generation — the generating session does not have the information to get it right.

**The destination is keyed by the card's `setId`, not by the batch:**
`Assets/Art/Cards/<setId>/art_<cardId>.png`, where the filename is the row's `artId` with `/`
flattened to `_`. `CardArtPostprocessor` keys import settings off `Assets/Art/Cards/<setId>/`, so
a file in the wrong set folder **imports with the wrong settings and nothing complains.** Read
the row's `setId` before copying — it does not always match the batch folder it sits in.

**Replacing existing art: overwrite the `.png` in place. Never delete the `.png` + `.meta`
pair.** The `.meta` carries the guid the Addressables entry points at; deleting it mints a new
one and silently orphans that entry. Overwriting keeps it. New art still needs its generated
`.meta` committed — a separate and easy-to-forget step.

**`art-bakeoff/` is disposable and fully untracked (Sam, 2026-07-31).** Every final version already
lives under `Assets/`; the folder is kept only because it is good to look at. The whole thing could
be deleted and no build, test or asset would change. Do not treat a candidate as load-bearing, and
do not ask before removing one — but equally, nothing in there is a deliverable.

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
- Keep cross-references explicit (e.g. "see `interfaces.md §5.3`") when one spec depends on
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
