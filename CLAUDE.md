# TCG "Kingdom of Bad Ideas"

A digital, turn-based **1v1 collectible trading card game** 

**Engine/stack:** Unity (LTS) + C#. Server-authoritative, deterministic rules engine that runs headless on the server. 
**There is no client-side prediction** (ADR-0004): the client mirrors server snapshots wholesale and never speculates. Mobile-first, architected for Desktop

**THE GAME REQUIRES A CONNECTION. THERE IS NO OFFLINE MODE, and no code will tell you so.**
Every bot lives in `src/Kingdom.Net.Server/Bots/` and runs server-side; the one client-side AI
(`PlayingAi`) is constructed at a single site inside `NetworkedGameBootstrap`, in a networked match.
So there is no local match path and **nothing a player can do without a server** — the screens that
still render offline (roster, collection, deck builder) exist only to prepare a match they cannot
start. Read this before choosing between server-authoritative and a local mirror for ANY player
data: the usual argument for a mirror is offline usability, and here that argument does not exist.

**The login gate does NOT establish that a server is reachable, and its name says it does.**
`SignInGateController.IsRequired` is `IsConfigured && !IsSignedIn && !Skipped`, and `IsSignedIn`
reads a **stored session off local disk** — its own summary is *"True when a session is stored. Says
nothing about the server accepting it."* So the login gate blocks a first launch and **nothing after
it**. Do not reason "the menu is behind a login, therefore the code below it has a server."

**What does establish it is the update gate, and only ONCE PER LAUNCH.** `MainMenuController` asks
`GET /update-gate` before the menu shows and blocks on anything but a pass — a stale build is sent
to the store, an unreachable server gets Retry and nothing else (Sam, 2026-10-03). The pass is
remembered for the life of the process, so it proves the server answered at boot, **not** that it is
answering now: every screen below the menu can still lose the network underneath itself.

**A graceful degrade is not free — it displaces the error.** A collection sync that fails leaves
`owned-cards.json` absent, absent means "do not constrain", the deck builder shows the full pool,
the player builds from cards they do not own, and `MatchGate` closes the socket when they queue. A
legible failure becomes an illegible one, later, in a different screen. Distinguish *never synced*
from *synced, then lost*; only the second is a transient worth absorbing silently.
(Sam, 2026-09-01, correcting a spec written the same day.)

## What this file is

Constraints you **cannot infer by looking**.
Everything below documents an *absence* or a *consequence* — a trigger that parses but never fires, a rename that relocates every save — because those are invisible on inspection and expensive to learn by hitting them.

**What is built, what exists, and how many of anything there are is deliberately NOT recorded here.**
Read the code, the data and the tests; they cannot drift from themselves. Do not add a status summary, a file inventory, a directory map, or a count to this file — each one can only become wrong, and this file is loaded into every session that touches the repo.

## Where things are

`src/` and `tests/` hold the deterministic rules core (solution `Kingdom.slnx` — note `.slnx`, not `.sln`). `unity/` is the Unity project.
`art-pipeline/` holds source art and generated candidates, and is gitignored.
`tooling/fonts/` holds variable font sources that `tooling/scripts/instance_font.py` cuts into
the static fonts Unity ships, because Unity cannot set a variable font's axes.
`tooling/scripts/` and `tools/ci/` hold automation.
See `src/README.md` for the engine map.

**The core is `netstandard2.1`, NOT .NET 10.**
Every `Kingdom.Core.*` project, plus `Kingdom.Net.Client` and `Kingdom.Net.Protocol`, targets `netstandard2.1` because:
**Unity consumes them as local packages** and cannot load a `net10.0` assembly. Only `Kingdom.Net.Server`
and the `Kingdom.Tools.*` projects are `net10.0`. Reaching for a .NET 10 API or a newer C#
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
Burst *"Failed to resolve assembly: 'Kingdom.AI'"* and a vanished `Kingdom/` menu — neither of
which points at an access modifier. The live declarations are in
`src/Kingdom.Core.Contracts/AssemblyInfo.cs`; that file carries the retirement trigger.

**`<Nullable>enable</Nullable>` is the same trap, and its fix has two of its own.** Unity takes
compiler switches from a file named exactly `csc.rsp` beside the `.asmdef`; each Unity-consumed
`src/` project carries one holding `-nullable:enable`. Any other filename is ignored without a
word — `<AssemblyName>.rsp` sat inert from 2026-08-23 to 2026-10-10 while 296 CS8632 warnings
filled the console. And the file takes **arguments only**: a `#` comment line is split into words
and every word becomes a source file, which fails the compile with CS2001 naming a word, not the
file. Confirm a switch arrived with
`grep -c -- -nullable unity/Library/Bee/artifacts/*.dag/Kingdom.Core.Cards.rsp`. A project that
stops being consumed by Unity takes its `csc.rsp` with its `.asmdef`.

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
`src/Kingdom.Core.Contracts/Enums.cs` carries a liveness marker, raise site and retirement
trigger per member; `EffectSourceCapabilities`, `AuraCapabilities` and `CardValidator` hold the
tables. The spec doc is a copy of those, and the copy is the thing to fix.

**Changing the shape of game state moves the hash, and NOTHING IN THIS REPO WILL TELL YOU.**
`PlayerState.Weapon` is game state; so is any field the canonical serializer walks. The pieces that
have to move together are the canonical serializer, the projection and the snapshot codec — miss one
and the client and server disagree about a state neither can prove wrong.

