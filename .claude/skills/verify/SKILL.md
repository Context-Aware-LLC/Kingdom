---
name: verify
description: Run automatically after producing any change Unity will have to compile — before calling that work done. TRIGGER — whenever a change touches a .cs file under unity/Emberfall/Assets/Scripts/{Presentation,Editor,AI,Data,Session}, an Emberfall.Core.*/.Net.Client/.Net.Protocol file or any .asmdef/.asmref/.csproj under unity/Emberfall.  SKIP only when nothing Unity-consumed changed: docs, Emberfall.Net.Server/.Tools.* (cloud-only, net10.0), non-Unity tooling scripts, or art with no accompanying code. Check the cached result in .agent/unity-gate.json before paying for a fresh run — the verify stage drives a real Editor and the Editor is a single-writer resource, so re-running it when a good answer already exists for this commit just contends with whoever else might have it open.
---

# /verify — Unity compile + test gate

Reports the result of `tools/ci/unity-gate.ps1` (compile stage via `unity-compile.ps1`, then headless-Editor compile+test via `unity-verify.ps1`), reusing a cached result when it still applies instead of always re-running.

**Nobody is going to tell you to run this.** The trigger is the description above, not an instruction in your task. If you just finished editing or creating a `.cs` file Unity compiles
(Presentation/Editor/AI/Data/Session, or a `Core`/`Net.Client`/`Net.Protocol` file it consumes as a local package), or touched an `.asmdef`/`.csproj` under `unity/Emberfall`, invoke this yourself
before you report that work as done — a green cloud CI run tells you nothing about whether the Editor still compiles.

## Procedure

1. **Get the current commit.** `git rev-parse HEAD` (and `git status --porcelain -- unity/Emberfall` for working-tree state).

2. **Read `.agent/unity-gate.json` if it exists.** Compare its `commit` field to the current HEAD:
   - **Missing file** → no cached result. Run the gate (step 3).
   - **`commit` differs from current HEAD** → stale. Run the gate (step 3).
   - **`commit` matches AND `workingTreeDirty` was `false` in the cached run AND `unity/Emberfall` is clean right now** → the cached result still applies. Skip to step 5 and report it directly without re-running (a cached PASS needs no fix attempt; a cached FAIL still goes through step 4 first).
   - **`commit` matches but either the cached run or the current tree has uncommitted
     `unity/Emberfall` changes** → the cache may not reflect what's actually on disk right now (the gate only ever reflects HEAD, never the working tree). Say so, and ask whether to trust the cached result anyway or re-run — don't silently pick one.

3. **Run the gate:** `pwsh tools/ci/unity-gate.ps1`. This can take a while (the verify stage drives a real headless Editor). If it fails immediately with "Unity project is locked", that means an Editor is open on this machine right now — tell the user that, don't treat it as a code failure, and don't try to clear the lock yourself (`unity-verify.ps1` never does either). Then read the freshly written `.agent/unity-gate.json`.

4. **On failure, try to fix it yourself before anyone hears about it — but know when that's not your call.** Most `unity-compile.ps1` failures are mechanical: a stale API reference after a refactor, a missing `using`, a type mismatch, an `internal` visibility break. Fix it, re-run the gate once, and if it now passes, move on to step 5 — the fix landing is the whole story, nothing to escalate.

   Escalate instead of retrying or guessing when:
   - It still fails after one honest fix attempt.
   - The failure is a `unity-verify.ps1` `checks` entry with `result: "FAIL"` — a `HeadlessVerify` failure is behavioral, not a compile mismatch, and means the code does something a self-check didn't expect. That's a judgment call, not a typo.
   - More than one fix is plausible, or the fix would touch a `RETIREMENT TRIGGER` comment or a hash-canonical boundary (`CLAUDE.md`) — those need a ruling, not a guess.
   - The compile stage's Unity-install-path warning fired. That's a machine problem, not a code problem — no amount of editing C# fixes it.

   **Escalate as a Paperclip review request, not `blocked`.** `blocked` on this board is already overloaded — the watchdog parks tickets there for infra reasons that have nothing to do with a real dependency, so a genuine "a human needs to decide this" signal gets lost in that noise.
   Instead:
   1. `POST /api/issues/{id}/interactions` — `kind: "request_confirmation"`,
      `continuationPolicy: "wake_assignee"`. The comment is the actual gate output, quoted verbatim (failed check names, the `CS####` error text, the `reasons` array from `unity-gate.json`) — not a paraphrase.
   2. Then `PATCH /api/issues/{id}` to `status: "in_review"`. Doing this in the other order 422s (`invalid_issue_disposition`) — the interaction has to exist before the API will accept `in_review`.

   Then continue to step 5 and report what actually happened (fixed, or escalated).

5. **Report the result** — don't just say "pass" or "fail":
   - Overall `pass`, `commitShort`, `timestamp`.
   - Compile stage: assembly count, and name every entry in `failedAssemblies` if any.
   - Verify stage: if `skipped` is true, say compile failed first so verify never ran. Otherwise list any `checks` with `result: "FAIL"`, note `hasCompileErrors` if true, and surface `reasons`. `logPath` (if set) points at the full Unity Editor log for anyone who needs more than the summary.
   - If `workingTreeDirty` is true, mention which paths were dirty — the result is only about what's actually committed at that SHA.
   - If step 4 fixed the failure, say so and report the now-passing result. If step 4 escalated say that instead of re-stating the original failure as if nothing happened after it.

## What this is not

A cached PASS is a reason to skip re-running the gate for the *same* question asked twice, not a reason to skip running it at all before a push — `tools/ci/README.md`'s "run this before every push" guidance for `unity-compile.ps1` still stands. This skill answers "what did the gate last say about this commit," not "is it safe to assume Unity still compiles" once new commits land.
