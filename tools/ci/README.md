# CI utility scripts

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
