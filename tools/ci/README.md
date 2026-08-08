# CI utility scripts

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

## Canonical-vocabulary gate

Run the repository scan with:

```powershell
pwsh tools/ci/vocabulary-gate.ps1
```

It exits nonzero for active-scope matches that are not an immutable font-license exception or a
path passed explicitly with `-MigrationNote`. The current repository is intentionally red until
the vocabulary migration lands, so this utility is not workflow-wired yet.

Exercise the scanner's temporary clean and failing fixtures with:

```powershell
pwsh tools/ci/vocabulary-gate.ps1 -SelfTest
```
