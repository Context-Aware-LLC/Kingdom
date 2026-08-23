# CI utility scripts

## .NET determinism gates — run this before every push

```powershell
pwsh tools/ci/gates.ps1            # full pass, ~2.5 min
pwsh tools/ci/gates.ps1 -Quick     # skip format+vocabulary
pwsh tools/ci/gates.ps1 -Nightly   # add the slow Category=Nightly corpus
```

The same stage list `ci.yml` runs, in the same order, with the same filters. **This is the
primary gate now.** On 2026-08-13 cloud CI stopped running per-push — 436 runs over the Aug 1-13
billing cycle burned 1756 of the 2000 free minutes, and per-run cost is fixed at ~3.5 billed
minutes, so the only lever was run count. Actions now runs once daily on a schedule plus
`workflow_dispatch`; see the block at the top of `ci.yml` for the full measurement.

Pairs with `unity-compile.ps1` below — they cover different halves, and neither substitutes for
the other. This one gates the pure-.NET core; that one gates the Unity-side assemblies no cloud
stage has ever touched.

**If Stage 1 fails with `ENDOFLINE: Replace 2 characters with '\n'`,** a tool on your box wrote
CRLF into a `.cs` file after checkout. `.gitattributes` pins `*.cs text eol=lf`, which makes git
normalize on *comparison* — so the file reads as clean in `git status` while `dotnet format`
reads the raw bytes and fails. `git status` will not find these for you. Find them with:

```powershell
Get-ChildItem src,tests -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and
                 [System.IO.File]::ReadAllBytes($_.FullName) -contains 13 }
```

then rewrite each with `[System.IO.File]::WriteAllText($p, (Get-Content -Raw $p) -replace "`r`n","`n")`.
Seven files were in this state on 2026-08-13. Note `grep`/`rg` under Git Bash reported them as
clean — trust the byte read.

## Unity compile gate — run this before every push

```powershell
pwsh tools/ci/unity-compile.ps1
```

Compiles the 12 `Emberfall.*` assemblies through `dotnet`. **No Editor, no Unity licence, no
domain reload**, about a minute. Exits nonzero listing each assembly that fails and one line per
distinct error.

**Why it is not optional.** Cloud CI runs `ubuntu-latest` and licence-free, so Stages 7–8 are
informational and *nothing* gates the Unity-side C#. `Emberfall.Presentation`, `.Editor`, `.AI`,
`.Data` and `.Session` are compiled by Unity and by nothing else — code passes every cloud stage
and still puts the Editor in Safe Mode. That is not hypothetical: on 2026-08-06 an
`InternalsVisibleTo` declared in a `.csproj` (invisible to Unity, which builds from the
`.asmdef`) plus three call sites left on APIs the NOR-31 refactor retired did exactly that, and
surfaced as a Burst *"Failed to resolve assembly"* and a vanished `Emberfall/` menu.

It also works while the Editor is open, which `unity-verify.ps1` does not — the Editor is a
single-writer resource.

**Limits.** It compiles; it does not run. It cannot work on a licence-free runner, because the
generated `.csproj` files hardcode a local Unity install path — if that path is wrong the script
warns rather than reporting hundreds of fake errors. And it needs those `.csproj` files to
exist, so a never-opened clone must open the project once first.

## Headless Unity verification (EF-001)

```powershell
pwsh tools/ci/unity-verify.ps1
```

The fuller gate: drives a real headless Editor and runs `HeadlessVerify.RunAll()`. Slower, needs
a seat and an idle Editor. Run before a release; run `unity-compile.ps1` before every push.

## Combined compile + test gate (cached)

```powershell
pwsh tools/ci/unity-gate.ps1
```

Runs `unity-compile.ps1` then, only if that passes, `unity-verify.ps1` — and writes the result
to `.agent/unity-gate.json` (gitignored, regenerated every run): timestamp, commit SHA, pass/fail
per stage, failed assembly/check names, and whether `unity/Emberfall` had uncommitted changes at
run time. Exits nonzero if either stage fails. Skips the verify stage on a compile failure rather
than queuing for the single-writer Editor over a break it already caught.

This is what the `/verify` skill runs when no fresh cached result exists for the current commit —
see `.claude/skills/verify/SKILL.md`. Run it directly the same way you'd run either gate alone.

