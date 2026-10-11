# Kingdom agent working environment

These project rules apply to every agent working in this repository. Role-specific instructions
may narrow an agent's scope or forbid publishing; when they do, follow the stricter rule.

## Product identity and tone

- **The public game is _Kingdom of Bad Ideas_**, at `kingdomofbadideas.com`. `Bad Ideas` is the
  app-icon label (`productName`) and `com.contextaware.kobi` is the package id. `Kingdom` remains
  the internal codename and `EMBERFALL` remains a set id; do not casually rename handles, ids,
  `artId`s, namespaces, repository paths, or other internal identifiers to match the public name.
- **The world is ridiculous and the performance is deadpan.** The tonal ingredients are original
  anachronism, medieval bureaucracy, peasant politics, and absurdity treated with complete
  seriousness. Gameplay remains strategically serious even when the setting, presentation, SFX,
  flavor, or an occasional separately approved mechanic supplies slapstick.
- **The inspiration is a tone, never borrowed material.** Do not reproduce or closely echo a
  recognizable Monty Python character, scene, line, prop, or joke. If a gag only works because the
  audience recognizes somebody else's bit, cut it. Original visual and verbal comedy must stand on
  its own.
- **Retone deliberately, not indiscriminately.** Audit names rather than blanket-renaming them;
  regenerate art selectively when a changed concept actually needs it. A display-name change never
  changes a card handle, id, or `artId`. Unless Sam gives a task-specific scope, follow the staged
  pass in `docs/dev-docs/specs/tone-kingdom-of-bad-ideas.md`: tone guide, CLOCKWRIGHT pilot,
  class-by-class rollout, NEUTRAL last, then global copy after the voice has been proven.

Before choosing a workspace or integration path, identify the execution mode:

- **Interactive** — Sam is directly participating in the conversation, assigning the work, and
  available to answer a question or approve an integration step.
- **Autonomous** — the work came from Paperclip, an issue queue, a scheduled/background run, or
  another harness where Sam is not actively participating.

Do not apply autonomous handoff rules to an interactive session. The ability to ask Sam is a
meaningful control boundary, not an implementation detail.

## Which workspace a task runs in

Two workspaces exist and they are not interchangeable. The correct default depends on the
execution mode.

| Task | Workspace | Why |
| --- | --- | --- |
| Interactive task with no competing writer or overlapping dirty state | **operator checkout** | Sam can see the work where they are already working and can resolve branch or integration decisions in the conversation. |
| Interactive task whose requested branch cannot be created safely in the operator checkout | **isolated** | Preserve unrelated local changes or active work. Tell Sam immediately which worktree and branch are being used; do not let the alternate path surface only at handoff. |
| Autonomous task that does not need the Unity Editor seat | **isolated** | Its own `git worktree` under the configured parent, cut from `origin/master`, on its own branch, and reaped automatically when the issue closes. |
| Task explicitly granted the Unity Editor seat | **operator checkout** | The seat is one machine-wide Editor pointed at one checkout. A second worktree does not mint a second seat, and the Editor rewrites URP settings across sibling worktrees. |

**Isolated** means work only in the dedicated worktree supplied for the assigned issue; never in
the operator's primary checkout. It is a full checkout — every path in the repo is present.

**Operator checkout** means the operator's primary checkout, with no isolation of any kind. Two
things follow, and both are load-bearing:

- **One agent at a time.** Concurrent shared runs land in the same directory and overwrite each
  other. The damage surfaces later as unexplained flakiness that does not look like contention.
- **Inspect before changing branches.** Preserve unrelated edits already present. If the requested
  branch operation would disturb them, use an isolated worktree and explain that choice before
  implementation begins.

An autonomous harness run in the operator checkout still leaves a `cleanup_failed` record because
there is no workspace to reap. That bookkeeping concern applies to autonomous runs; it does not
turn a direct interactive session with Sam into an autonomous run.

RETIREMENT TRIGGER: this table assumes an isolated worktree is a FULL checkout. It was sparse until
2026-08-10 (`src tests tooling unity tools .github`), which silently made `art/`, `docs/`,
`deploy/`, `evidence/` and `.claude/` unreachable and forced every art or docs task into the shared
checkout. If a `provisionCommand` that narrows the checkout is ever restored to the project's
execution-workspace policy, this table is wrong and the excluded paths become a second row.

## Generated artwork

- In this project, save generated images, review candidates, and their prompts under
  `art-pipeline/<subject>/` by default, including when using the `imagegen` skill. Resolve this
  path from the repository root, not the current subdirectory. Sam's explicit destination overrides
  this default.
- The built-in image generator may initially save under `$CODEX_HOME/generated_images/`; copy
  its outputs into `art-pipeline/` before presenting the saved review paths. Use descriptive,
  versioned filenames and preserve existing candidates. This project default replaces the generic
  `output/imagegen/` destination; it does not change defaults for other projects.

## Worktree and file safety

- Stage every commit by explicit path: `git add -- <path>`. Never use `git add -A`, `git add .`,
  or a broad glob. Multiple sessions may share the repository, and a broad add can sweep another
  session's work into your commit.
