# TCG "Hearthlike"

A digital, turn-based **1v1 collectible trading card game** 

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules engine that runs headless on the server. 
**There is no client-side prediction** (ADR-0004): the client mirrors server snapshots wholesale and never speculates. Mobile-first, architected for Desktop

**THE GAME REQUIRES A CONNECTION. THERE IS NO OFFLINE MODE, and no code will tell you so.**
Every bot lives in `src/Emberfall.Net.Server/Bots/` and runs server-side; the one client-side AI
(`PlayingAi`) is constructed at a single site inside `NetworkedGameBootstrap`, in a networked match.
So there is no local match path and **nothing a player can do without a server** — the screens that
still render offline (roster, collection, deck builder) exist only to prepare a match they cannot
start. Read this before choosing between server-authoritative and a local mirror for ANY player
data: the usual argument for a mirror is offline usability, and here that argument does not exist.

**The login gate does NOT establish that a server is reachable, and its name says it does.**
`SignInGateController.IsRequired` is `IsConfigured && !IsSignedIn && !Skipped`, and `IsSignedIn`
reads a **stored session off local disk** — its own summary is *"True when a session is stored. Says
nothing about the server accepting it."* So the gate blocks a first launch and **nothing after it**:
a returning player with a stored token boots into the menu with no network at all. Do not reason
"the menu is behind a login, therefore the code below it has a server."

**A graceful degrade is not free — it displaces the error.** That returning player's collection sync
fails, an absent `owned-cards.json` means "do not constrain", the deck builder shows the full pool,
they build from cards they do not own, and `MatchGate.cs:353` closes the socket when they queue. A
legible failure becomes an illegible one, later, in a different screen. Distinguish *never synced*
from *synced, then lost*; only the second is a transient worth absorbing silently.
(Sam, 2026-09-01, correcting a spec written the same day.)

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


## Card effects: the trigger name is not the behaviour

**Before authoring or changing anything inside a card's `rules[]` — activation, condition, lifetime,
action, target — or touching an engine raise site behind one, read the `Hazards` and `What a weapon
can and cannot do` sections of `docs/dev-docs/specs/card-schema.md`.** That is where the
trigger-by-trigger behaviour lives: what each one actually raises, what it deliberately stays
silent on, and what a weapon can express. Same shape as the vulnerability-index rule below — a
match is obvious and the scan takes seconds.

The one line to carry without reading: **a trigger that parses is not a trigger that fires, and
liveness is per SOURCE TYPE, never per trigger name.** `ON_ATTACK` is valid on a weapon and dead on
a minion; `ON_TURN_END` fires on *both* players' turn ends; `ON_HEAL` means "this minion was
healed" and never fires on a hero. A membership check against the `Trigger` enum catches none of
that. `CardValidator` rule C21 enforces the real relation — **assign the exact source/trigger pair
in the same commit that lands its raise path.**

**When the doc and the code disagree, the code wins.** The `Trigger` enum in
`src/Emberfall.Core.Contracts/Enums.cs` carries a liveness marker, raise site and retirement
trigger per member; `EffectSourceCapabilities`, `AuraCapabilities` and `CardValidator` hold the
tables. The spec doc is a copy of those, and the copy is the thing to fix.

**Changing the shape of game state moves the hash, and NOTHING IN THIS REPO WILL TELL YOU.**
`PlayerState.Weapon` is game state; so is any field the canonical serializer walks. The pieces that
have to move together are the canonical serializer, the projection and the snapshot codec — miss one
and the client and server disagree about a state neither can prove wrong.

