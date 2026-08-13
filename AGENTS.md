# Emberfall agent working environment

These project rules apply to every agent working in this repository. Role-specific instructions
may narrow an agent's scope or forbid publishing; when they do, follow the stricter rule.

## Which workspace a task runs in

Two workspaces exist and they are not interchangeable. **Isolated is the default and needs no
justification; shared is an exception that does.**

| Task | Workspace | Why this is not a judgement call |
| --- | --- | --- |
| Needs the Unity Editor seat | **shared** | The seat is one machine-wide Editor pointed at one checkout. A second worktree does not mint a second seat, and the Editor rewrites URP settings across sibling worktrees. |
| Anything else | **isolated** | Its own `git worktree` under the configured parent, cut from `origin/master`, on its own branch, reaped automatically when the issue closes. |

**Isolated** means work only in the dedicated worktree supplied for the assigned issue; never in
the operator's primary checkout. It is a full checkout — every path in the repo is present.

**Shared** means the operator's primary checkout, with no isolation of any kind. Two things follow,
and both are load-bearing:

- **One agent at a time.** Concurrent shared runs land in the same directory and overwrite each
  other. The damage surfaces later as unexplained flakiness that does not look like contention.
- **It leaves a `cleanup_failed` record every time.** There is no workspace to reap — the path is
  the operator's own checkout — so the run's bookkeeping never resolves and the operator clears it
  by hand. Shared is not free; spend it only for the Unity seat.

RETIREMENT TRIGGER: this table assumes an isolated worktree is a FULL checkout. It was sparse until
2026-08-10 (`src tests tooling unity tools .github`), which silently made `art/`, `docs/`,
`deploy/`, `evidence/` and `.claude/` unreachable and forced every art or docs task into the shared
checkout. If a `provisionCommand` that narrows the checkout is ever restored to the project's
execution-workspace policy, this table is wrong and the excluded paths become a second row.

## Worktree and file safety

- Stage every commit by explicit path: `git add -- <path>`. Never use `git add -A`, `git add .`,
  or a broad glob. Multiple sessions may share the repository, and a broad add can sweep another
  session's work into your commit.
- Preserve source files as UTF-8 without BOM. Many files contain em dashes (`—`); use targeted
  edits and verify the encoding after any tool that could rewrite a whole file.
- Do not open the Unity Editor or run Unity-MCP tools unless the task brief explicitly hands you
  the Unity seat. Unity is a single-writer resource shared with the operator.

## Branch and integration contract

- One ticket, one merge. A branch is scoped to the issue that created it and must not accumulate
  follow-on tickets.
- Name an agent-owned branch `agent/<issue-id>-<short-slug>` unless the execution harness has
  already assigned a branch. Never switch, rename, or re-point a harness-owned branch.
- Rebase onto `origin/master` before every push: `git fetch origin` followed by
  `git rebase origin/master`.
- Push the issue branch and open a pull request against `master`. Never merge the pull request,
  push directly to `master`, or force-push a shared branch. The merge is Sam's checkpoint.
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
