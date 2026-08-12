# EF-001 · Headless Unity verification harness
#
# Runs the Editor headless, compiles, executes HeadlessVerify.RunAll() over the four local
# self-checks, and exits non-zero on any compile error or check failure.
#
# IMPORTANT: compile errors arrive in the Unity log typed as Log, not Error.
# This script keys off message text, not log severity, to catch them.
#
# Usage: pwsh tools/ci/unity-verify.ps1
#        pwsh tools/ci/unity-verify.ps1 -UnityPath "C:\path\to\Unity.exe"
[CmdletBinding()]
param(
    # Hub's default install location, which differs per platform (and the Linux binary has no
    # .exe). Override with -UnityPath for any non-Hub install. The version is pinned to match
    # unity/Emberfall/ProjectSettings/ProjectVersion.txt; a mismatched Editor compiles against
    # different assemblies and makes this gate report on something you do not ship.
    [string]$UnityPath = $(if ($IsWindows) { 'C:\Program Files\Unity\Hub\Editor\6000.5.1f1\Editor\Unity.exe' }
                           else            { "$HOME/Unity/Hub/Editor/6000.5.1f1/Editor/Unity" })
)

$ErrorActionPreference = 'Stop'

# Path.Combine, never a literal '\': a backslash is a legal FILENAME character on Linux, so
# 'unity\Emberfall' there is one odd filename rather than a path, and this fails below as
# "Unity project not found" as though the checkout were broken.
$REPO_ROOT  = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))
$PROJECT    = [System.IO.Path]::Combine($REPO_ROOT, 'unity', 'Emberfall')
$TIMESTAMP  = (Get-Date -Format 'yyyyMMdd-HHmmss')
$LOG        = Join-Path ([System.IO.Path]::GetTempPath()) "unity-verify-$TIMESTAMP.log"

if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity not found at '$UnityPath'. Pass -UnityPath or install Unity 6000.5.1f1."
    exit 1
}

if (-not (Test-Path $PROJECT)) {
    Write-Error "Unity project not found at '$PROJECT'."
    exit 1
}

$lockFile = [System.IO.Path]::Combine($PROJECT, 'Temp', 'UnityLockfile')
if (Test-Path $lockFile) {
    Write-Error "Unity project is locked ('$lockFile'). Close the Editor or remove a confirmed-stale lock manually; this harness never clears project locks."
    exit 1
}

# Git operations can replace a script while preserving an old timestamp. Unity's Asset Database can
# then regard a cached assembly as current, so a fresh Editor process alone is not a compile barrier.
# Advance project-owned compilation-input timestamps before startup. Unity's normal initial refresh
# will reimport these inputs and rebuild scripts without force-refreshing unrelated assets.
$compileRoots = @(
    (Join-Path $PROJECT 'Assets'),
    (Join-Path $PROJECT 'Packages')
)
$compileExtensions = @('.cs', '.asmdef', '.asmref', '.rsp')
$compileInputs = @(
    foreach ($root in $compileRoots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
                $compileExtensions -contains $_.Extension.ToLowerInvariant()
            }
        }
    }
)

if ($compileInputs.Count -eq 0) {
    Write-Error "No project-owned Unity compilation inputs were found under Assets or Packages."
    exit 1
}

$compileBarrierUtc = [DateTime]::UtcNow
foreach ($compileInput in $compileInputs) {
    [System.IO.File]::SetLastWriteTimeUtc($compileInput.FullName, $compileBarrierUtc)
}

Write-Host "unity-verify: Unity  $UnityPath"
Write-Host "unity-verify: project $PROJECT"
Write-Host "unity-verify: log    $LOG"
Write-Host "unity-verify: compile barrier $($compileBarrierUtc.ToString('O')) ($($compileInputs.Count) input(s))"
Write-Host "unity-verify: launching headless…"
Write-Host ""

$unityArgs = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath', "`"$PROJECT`"",
    '-executeMethod', 'Emberfall.Editor.HeadlessVerify.RunAll',
    '-logFile', "`"$LOG`""
)

$proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -NoNewWindow
$finished = $proc.WaitForExit(480000)
if (-not $finished) {
    Write-Host "unity-verify: ERROR — Unity timed out after 480 seconds"
    try { $proc.Kill() } catch { Write-Host "unity-verify: WARNING — could not stop timed-out Unity process: $($_.Exception.Message)" }
    $proc.WaitForExit()
    $unityExit = 124
} else {
    $unityExit = $proc.ExitCode
}

# ── Log read ────────────────────────────────────────────────────────────────
if (-not (Test-Path $LOG)) {
    Write-Host "unity-verify: ERROR — log file was not written ($LOG)"
    exit 1
}

$logLines = Get-Content $LOG -Encoding UTF8

# ── Compile-error detection (text-based, NOT severity-based) ────────────────
# Unity reports compile errors as Log-level entries in batchmode; grep the text.
$compileErrors = $logLines | Where-Object {
    $_ -match 'error CS\d+' -or
    $_ -match 'Assembly compilation failed' -or
    $_ -match '^ERROR in .*\.cs\(' -or
    $_ -match 'CompilerOutput:-stderr>'
}

if ($compileErrors) {
    Write-Host "unity-verify: COMPILE ERRORS DETECTED"
    $compileErrors | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
}

# ── PASS/FAIL table from HeadlessVerify markers ─────────────────────────────
$checks = [System.Collections.Generic.List[PSCustomObject]]::new()
foreach ($line in $logLines) {
    if ($line -match '\[HeadlessVerify\]\s+(PASS|FAIL)\s+(.+)') {
        $checks.Add([PSCustomObject]@{
            Result = $Matches[1]
            Name   = $Matches[2].Trim()
        })
    }
}

Write-Host "unity-verify: =============================="
if ($checks.Count -gt 0) {
    foreach ($c in $checks) {
        Write-Host "unity-verify: $($c.Result.PadRight(4))  $($c.Name)"
    }
} else {
    Write-Host "unity-verify: WARNING — no check results found in log"
    Write-Host "unity-verify: last 50 log lines:"
    $logLines | Select-Object -Last 50 | ForEach-Object { Write-Host "  $_" }
}

$failedChecks = $checks | Where-Object { $_.Result -eq 'FAIL' }
$failCount    = ($failedChecks | Measure-Object).Count

if ($compileErrors -or $failCount -gt 0 -or ($checks.Count -eq 0) -or ($unityExit -ne 0)) {
    $reasons = @()
    if ($compileErrors)       { $reasons += 'compile errors' }
    if ($failCount -gt 0)     { $reasons += "$failCount check(s) FAIL" }
    if ($checks.Count -eq 0)  { $reasons += 'no check output found' }
    if ($unityExit -ne 0)     { $reasons += "Unity exited $unityExit" }
    Write-Host "unity-verify: FAILED — $($reasons -join ', ')"
    Write-Host "unity-verify: =============================="
    exit 1
} else {
    Write-Host "unity-verify: ALL PASS"
    Write-Host "unity-verify: =============================="
    exit 0
}
