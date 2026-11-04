# Combined Unity gate: unity-compile.ps1 (fast, no Editor) then unity-verify.ps1 (headless
# Editor, compile + HeadlessVerify.RunAll tests), with the result written as JSON so a session
# can check the last known outcome for this commit before paying for another run.
#
# WHY A WRAPPER INSTEAD OF ONE BIGGER SCRIPT. unity-compile.ps1 and unity-verify.ps1 each exist
# for a reason documented in their own headers (tools/ci/README.md) and are run standalone too;
# this script composes them rather than re-implementing either. It fails fast on a compile
# break — the verify stage costs a domain reload and the Editor is a single-writer resource
# (CLAUDE.md), so there is no reason to queue for it when the compile stage already failed.
#
# Both are invoked as separate `pwsh` PROCESSES, not dot-sourced or call-operator'd in-process:
# each ends with `exit N`, and `exit` inside a script pulled into the same process via `&`
# terminates the whole process, not just that script's scope. A child process's `exit` only
# ends the child; $LASTEXITCODE then reports it correctly to this script.
#
# Usage: pwsh tools/ci/unity-gate.ps1
#        pwsh tools/ci/unity-gate.ps1 -Configuration Release -UnityPath 'C:\...\Unity.exe'
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    # Hub's default install location, which differs per platform (and the Linux binary has no
    # .exe). Override with -UnityPath for any non-Hub install.
    [string]$UnityPath = $(if ($IsWindows) { 'C:\Program Files\Unity\Hub\Editor\6000.7.0b7\Editor\Unity.exe' }
                           else            { "$HOME/Unity/Hub/Editor/6000.7.0b7/Editor/Unity" })
)

$ErrorActionPreference = 'Stop'

# Path.Combine, never a literal '\' - a backslash is a legal filename character on Linux.
$REPO_ROOT = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))
$AGENT_DIR = Join-Path $REPO_ROOT '.agent'
$OUT_PATH  = Join-Path $AGENT_DIR 'unity-gate.json'

function Strip-Ansi([string[]]$lines) {
    $lines | ForEach-Object { $_ -replace "`e\[[0-9;]*m", '' }
}

function Invoke-GateScript([string]$Name, [string[]]$ScriptArgs) {
    $scriptPath = Join-Path $PSScriptRoot $Name
    $raw = & pwsh -NoProfile -File $scriptPath @ScriptArgs 2>&1 | ForEach-Object { $_.ToString() }
    [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output   = Strip-Ansi $raw
    }
}

$startedAt = Get-Date

$commit      = (& git -C $REPO_ROOT rev-parse HEAD).Trim()
$commitShort = (& git -C $REPO_ROOT rev-parse --short HEAD).Trim()
$dirtyUnity  = @(& git -C $REPO_ROOT status --porcelain -- unity | ForEach-Object { $_.Trim() })

Write-Host "unity-gate: commit $commitShort  $(if ($dirtyUnity.Count -gt 0) { "($($dirtyUnity.Count) uncommitted unity path(s) — gate reflects HEAD, not the working tree)" } else { '(clean)' })" -ForegroundColor Cyan

# ── Stage 1: compile ─────────────────────────────────────────────────────────
Write-Host "unity-gate: running unity-compile.ps1..." -ForegroundColor Cyan
$compileRun = Invoke-GateScript 'unity-compile.ps1' @('-Configuration', $Configuration)
$compileRun.Output | ForEach-Object { Write-Host "  $_" }

$compilePass = ($compileRun.ExitCode -eq 0)
$compileSummary = $compileRun.Output | Select-String -Pattern '^(PASS - all (\d+) Kingdom assemblies compile\.)|(FAIL - (\d+) of (\d+) assemblies do not compile: (.+))$' | Select-Object -Last 1

