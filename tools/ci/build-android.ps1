# EF-060 · Headless Android APK build harness
#
# Switches Unity to Android at startup, runs EmberfallBuild.BuildAndroid(), and exits non-zero
# unless Unity reports success and a fresh APK exists at the expected path.
#
# Usage: pwsh tools/ci/build-android.ps1
#        pwsh tools/ci/build-android.ps1 -UnityPath "C:\path\to\Unity.exe"
[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.5.1f1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'unity\Emberfall'
$apkPath = Join-Path $project 'Builds\Android\Emberfall.apk'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path ([System.IO.Path]::GetTempPath()) "unity-build-android-$timestamp.log"

if (-not (Test-Path -LiteralPath $UnityPath)) {
    Write-Error "Unity not found at '$UnityPath'. Pass -UnityPath or install Unity 6000.5.1f1."
    exit 1
}

if (-not (Test-Path -LiteralPath $project)) {
    Write-Error "Unity project not found at '$project'."
    exit 1
}

$lockFile = Join-Path $project 'Temp\UnityLockfile'
if (Test-Path -LiteralPath $lockFile) {
    Write-Error "Unity project is locked ('$lockFile'). Close the Editor first; this harness never clears project locks."
    exit 1
}

$compileRoots = @(
    (Join-Path $project 'Assets'),
    (Join-Path $project 'Packages')
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
    Write-Error 'No project-owned Unity compilation inputs were found under Assets or Packages.'
    exit 1
}

$compileBarrierUtc = [DateTime]::UtcNow
foreach ($input in $compileInputs) {
    [System.IO.File]::SetLastWriteTimeUtc($input.FullName, $compileBarrierUtc)
}

$buildStartedUtc = [DateTime]::UtcNow
Write-Host "build-android: Unity   $UnityPath"
Write-Host "build-android: project $project"
Write-Host "build-android: APK     $apkPath"
Write-Host "build-android: log     $log"
Write-Host "build-android: launching headless (IL2CPP + ARM64)..."

$unityArgs = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-buildTarget', 'android',
    '-projectPath', "`"$project`"",
    '-executeMethod', 'Emberfall.Editor.EmberfallBuild.BuildAndroid',
    '-logFile', "`"$log`""
)

$process = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -NoNewWindow
$finished = $process.WaitForExit(1800000)
if (-not $finished) {
    Write-Host 'build-android: ERROR - Unity timed out after 30 minutes'
    try { $process.Kill() } catch { Write-Host "build-android: WARNING - could not stop Unity: $($_.Exception.Message)" }
    $process.WaitForExit()
    $unityExit = 124
} else {
    $unityExit = $process.ExitCode
}

if (-not (Test-Path -LiteralPath $log)) {
    Write-Host "build-android: ERROR - log file was not written ($log)"
    exit 1
}

$logLines = Get-Content -LiteralPath $log -Encoding UTF8
$compileErrors = $logLines | Where-Object {
    $_ -match 'error CS\d+' -or
    $_ -match 'Assembly compilation failed' -or
    $_ -match '^ERROR in .*\.cs\(' -or
    $_ -match 'CompilerOutput:-stderr>'
}
$buildSucceeded = $logLines | Where-Object {
    $_ -match '\[EmberfallBuild\] Player build Succeeded'
}
$buildFailed = $logLines | Where-Object {
    $_ -match '(?i)(\[EmberfallBuild\] Player build.*failed|BuildFailedException)'
}
$apk = Get-Item -LiteralPath $apkPath -ErrorAction SilentlyContinue
$apkIsFresh = $null -ne $apk -and $apk.LastWriteTimeUtc -ge $buildStartedUtc

Write-Host 'build-android: =============================='
Write-Host 'build-android: Last 60 log lines:'
$logLines | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" }
Write-Host 'build-android: =============================='

if ($buildSucceeded -and -not $buildFailed -and -not $compileErrors -and $unityExit -eq 0 -and $apkIsFresh) {
    Write-Host "build-android: SUCCESS - APK produced ($($apk.Length) bytes)"
    Write-Host "build-android: $apkPath"
    exit 0
}

$reasons = @()
if ($compileErrors) { $reasons += 'compile errors' }
if ($buildFailed) { $reasons += 'build failure marker found' }
if (-not $buildSucceeded) { $reasons += 'no success marker found' }
if ($unityExit -ne 0) { $reasons += "Unity exited $unityExit" }
if (-not $apk) { $reasons += 'APK missing' }
elseif (-not $apkIsFresh) { $reasons += 'APK is stale' }
Write-Host "build-android: FAILED - $($reasons -join ', ')"
exit 1