**A `dotnet test` that matches zero tests exits 0.** All five .NET test projects must stay
registered in `Kingdom.slnx`, because a run that matches nothing is green for the wrong reason.
**Read the count, not the badge.**

**Nothing pins the canonical hash, by decision** — no golden digest, no replay fixtures. What
exists is relative and cannot go stale: the per-field mutation sweep in `CanonicalSerializationTests`,
the committed field manifest, and NetSoak's zero-resync run. So a state-shape change lands on review
and a build, and **the reviewer is the only guard**: read the three sites rather than hunting for a gate.

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

**Do not list placed markers here — a list of them is itself a record that drifts.**
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

**Before committing `unity/ProjectSettings/ProjectSettings.asset`, `git diff` it and confirm `productName` is still exactly `Bad Ideas`.**

It was `Emberfall` until 2026-09-28. Sam renamed it before any store upload, because it is the
label under the app icon. The codename and the folder names are `Kingdom` (renamed from
`Emberfall` 2026-10-10); the first set's setId is `PENDINGHAM` (renamed from `EMBERFALL`
2026-10-11, with its folders, card file and pack ids).

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
again. **`Kingdom/Build/Restore Product Name` puts it back** (set through `PlayerSettings`,
since Unity holds project settings in memory and overwrites a file edited underneath it). The
rename is harmless while it stays in the working tree; only the commit is damaging.

## Run the check the edit can break, not the suite

**After an edit, run only what depends on the files you changed.** (Sam, 2026-10-06.) A full gate
after every change spends minutes proving things the change could not have reached, and he waits
through all of it. Reach follows the project references and runs one way: Unity consumes the core,
`tests/` consumes `src/`, and nothing under `tests/` references a `unity/Assets/Scripts` assembly.

| You changed | Run | Do not run |
|---|---|---|
| `unity/Assets/Scripts/**` | `pwsh tools/ci/unity-compile.ps1`, then the one `HeadlessVerify` static that covers that surface, if one does | `dotnet test`; the other statics |
| `src/Kingdom.Core*`, `Net.Client`, `Net.Protocol` | the test classes that name the type you changed, then `unity-compile.ps1`, because Unity builds these too | the rest of the suite |
| `src/Kingdom.Net.Server`, `Kingdom.Tools.*`, `Kingdom.Training.*` | the test classes that name the type you changed | anything Unity; it never loads these |
| card JSON | the CardCi line of `gates.ps1` Stage 6 | the test suites |
| `.uss`, `.uxml` | the Stage 1b or 1c line of `gates.ps1` | everything else |
| a script under `tooling/` | that script's own test under `tests/python/` | |
| docs, comments, memory | nothing | |

Find the test classes by grepping `tests/` for the type, then
`dotnet test tests/<that project> --filter "FullyQualifiedName~<TestClass>"`. **A filter that
matches nothing exits 0** (see the suite rule above), so read the count of tests it ran.
`unity-compile.ps1` has no per-assembly switch; it is already the smallest compile check there is.
With an Editor open, run the one static through `unity command menu` or `eval`, never `RunAll`,
which exits the Editor.

**Widen only when you cannot bound the reach**, and then to the half that applies (`gates.ps1` for
.NET, `unity-gate.ps1` for Unity): a change to game-state shape, the canonical serializer, the
projection, the snapshot codec or the wire; a `.csproj`, `.asmdef`, `Directory.Build.props` or
`Kingdom.slnx`; a type whose callers in other assemblies you did not enumerate; or Sam asking.

**A push follows the same rule over the whole diff**: the union of what its commits touched, not
the full gate by default. This outranks the "before every push" lines in the `tools/ci/` script
headers and in the `/verify` skill; the daily cloud run is the backstop for everything else. Say
in the report which checks you ran and which you left out, so a narrow run never reads as a full one.

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

**An open Editor holds `unity/Temp/UnityLockfile`, and anything spawning its own Editor
blocks on it.** `tools/ci/unity-verify.ps1` does exactly that, so the verify half of the gate needs
the Editor closed, and it reports the lock as an error rather than clearing it. Never clear it
yourself.

**Drive an already-open Editor instead of spawning one.** The Unity
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

**Paused 2026-10-19:** Sam turned the graph's `PreToolUse` and `SessionStart` hooks off to measure
the difference, so nothing below is injected right now; run `code-review-graph` by hand. Restoring
them means reverting the commit that removed them from `.claude/settings.json` and this paragraph.

A persistent `code-review-graph` of this repo answers *where is this defined, what calls it, which
flow is it in*; grep answers *which files contain this string*. A `PreToolUse` hook injects the
graph's answer ahead of every Grep, Glob, Read and Bash result, so you should not have to do
anything. Two limits: its call edges mostly stop at the assembly boundary, so "Called by" means
*within this project* and `code-review-graph dead-code` is unusable
(`python tooling/scripts/graph_metrics.py` prints today's resolution rates); and its absence is
invisible — the hook exits silently where the tool is not installed. If you have run several
searches and never seen a `[code-review-graph]` block, run `code-review-graph status` and judge it
by **node count, not exit code**: a broken install reports a handful of nodes and exits 0. Install
with `uv tool install "code-review-graph[communities,embeddings]"`, never `pip --user`. It refreshes
from `.githooks/pre-commit`, so `built_at_commit` trailing HEAD by one commit is correct.