- **Staging by path is not sufficient — commit by path too: `git commit --only <paths>`.** A bare
  `git commit` commits the whole index, and the index is not yours alone: whatever another session
  left staged rides into your commit no matter how carefully you added. This cost a session on
  2026-08-12. `git status` distinguishes them — `D ` in the LEFT column is someone else's staged
  change, ` D` in the right column is an unstaged one.
- Preserve source files as UTF-8 without BOM. Many files contain em dashes (`—`); use targeted
  edits and verify the encoding after any tool that could rewrite a whole file.
- Do not open the Unity Editor, drive one through the `unity` CLI, or run `-batchmode`, unless the
  task brief explicitly hands you the Unity seat. Unity is a single-writer resource shared with the
  operator. (The Unity MCP relay this line used to name was removed 2026-08-20; the CLI replaced it.)

## The shared headless Editor at `C:\claude-editor`

A permanent worktree of this repo carrying its own warm Unity `Library/`, so an **interactive**
session can run the compile gate, a build or the Device Simulator without contending for Sam's own
Editor. **Autonomous runs do not use it** — they take an isolated worktree and are covered by the
Unity-seat rule above. Its name is historical; any interactive agent may hold it.

**`C:\claude-editor\EDITOR-LOCK.md` IS the lock. Read it before you touch that directory at all** —
before you `cd` into it, `git -C` it, launch Unity on it, or "just check something quickly."
Occupied by someone else means hands off. Editing that file's status block is how you claim it and
how you release it; nothing else is authoritative, in this file or anywhere else.

Three reasons it is not optional, all of which bite silently: one worktree is one branch, so a
second session switching branches destroys the occupant's run mid-flight; `Temp/UnityLockfile` is
per project folder, so it is one Editor at a time; and every Unity run rewrites tracked assets that
are not yours, so the damage arrives looking like your own diff.

**Releasing is part of the task**, not follow-up work: revert Unity's collateral, confirm
`productName` is still `Bad Ideas`, park the worktree off any feature branch, set the block back to
`VACANT`. A stale claim — OCCUPIED, no `Unity.exe` on that path, `since` older than 30 minutes — is
fair to take; say so in the file when you do.

The full protocol, and the Unity hazards it exists to contain, are in this repo's `CLAUDE.md`
("The shared headless Editor at `C:\claude-editor`, and its lock"). That file is not loaded into
your session automatically — **read it before your first Unity action**, not after.

**RETIREMENT TRIGGER:** if `C:\claude-editor` is deleted, moved, or stops carrying its own
`Library/`, this section dies with it, and so does the matching section in `CLAUDE.md`.

## Branch and integration contract

- For autonomous work, one ticket means one branch and one merge. Do not accumulate follow-on
  tickets on the same branch. In an interactive session, keep the branch scoped to the work Sam is
  directing and ask if a follow-on would materially broaden it.
- Name an agent-owned branch `agent/<issue-id>-<short-slug>` unless the execution harness has
  already assigned a branch. Never switch, rename, or re-point a harness-owned branch.
- Rebase onto `origin/master` before every push: `git fetch origin` followed by
  `git rebase origin/master`.
- **Autonomous:** push the issue branch and open a pull request against `master`. Never merge it;
  the pull request is Sam's checkpoint.
- **Interactive:** Git integration is implementation plumbing, not a user checkpoint. Sam grants
  standing authorization to create or reuse an appropriate branch, commit, rebase or merge, push
  to the branch's upstream (including `master`), open and merge a pull request, and update the
  operator checkout after verification. Do not ask Sam to choose or approve routine Git steps, and
  do not leave completed work sitting only on a branch or in an open pull request. Use the safest
  normal integration route available and report the result. Ask only when the target is genuinely
  ambiguous, integration conflicts with unrelated work, or the necessary operation is destructive
  or difficult to recover.
- Autonomous work must never push directly to `master` or force-push a shared branch. Interactive
  standing authorization does not include force-pushing over unrelated history.
- If role-specific instructions forbid pushing or opening a pull request, hand the local commit to
  the named lead. That lead owns the rebase, branch publication, and mandatory pull request before
  integration.
- **There is no cloud CI on your pull request. Do not wait for one.** Since 2026-08-13 `ci.yml`
  has no `push` and no `pull_request` trigger — it is `workflow_dispatch` plus a daily 09:00 UTC
  cron on `master`. Actions burned 1756 of the 2000 free minutes in 13 days and run count was the
  only lever; the full reasoning is in the header of `.github/workflows/ci.yml`. A pull request
  that sits waiting to go green waits forever.
- **`pwsh tools/ci/gates.ps1` is the gate now, and it is yours to run.** Same stages as the cloud
  pipeline, same filters, ~2.5 min, free. Run it on the rebased tree before you push and report
  the result on the issue. Nothing else will catch you — the daily run reports against `master`
  hours later, long after your branch merged.
- If a change genuinely warrants a cloud run, ask Sam to dispatch `ci.yml` at your branch rather
  than spending the budget yourself.
- A failure you introduced remains yours whether a cloud run found it or `gates.ps1` did. Fix the
  gate or record the unresolved failure on the issue; do not silently hand off a red branch.

## Verification

- Run the smallest project-specific check that proves the changed surface on the rebased tree.
- If a task explicitly grants the Unity seat and changes `unity/`, run
  `pwsh tools/ci/unity-verify.ps1` before pushing. Cloud CI has no Unity seat.