**The suite is BLOCKING again, and a `dotnet test` matching zero tests exits 0.** The five .NET
projects came back on 2026-08-24 (Sam's direction) minus the card-authoring block, ran advisory
while they were red, and went blocking the same day they went green — `ci.yml` Stage 2 and
`gates.ps1` Stage 2. All five must stay registered in `Emberfall.slnx`, because a run that matches
nothing is green for the wrong reason. **Read the count, not the badge.**

**NOTHING pins the canonical hash any more, by decision.** `Known_Vector_Digest_Is_Pinned`, the 11
golden replay fixtures and their rebaseline guard were deleted 2026-08-24 (Sam: the replays were
fake) — they recorded a state the effect-model fork and the numeric card id had deliberately left
behind, and with one developer and no live players a golden guards against a desync that cannot
happen. What survives is RELATIVE and cannot go stale: the per-field mutation sweep in
`CanonicalSerializationTests` (perturb a field, the digest must move), the committed field manifest,
and NetSoak's zero-resync run. So a state-shape change still lands on review and a build, and
**the reviewer is still the only guard**: read the three sites rather than hunting for a gate.
The deleted copies are outside the repo in `emberfall-test-archive/returned-2026-08-24/`.

**A fixture's card id is minted, not written.** `Support/FixtureCard` maps a test-local mnemonic to
a number above 1_000_000, far clear of every shipped row; `Support/ShippedCards` is the ONE place a
real handle resolves to a real id, and it exists only in tests. Neither is a handle lookup in the
sense the ruling forbids — see the class docs, which say why.

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

**Do not list placed markers here**
 — a list of them is itself a record that drifts.** 
Ask the repo instead, and note that they are spread across the engine, the Unity presentation layer
and `tooling/`, not just `src/`:

```
git grep -n "RETIREMENT TRIGGER" -- src tooling tools unity
```

**A claim with no retirement trigger and no expiry is fine.** Most of this file is that — a hazard
that is simply true. Only pin the ones that a known, named change will invalidate.



## Two things about art files

**Replacing existing art: overwrite the `.png` in place. Never delete the `.png` + `.meta` pair.**
The `.meta` carries the guid the Addressables entry points at; deleting it mints a new one and
silently orphans that entry, with no error at any point. Overwriting keeps it. New art still needs
its generated `.meta` committed — a separate and easy-to-forget step.

**The `<setId>` folder under `Assets/Art/Cards/` decides which BUNDLE a file ships in.**
`AddressablesSetup` builds one `CardArt_<setId>` group per folder on disk, so a file under the
wrong set folder still renders and still passes CI — the address is a flat `card_art/<fileName>`
with no set in it — while shipping in the wrong bundle. CI runs `audit_card_art_names.py` with
`--fail-missing-only`, which suppresses this exact finding; run it without the flag to see it.
Read the row's `setId` before copying. (`Assets/Art/UI/` is the opposite — its subfolders are
purely organisational. Do not reason from one to the other.)

The naming convention itself, and everything that checks it, lives in that script's docstring.

## Never commit a changed `productName`

**Before committing `unity/Emberfall/ProjectSettings/ProjectSettings.asset`, `git diff` it and confirm `productName` is still exactly `Bad Ideas`.**

It was `Emberfall` until 2026-09-28. Sam renamed it before any store upload, because it is the
label under the app icon. The codename, the setId and the folder names stay `Emberfall`.

On Windows, `Application.persistentDataPath` is `AppData/LocalLow/<companyName>/<productName>`
and PlayerPrefs are keyed the same way, so that one string keys the desktop player's **entire
collection** and stored session. Rename the product and a desktop build silently boots against a
different, empty collection root, with no error and no way for the player to tell what happened.
On Android and iOS the save root is keyed on the package id (`com.contextaware.kobi`), so
`productName` is only the icon label there. Once a build ships, the package id can never change.

**This is not a typo risk — a tool does it to you.** Running a Unity **multiplayer scenario**
rewrites `productName` to its role name. The project's only scenario asset was deleted
2026-08-26, so nothing triggers this today — but Multiplayer Play Mode is a built-in editor
module, not a package, so authoring a new scenario under `Assets/Settings/PlayMode/` arms it
again. **`Emberfall/Build/Restore Product Name` puts it back** (set through `PlayerSettings`,
since Unity holds project settings in memory and overwrites a file edited underneath it). The
rename is harmless while it stays in the working tree; only the commit is damaging.

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

## Unity: the Editor is a single-writer resource

**An open Editor holds `unity/Emberfall/Temp/UnityLockfile`, and anything spawning its own Editor
blocks on it.** `tools/ci/unity-verify.ps1` does exactly that, so the verify half of the gate needs
the Editor closed, and it reports the lock as an error rather than clearing it. Never clear it
yourself.

**Drive an already-open Editor instead of spawning one.** The Unity CLI reaches a running Editor
through the project's `com.unity.pipeline` package, so it takes no lock and contends with nobody:
`unity command menu --path "<menu item>"` replaces `Unity.exe -batchmode -executeMethod`, and every
command takes `--json`. `unity pipeline list` is what diagnoses a connection failure; the
`unity-cli` skill carries the rest, including the Safe Mode recovery loop.

