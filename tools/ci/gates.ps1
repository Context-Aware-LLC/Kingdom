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

# Stage 2 is ADVISORY and reports PASS on a red suite; these carry what it found down to the
# summary at the bottom, which is the only part of the run anyone reads. See the long note on
# Stage 2 -- all of this retires with the LASTEXITCODE reset it exists to compensate for.
$testRed = 0
$testTotal = 0
$testSawSummary = $false
$testRedNames = [System.Collections.Generic.List[string]]::new()

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
# (`CardValidation/`, plus the Unity authoring model the old csproj compiled in). Five projects,
# 1467 tests. The archive at C:\Users\User\Documents\emberfall-test-archive is history now, not the
# live copy.
#
# ADVISORY, and that flag is the reason to read this. 23 tests fail on the day they returned, and
# none of them is a migration artefact — they are the effect-model fork and the numeric card id
# disagreeing with behaviour the suite was written against in July. Blocking on them would block
# every merge on work nobody has scheduled.
#
# RETIREMENT TRIGGER: when `dotnet test Emberfall.slnx` is green, drop the LASTEXITCODE reset below
# so the stage can fail the run — and cut the same flag out of ci.yml's Stage 2, which carries a
# copy of this note.
Invoke-Stage 'Stage 2 · .NET test suites (ADVISORY — see note above)' {
    dotnet test Emberfall.slnx -c Release --no-build --nologo 2>&1 | Tee-Object -Variable testOut

    # THE NAG, and it exists because of the reset at the bottom of this block. That reset makes
    # this stage print PASS on a red suite, and the failing test names scroll past under a green
    # header in the middle of a long run -- so the one list that decides when the ADVISORY flag
    # can go is the list nobody reads. Sam, 2026-08-30: "yep nag is good", "output fail text at
    # the end of the run".
    #
    # It only COLLECTS here. The printing is in the summary block at the bottom of this file,
    # next to the gate verdict, because that is the part of a 90-second run anyone actually
    # reads.
    #
    # A PARSE THAT MATCHES NOTHING SAYS SO. Never let this report "0 red" because dotnet's
    # summary line moved -- a fabricated zero retires the flag on a suite nobody checked.
    #
    # RETIREMENT TRIGGER: this block and its printer both die with the reset they are about.
    # When the suite is green and you drop `$global:LASTEXITCODE = 0`, delete the collection
    # here AND the "STAGE 2" section of the summary in the same commit -- the stage will fail on
    # its own from then on, and a second voice saying so is noise.
    foreach ($line in $testOut) {
        $t = "$line"
        if ($t -match 'Failed:\s*(\d+),\s*Passed:\s*\d+,\s*Skipped:\s*\d+,\s*Total:\s*(\d+)') {
            $script:testSawSummary = $true
            $script:testRed   += [int]$Matches[1]
            $script:testTotal += [int]$Matches[2]
        }
        # The per-test failure lines, so the summary can name them rather than just count them.
        # Both spellings: VSTest prints "  Failed Name [12 ms]", MTP prints "failed Name".
        elseif ($t -match '^\s{2,}(?:Failed|failed)\s+(\S.*?)(?:\s+\[[\d.]+\s*m?s\])?\s*$') {
            $null = $script:testRedNames.Add($Matches[1].Trim())
        }
    }

    $global:LASTEXITCODE = 0
}

# What runs below was never in tests/ and carries weight the suite does not: CardCi validates every
# row of every card set, NetSoak plays live networked matches and asserts zero resyncs, and the
# drift guard, format, stylelint and warnings-as-errors are all untouched.

Invoke-Stage 'Stage 5b · Networked AI soak' {
    dotnet run --project src/Emberfall.Tools.NetSoak -c Release --no-build -- liveai 5
}
Invoke-Stage 'Stage 6 · Content C-series card gate' {
    dotnet run --project src/Emberfall.Tools.CardCi -c Release --no-build -- unity/Emberfall/Assets/CardData unity/Emberfall/Assets/Art
}
Invoke-Stage 'Stage 6c · Training exporter tests' {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:EMBERFALL_TRAINING_PYTHON) {
        $candidates.Add($env:EMBERFALL_TRAINING_PYTHON)
    }
    $candidates.Add('python3')
    $unslothPython = Join-Path $env:USERPROFILE '.unsloth/studio/unsloth_studio/Scripts/python.exe'
    if (Test-Path -LiteralPath $unslothPython) {
        $candidates.Add($unslothPython)
    }
    $exportPython = $null
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate) -and
            -not (Get-Command $candidate -ErrorAction SilentlyContinue)) {
            continue
        }
        & $candidate -c 'import numpy' 2>$null
        if ($LASTEXITCODE -eq 0) {
            $exportPython = $candidate
            break
        }
    }
    if (-not $exportPython) {
        throw 'No Python with NumPy found; set EMBERFALL_TRAINING_PYTHON'
    }
    & $exportPython tests/python/test_training_exporters.py -v
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

# STAGE 2 -- the advisory suite, reported here because its own stage cannot fail and its output
# scrolled past a hundred lines ago. Retires with the LASTEXITCODE reset in Stage 2; delete both
# together. Skipped under -Quick, where Stage 2 does not run at all.
if (-not $Quick) {
    if (-not $testSawSummary) {
        Write-Host "STAGE 2  no test summary line matched — dotnet's output format moved." -ForegroundColor Yellow
        Write-Host "         Fix the parse in Stage 2. Do NOT read this as a green suite." -ForegroundColor Yellow
        Write-Host ""
    } elseif ($testRed -gt 0) {
        Write-Host ("STAGE 2  {0} of {1} tests RED — the stage above still said PASS, by design." -f $testRed, $testTotal) -ForegroundColor Red
        if ($testRedNames.Count -gt 0) {
            $testRedNames | Sort-Object -Unique | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
        } else {
            Write-Host "   (names not parsed — scroll up for dotnet's own list)" -ForegroundColor DarkGray
        }
        Write-Host  "         At zero: drop the LASTEXITCODE reset in Stage 2, cut the matching flag" -ForegroundColor Red
        Write-Host  "         from ci.yml Stage 2, and delete this section." -ForegroundColor Red
        Write-Host ""
    } else {
        Write-Host ("STAGE 2  {0}/{0} GREEN. The retirement trigger is DUE: drop the LASTEXITCODE" -f $testTotal) -ForegroundColor Green
        Write-Host  "         reset in Stage 2, cut the matching flag from ci.yml Stage 2, and delete" -ForegroundColor Green
        Write-Host  "         this section. Cheaper now than the next person rediscovering it." -ForegroundColor Green
        Write-Host ""
    }
}

if ($failed.Count -eq 0) {
    Write-Host ("ALL GATES PASS  ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    exit 0
}
Write-Host ("{0} GATE(S) FAILED  ({1:N1}s)" -f $failed.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Red
$failed | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
exit 1
