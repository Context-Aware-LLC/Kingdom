#!/usr/bin/env pwsh
# The .NET determinism gates, run locally. This is the same stage list ci.yml runs, in the same
# order, with the same filters — deliberately duplicated rather than shared, because the workflow
# has to stand alone on a fresh runner and this has to stand alone with no network.
#
# WHY THIS EXISTS. On 2026-08-13 the cloud pipeline stopped running per-push: 436 runs over the
# Aug 1-13 billing cycle burned 1756 of the 2000 free minutes, and at ~3.5 billed minutes per run
# the only lever left was run count (see ci.yml's `on:` block). Cloud CI is now once a day plus
# manual dispatch. That trade only works because the gates are free here: .NET 10 is installed
# locally, the suite is millisecond-scale, and a full pass costs about ninety seconds of your own
# machine instead of four minutes of a metered runner.
#
#   pwsh tools/ci/gates.ps1              # everything below
#   pwsh tools/ci/gates.ps1 -Quick       # skip the lint stages, just build and run the tools
#   pwsh tools/ci/gates.ps1 -Nightly     # (inert — the Nightly corpus left with the test suites)
#
# Run this before a push the way you already run unity-compile.ps1. They cover different halves:
# this one gates the pure-.NET core, unity-compile.ps1 gates the Unity-side assemblies that no
# cloud stage has ever touched.

[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$Nightly
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repo

$failed = [System.Collections.Generic.List[string]]::new()
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Body)
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host ""
    Write-Host "── $Name " -NoNewline -ForegroundColor Cyan
    Write-Host ("─" * [Math]::Max(1, 60 - $Name.Length)) -ForegroundColor DarkGray
    try {
        & $Body
        if ($LASTEXITCODE -ne 0) { throw "exit $LASTEXITCODE" }
        Write-Host ("   PASS  {0:N1}s" -f $t.Elapsed.TotalSeconds) -ForegroundColor Green
    } catch {
        Write-Host ("   FAIL  {0:N1}s  — {1}" -f $t.Elapsed.TotalSeconds, $_) -ForegroundColor Red
        $failed.Add($Name)
    }
}

Invoke-Stage 'Restore' { dotnet restore Emberfall.slnx }
Invoke-Stage 'Build (Release)' { dotnet build Emberfall.slnx -c Release --no-restore }

if (-not $Quick) {
    Invoke-Stage 'Stage 1 · Lint (dotnet format)' {
        dotnet format Emberfall.slnx --verify-no-changes --no-restore
    }

    # Gates USS PARSEABILITY, not style — .stylelintrc.json carries zero rules on purpose. Unity
    # discards an entire stylesheet on one malformed declaration, silently: every rule in the file
    # stops applying with nothing logged, so it presents as a layout bug rather than a syntax one
    # and you debug the wrong thing. collection.uss shipped three of them (a missing semicolon, a
    # stray character) before this existed. stylelint-config-standard was tried and rejected: ~40
    # cosmetic hits on a clean file plus genuine false positives on `-unity-*` values, and a gate
    # nobody reads is not a gate. Parse errors are reported regardless of the rule set, so zero
    # rules is the whole check.
    Invoke-Stage 'Stage 1b · USS syntax (stylelint)' {
        if (-not (Test-Path "$repo/node_modules/stylelint")) {
            Write-Host "   stylelint is not installed — run 'npm install' at the repo root" -ForegroundColor Yellow
            throw "node_modules/stylelint missing"
        }
        npx stylelint "unity/Emberfall/Assets/UI/**/*.uss"
    }
}

# Stage 1's rebaseline-guard is deliberately absent: it diffs against a base ref that only means
# something for a PR. Run it by hand as `bash tools/ci/rebaseline-guard.sh origin/master` when
# you have touched a golden fixture.

# Stages 2, 3, 3b, 4, 5 and Nightly ran the .NET test suites. Those suites were ARCHIVED OUT of the
# repo 2026-08-18 (Sam's ruling) to C:\Users\User\Documents\emberfall-test-archive — 1250 tests that
# were scaffolding for agent-authored changes and had become a bigger failure surface than the code.
# Nothing here comes back until something actually breaks and an archived test would have caught it;
# then that ONE test returns. See the archive's README.
#
# What still runs below is the part that was never in tests/ and carries the real weight: CardCi
# validates every row of every card set, NetSoak plays live networked matches and asserts zero
# resyncs, and the drift guard, format, stylelint and warnings-as-errors are all untouched.

Invoke-Stage 'Stage 5b · Networked AI soak' {
    dotnet run --project src/Emberfall.Tools.NetSoak -c Release --no-build -- liveai 5
}
Invoke-Stage 'Stage 6 · Content C-series card gate' {
    dotnet run --project src/Emberfall.Tools.CardCi -c Release --no-build -- unity/Emberfall/Assets/CardData unity/Emberfall/Assets/Art
}
# Sams Migration FLag
Invoke-Stage 'Stage 6c · Training exporter tests' {
    python3 tests/python/test_training_exporters.py -v
}
Invoke-Stage 'Stage 6c2 · C# graph resolver tests' {
    python3 tests/python/test_csharp_resolver.py -v
}
Invoke-Stage 'Stage 6d · Training trainer tests' {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:EMBERFALL_TRAINING_PYTHON) {
        $candidates.Add($env:EMBERFALL_TRAINING_PYTHON)
    }
    $candidates.Add('python3')
    $unslothPython = Join-Path $env:USERPROFILE '.unsloth/studio/unsloth_studio/Scripts/python.exe'
    if (Test-Path -LiteralPath $unslothPython) {
        $candidates.Add($unslothPython)
    }
    $trainingPython = $null
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate) -and
            -not (Get-Command $candidate -ErrorAction SilentlyContinue)) {
            continue
        }
        & $candidate -c 'import numpy, torch' 2>$null
        if ($LASTEXITCODE -eq 0) {
            $trainingPython = $candidate
            break
        }
    }
    if (-not $trainingPython) {
        throw 'No Python with NumPy and Torch found; set EMBERFALL_TRAINING_PYTHON'
    }
    $previousRequirement = $env:EMBERFALL_REQUIRE_TORCH
    try {
        $env:EMBERFALL_REQUIRE_TORCH = '1'
        & $trainingPython tests/python/test_training_trainer.py -v
    } finally {
        if ($null -eq $previousRequirement) {
            Remove-Item Env:EMBERFALL_REQUIRE_TORCH -ErrorAction SilentlyContinue
        } else {
            $env:EMBERFALL_REQUIRE_TORCH = $previousRequirement
        }
    }
}

if ($Nightly) {
    Write-Host ""
    Write-Host "-Nightly has nothing to run: the Category=Nightly corpus left with the .NET test" -ForegroundColor Yellow
    Write-Host "suites on 2026-08-18 (C:\Users\User\Documents\emberfall-test-archive)." -ForegroundColor Yellow
}

Pop-Location

Write-Host ""
Write-Host ("═" * 64) -ForegroundColor DarkGray
if ($failed.Count -eq 0) {
    Write-Host ("ALL GATES PASS  ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    exit 0
}
Write-Host ("{0} GATE(S) FAILED  ({1:N1}s)" -f $failed.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Red
$failed | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
exit 1
