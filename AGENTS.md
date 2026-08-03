# Emberfall agent working environment

These project rules apply to every agent working in this repository. Role-specific instructions
may narrow an agent's scope or forbid publishing; when they do, follow the stricter rule.

## Worktree and file safety

- Work only in the dedicated worktree supplied for the assigned issue. Do not use the operator's
  primary checkout.
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
- `agent/**` is deliberately absent from the workflow's `push` branches. For an `agent/**`
  branch, opening a pull request is mandatory: the `pull_request` workflow is its only cloud CI
  gate and reruns on every subsequent push. `feat/**` and `fix/**` also run CI on push before a
  pull request exists. See `.github/workflows/ci.yml`.
- A failing pull request remains the branch owner's responsibility. Fix the gate or record the
  unresolved failure on the issue; do not silently hand off a red branch.

## Verification

- Run the smallest project-specific check that proves the changed surface on the rebased tree.
- If a task explicitly grants the Unity seat and changes `unity/`, run
  `pwsh tools/ci/unity-verify.ps1` before pushing. Cloud CI has no Unity seat.