$compileStage = [ordered]@{
    pass             = $compilePass
    exitCode         = $compileRun.ExitCode
    totalAssemblies  = $null
    failedAssemblies = @()
}
if ($compileSummary) {
    $m = $compileSummary.Matches[0]
    if ($m.Groups[2].Success) {
        $compileStage.totalAssemblies = [int]$m.Groups[2].Value
    } elseif ($m.Groups[5].Success) {
        $compileStage.totalAssemblies = [int]$m.Groups[5].Value
        $compileStage.failedAssemblies = @($m.Groups[6].Value -split ', ' | Where-Object { $_ })
    }
}

# ── Stage 2: headless verify (skipped if compile already failed) ────────────
if (-not $compilePass) {
    Write-Host "unity-gate: compile failed — skipping unity-verify.ps1 (Editor is single-writer, not worth queuing for it)." -ForegroundColor Yellow
    $verifyStage = [ordered]@{
        pass             = $null
        skipped          = $true
        exitCode         = $null
        checks           = @()
        hasCompileErrors = $null
        compileErrorLines = @()
        reasons          = @()
        logPath          = $null
    }
} else {
    Write-Host "unity-gate: running unity-verify.ps1..." -ForegroundColor Cyan
    $verifyRun = Invoke-GateScript 'unity-verify.ps1' @('-UnityPath', $UnityPath)
    $verifyRun.Output | ForEach-Object { Write-Host "  $_" }

    $checks = [System.Collections.Generic.List[PSCustomObject]]::new()
    foreach ($line in $verifyRun.Output) {
        if ($line -match '^unity-verify:\s+(PASS|FAIL)\s+(.+)$') {
            $checks.Add([PSCustomObject]@{ name = $Matches[2].Trim(); result = $Matches[1] })
        }
    }

    $compileErrorLines = @($verifyRun.Output | Where-Object {
        $_ -match 'error CS\d+' -or
        $_ -match 'Assembly compilation failed' -or
        $_ -match '^ERROR in .*\.cs\(' -or
        $_ -match 'CompilerOutput:-stderr>'
    } | Select-Object -Unique)

    $logLine = $verifyRun.Output | Select-String -Pattern '^unity-verify: log\s+(.+)$' | Select-Object -Last 1
    $logPath = if ($logLine) { $logLine.Matches[0].Groups[1].Value.Trim() } else { $null }

    $reasonsLine = $verifyRun.Output | Select-String -Pattern '^unity-verify: FAILED — (.+)$' | Select-Object -Last 1
    $reasons = if ($reasonsLine) { @($reasonsLine.Matches[0].Groups[1].Value -split ', ') } else { @() }

    $verifyStage = [ordered]@{
        pass              = ($verifyRun.ExitCode -eq 0)
        skipped           = $false
        exitCode          = $verifyRun.ExitCode
        checks            = $checks
        hasCompileErrors  = ($compileErrorLines.Count -gt 0)
        compileErrorLines = $compileErrorLines
        reasons           = $reasons
        logPath           = $logPath
    }
}

$overallPass = $compileStage.pass -and ($verifyStage.pass -eq $true)
$finishedAt  = Get-Date

$result = [ordered]@{
    timestamp        = $startedAt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    commit           = $commit
    commitShort      = $commitShort
    workingTreeDirty = ($dirtyUnity.Count -gt 0)
    dirtyPaths       = $dirtyUnity
    pass             = $overallPass
    durationSeconds  = [math]::Round(($finishedAt - $startedAt).TotalSeconds, 1)
    stages           = [ordered]@{
        compile = $compileStage
        verify  = $verifyStage
    }
}

New-Item -ItemType Directory -Force -Path $AGENT_DIR | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -Path $OUT_PATH -Encoding utf8

Write-Host ''
if ($overallPass) {
    Write-Host "unity-gate: PASS — result written to $OUT_PATH" -ForegroundColor Green
    exit 0
} else {
    Write-Host "unity-gate: FAIL — result written to $OUT_PATH" -ForegroundColor Red
    exit 1
}
