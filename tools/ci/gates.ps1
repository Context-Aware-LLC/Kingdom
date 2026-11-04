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

# The first Python that actually RUNS and can import what the stage needs, or $null.
#
# IT READS WHAT THE INTERPRETER PRINTS, NEVER $LASTEXITCODE. On this machine `python3` resolves
# to C:\Users\User\bin\python3, an extensionless sh shim written for Git Bash. PowerShell cannot
# execute it: `& python3 file.py` returns at once, runs nothing, prints nothing, and leaves
# $LASTEXITCODE holding whatever the PREVIOUS command set. So after a passing stage the old
# `import numpy` probe read a stale 0, chose python3, and three stages "passed" in 0.2s having
# run no tests (measured 2026-10-07: ALL GATES PASS with the exporter, resolver and trainer
# suites silent). After a FAILING stage the same probe read a stale 1 and fell through to a
# real interpreter, which is why the suites appeared to run some days and not others.
function Find-Python {
    param([string]$Import = '')
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:KINGDOM_TRAINING_PYTHON) {
        $candidates.Add($env:KINGDOM_TRAINING_PYTHON)
    }
    # The old name, until this machine's variable is renamed (src/Kingdom.Net.Server/KingdomEnv.cs).
    if ($env:EMBERFALL_TRAINING_PYTHON) {
        $candidates.Add($env:EMBERFALL_TRAINING_PYTHON)
    }
    $candidates.Add('python3')
    $candidates.Add('python')
    $unslothPython = Join-Path $env:USERPROFILE '.unsloth/studio/unsloth_studio/Scripts/python.exe'
    if (Test-Path -LiteralPath $unslothPython) {
        $candidates.Add($unslothPython)
    }
    $probe = "print('emberfall-python-ok')"
    if ($Import) { $probe = "import $Import; $probe" }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate) -and
            -not (Get-Command $candidate -ErrorAction SilentlyContinue)) {
            continue
        }
        $out = $null
        try { $out = & $candidate -c $probe 2>$null } catch { }
        if ("$out".Trim() -eq 'emberfall-python-ok') { return $candidate }
    }
    return $null
}

Invoke-Stage 'Restore' { dotnet restore Kingdom.slnx }
Invoke-Stage 'Build (Release)' { dotnet build Kingdom.slnx -c Release --no-restore }
Invoke-Stage 'Android build harness' {
    pwsh -NoProfile -File tools/ci/test-android-build-support.ps1
}

if (-not $Quick) {
    Invoke-Stage 'Stage 1 · Lint (dotnet format)' {
        dotnet format Kingdom.slnx --verify-no-changes --no-restore
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
        npx stylelint "unity/Assets/UI/**/*.uss"
    }

    # The same failure mode one file type over, and the one with no log line at all. Unity parses
    # a .uxml as XML and, on a parse error, hands the UIDocument an EMPTY rootVisualElement --
    # silently. It reads as a wiring bug when the wiring is fine. These files carry long design
    # comments ABOUT CSS, so they quote custom properties, and a doubled hyphen inside an XML
    # comment is illegal: AccountCreateScreen.uxml shipped `var(<two hyphens>edge-safe)` in a
    # comment on 2026-08-29 and a first launch was a blank screen after the splash.
    Invoke-Stage 'Stage 1c · UXML well-formedness' {
        python tooling/scripts/check_uxml_wellformed.py
    }
}

# Stage 1's rebaseline-guard is gone entirely as of 2026-08-24, here and in ci.yml: it guarded a
# `*.replay.json` expected block against being rewritten without its sibling `*.rebaseline.md`,
# and the golden replay fixtures were deleted that day (Sam: they were fake).

# Stage 2 — the .NET test suites.
#
# Archived out 2026-08-18, brought BACK 2026-08-24 at Sam's direction, minus the card-authoring block
# (`CardValidation/`, plus the Unity authoring model the old csproj compiled in). Every test project runs.
# The archive at C:\Users\User\Documents\emberfall-test-archive is history now, not the live copy.
#
# BLOCKING since 2026-08-30, which is what it now looks like: no LASTEXITCODE reset, so a red suite
# fails the run through Invoke-Stage like every other stage. It was ADVISORY from the day it
# returned — 23 tests failed then, the effect-model fork and the numeric card id disagreeing with
# behaviour the suite was written against in July, and blocking on those would have blocked every
# merge on work nobody had scheduled. The run that discharged the trigger read `Failed: 0` on all
# five projects: 1490 passed, 22 skipped.
#
# THE NAG THAT COMPENSATED FOR THE RESET DIED WITH IT — the per-test name collection that used to
# live in this block, and the "STAGE 2" section of the summary at the bottom of this file. The
# stage names its own failures now, and a second voice saying so was only ever noise.
#
# A `dotnet test` MATCHING ZERO TESTS EXITS 0, so blocking is not the same as verified: every test
# project must stay registered in Kingdom.slnx or this stage is green for the wrong reason.
# Read the count, not the badge.
Invoke-Stage 'Stage 2 · .NET test suites' {
    dotnet test Kingdom.slnx -c Release --no-build --nologo
}

# What runs below was never in tests/ and carries weight the suite does not: CardCi validates every
# row of every card set, NetSoak plays live networked matches and asserts zero resyncs, and the
# drift guard, format, stylelint and warnings-as-errors are all untouched.

Invoke-Stage 'Stage 5b · Networked AI soak' {
    dotnet run --project src/Kingdom.Tools.NetSoak -c Release --no-build -- liveai 5
}
Invoke-Stage 'Stage 6 · Content C-series card gate' {
    dotnet run --project src/Kingdom.Tools.CardCi -c Release --no-build -- unity/Assets/CardData unity/Assets/Art --translations unity/Assets/Resources/Localization
}
Invoke-Stage 'Stage 6c · UI edge-clearance audit' {
    python tooling/scripts/audit_edge_safety.py
    if ($LASTEXITCODE -ne 0) { throw "edge audit exit $LASTEXITCODE" }
    python tests/python/test_audit_edge_safety.py -v
}
Invoke-Stage 'Stage 6c2 · C# graph resolver tests' {
    $resolverPython = Find-Python
    if (-not $resolverPython) {
        throw 'No runnable Python found; set KINGDOM_TRAINING_PYTHON'
    }
    & $resolverPython tests/python/test_csharp_resolver.py -v
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