**With two Editors open the CLI drives the WRONG one and labels it convincingly.** Measured
2026-08-21: `unity pipeline list --json` reported exactly one instance, labelled with the
WORKTREE's path, while evaluated code returned the MAIN COPY's `Application.dataPath`, and
`--project-path` did not change it. **A path label is not evidence of which Editor you are
driving** — return `Application.dataPath` and read it before any eval that writes, or a mutating
eval aimed at your worktree lands silently in somebody else's project. `-batchmode -executeMethod`
is exempt: there `-projectPath` is the target, not a hint.

**RETIREMENT TRIGGER:** the Unity MCP relay and its two skills were removed 2026-08-20 in favour of
the CLI; if an MCP path is ever reinstated, this section and `.claude/skills/` change together.

## The shared headless Editor at `C:\claude-editor`, and its lock

**Before you touch `C:\claude-editor` at all, read `C:\claude-editor\EDITOR-LOCK.md`.** Occupied
by someone else → do not `cd` into it, do not `git -C` it, do not launch Unity on it, do not "just
check something quickly". Editing that file's status block IS claiming and releasing it. Nothing
else is authoritative — the brain pages `handoff/unity-editor` and `handoff/unity-editor-status`
are pointers that say so themselves, because a lock read from two gbrain lanes returned two
different statuses at the same moment.

It is a permanent git worktree of this repo carrying its own warm Unity `Library/` (gitignored), so
a session can run the compile gate, `HeadlessVerify`, a build or the Device Simulator **without
contending for Sam's own Editor** — a different folder, not covered by this lock. For any agent
working **interactively** with Sam — Claude or Codex, the name is historical — and **not** for
Paperclip or other autonomous runs, which take isolated worktrees. The protocol is mirrored in
`AGENTS.md` for agents that do not auto-load this file; the two move together.

**Nothing keeps it current with `master`, and a stale gate run comes back GREEN.** It is one
worktree on one branch, so a result from it is about whatever is checked out there. Read
`git -C /c/claude-editor rev-parse --abbrev-ref HEAD` and how far behind it is before you believe
anything it tells you.

Three reasons the lock is not optional, all of which bite silently:

1. **One worktree is one branch** — a second session switching branches underneath an occupant
   destroys their run mid-flight.
2. **`Temp/UnityLockfile` is per project folder** — one Editor or one batchmode run at a time.
3. **Every Unity run rewrites tracked assets that are not yours** — `EditorBuildSettings.asset`,
   `ShaderGraphSettings.asset`, the codecoverage `Settings.json`, sometimes a card `.meta` or the
   URP globals. The damage arrives looking like your own diff.

**Releasing is part of the task.** Revert Unity's collateral, confirm `productName` is still
`Bad Ideas`, park the worktree off any feature branch, and set the block back to `VACANT`. A stale
claim — OCCUPIED, no `Unity.exe` on that path, `since` older than 30 minutes — is fair to take; say
so in the file when you do.

**RETIREMENT TRIGGER:** if `C:\claude-editor` is deleted, moved, or stops carrying its own
`Library/`, this section dies with it — and so do the `AGENTS.md` mirror, the pointer block inside
`EDITOR-LOCK.md`, and those two brain pointers.

## Ask the graph before you grep

There is a persistent knowledge graph of this repo — csharp, sql, python, javascript, powershell,
bash, c and cpp — built by `code-review-graph`. It answers *where is this defined, what calls it,
what does it call, which flow is it in*. Grep answers *which files contain this string*. Reaching
for the second when you wanted the first is the default failure, because grep always looks like it
worked. Its size is printed at every session start; no count for it lives in this file.

**Its callers/callees effectively stop at the assembly boundary, and it does not say so.** C# node
keys are path-scoped (`...\CardDefinition.cs::TargetSpec.IsKnown`) while most `CALLS` targets are
stored as bare names (`FromSeconds`, `nameof`), so a cross-project call usually does not resolve:
re-measured 2026-09-13, `Emberfall.Net.Server` → `Emberfall.Core` resolves to **27 edges across
1,409 Server symbols** (it was a flat 0 on 2026-08-20, before `tooling/scripts/csharp_resolver.py`),
and `CALLS` is 78% unresolved. Treat "Called by" as *within this project*, and never as an
exhaustive list. Everything derived from those edges inherits the hole — `caller_count`,
`test_coverage` and `risk_score` are still mostly defaults, and **`code-review-graph dead-code` is
therefore unusable**. **Believe the tool over this paragraph:**
`python tooling/scripts/graph_metrics.py` prints today's resolution rates, and
`tooling/scripts/graph_view.py` carries the history and a retirement trigger.

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
