#!/usr/bin/env pwsh
#
# EF-046 (NOR-74) DONE-WHEN harness.
#
#   "a scripted self-check creates a character, builds a legal 30-card deck, saves,
#    reloads it from disk in a fresh session, and reports queue-eligible"
#
# The load-bearing phrase is "in a fresh session". This script therefore invokes
# Emberfall.Tools.CollectionSelfCheck TWICE, as TWO SEPARATE PROCESSES, against one collection
# root. The build process exits — taking every store instance, every static and every cached
# byte with it — before the verify process is started. A single process that news up a second
# store proves less: a static cache would survive it and the check would still pass.
#
# The two pids printed below are the evidence, and they are why this is a script rather than a
# [Fact] in Emberfall.Core.Tests. An in-process fixture cannot make this claim.
#
# Usage:  pwsh tools/ci/deckbuilder-selfcheck.ps1
#         pwsh tools/ci/deckbuilder-selfcheck.ps1 -Root C:\some\dir   # keep the artifacts
#
# Exit code is the verdict: 0 pass, non-zero fail.

[CmdletBinding()]
param(
    # Collection root to build into. Defaults to a fresh temp dir that is removed on success.
    [string] $Root,

    # Card data directory. Defaults to the tool's own repo-root discovery.
    [string] $Cards
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project  = Join-Path $repoRoot 'src/Emberfall.Tools.CollectionSelfCheck/Emberfall.Tools.CollectionSelfCheck.csproj'

$ephemeral = -not $Root
if ($ephemeral) {
    $Root = Join-Path ([System.IO.Path]::GetTempPath()) ("ef046-selfcheck-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
}

# A reused root would let the verify phase read a PREVIOUS run's files and pass without the build
# phase having done anything. Start from nothing, always.
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force -Path $Root | Out-Null

Write-Host "== EF-046 deckbuilder self-check ==" -ForegroundColor Cyan
Write-Host "repo root : $repoRoot"
Write-Host "root      : $Root"
Write-Host ""

# Build once, up front, so the two phase runs are plain executions and the compiler's output does
# not interleave with the evidence.
Write-Host "-- building tool --" -ForegroundColor DarkGray
& dotnet build $project -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "tool failed to build"; exit 1 }
Write-Host ""

$common = @('--root', $Root)
if ($Cards) { $common += @('--cards', $Cards) }

function Invoke-Phase {
    param([string] $Phase)

    Write-Host "-- process: --phase $Phase --" -ForegroundColor Yellow

    # Out-Host, not a bare call: a PowerShell function returns EVERY uncaptured stream, so letting
    # the child's stdout fall through would make the caller's `$code` an array of output lines with
    # the exit code stapled on the end — and `$code -ne 0` on an array filters instead of comparing,
    # which reads as a failure on a passing run.
    & dotnet run --project $project --no-build -- @common --phase $Phase 2>&1 | Out-Host
    $code = $LASTEXITCODE
    Write-Host ""
    return $code
}

$buildCode = Invoke-Phase 'build'
if ($buildCode -ne 0) {
    Write-Host "SELF-CHECK FAILED in the build phase (exit $buildCode)." -ForegroundColor Red
    exit $buildCode
}

# The build process is gone by here. Anything the verify process sees came off the filesystem.
$verifyCode = Invoke-Phase 'verify'
if ($verifyCode -ne 0) {
    Write-Host "SELF-CHECK FAILED in the verify phase (exit $verifyCode)." -ForegroundColor Red
    Write-Host "artifacts left for inspection: $Root" -ForegroundColor DarkGray
    exit $verifyCode
}

Write-Host "== EF-046 DONE-WHEN DISCHARGED ==" -ForegroundColor Green
Write-Host "two processes, one collection root, deck reloaded from disk and queue-eligible."

if ($ephemeral) { Remove-Item -Recurse -Force $Root }
exit 0
