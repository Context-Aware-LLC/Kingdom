<#
.SYNOPSIS
  EF-064 - drive a frame capture on a connected Android device and pull the result.

.DESCRIPTION
  Installs the APK, launches it, waits while a human plays, backgrounds the app to force the
  flush, then pulls the CSV + summary off the device and prints the summary to stdout.

  WHY BACKGROUNDING IS THE FLUSH TRIGGER
  DevicePerfCapture writes on OnApplicationPause(true). Android does not guarantee
  OnApplicationQuit runs -- the OS can kill the process outright -- so `input keyevent HOME`
  is the only reliable way to make the app write its capture from the host side.

  WHAT THIS CANNOT GIVE YOU
  Draw calls, batches and SetPass calls. They are not readable from script in a player build
  (UnityStats is Editor-only). For those, use one of:
    * Unity Profiler over adb:
        adb forward tcp:34999 localabstract:Unity-<packagename>
      then Window > Analysis > Profiler, target <Enter IP>, 127.0.0.1:34999.
      Requires a DEVELOPMENT build with "Autoconnect Profiler" or manual connect.
    * Android GPU Inspector (https://gpuinspector.dev) for per-drawcall GPU timing.
  The static proxy that needs no device at all is:
        python tooling/scripts/card_renderer_census.py

.PARAMETER Apk
  Path to the APK. Defaults to EF-060's output location.

.PARAMETER PackageName
  Android application id. If omitted, resolved from ProjectSettings.asset, falling back to
  com.<companyName>.<productName>. Pass it explicitly if resolution fails.

.PARAMETER Seconds
  How long to let the app run before forcing the flush. Default 120.

.PARAMETER OutDir
  Where to drop the pulled capture. Default: artifacts/perf/<timestamp>/

.PARAMETER SkipInstall
  Keep the currently installed package, but still verify that its APK hash exactly matches -Apk.

.EXAMPLE
  pwsh tools/ci/capture-android-perf.ps1 -Seconds 180
#>
[CmdletBinding()]
param(
    [string]$Apk = "unity/Emberfall/Builds/Android/Emberfall.apk",
    [string]$PackageName,
    [int]$Seconds = 120,
    [string]$OutDir,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repo

if ($Seconds -lt 1 -or $Seconds -gt 300) {
    throw '-Seconds must be between 1 and 300 (the in-player buffer holds five minutes).'
}

function Find-Adb {
    $c = Get-Command adb -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $candidates = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ANDROID_HOME\platform-tools\adb.exe",
        "$env:ANDROID_SDK_ROOT\platform-tools\adb.exe"
    )
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
    throw "adb not found. Install Android platform-tools or set ANDROID_HOME."
}

function Resolve-PackageName {
    if ($PackageName) { return $PackageName }
    $ps = Join-Path $repo 'unity/Emberfall/ProjectSettings/ProjectSettings.asset'
    $text = Get-Content $ps -Raw
    # applicationIdentifier is a YAML map: `applicationIdentifier:\n    Android: com.x.y`
    if ($text -match 'applicationIdentifier:\s*\r?\n(?:\s+\w+:.*\r?\n)*?\s+Android:\s*(\S+)') {
        return $Matches[1]
    }
    $company = if ($text -match 'companyName:\s*(.+)') { $Matches[1].Trim() } else { 'DefaultCompany' }
    $product = if ($text -match 'productName:\s*(.+)')  { $Matches[1].Trim() } else { 'Bad Ideas' }
    $guess = "com.$company.$product" -replace '\s',''
    Write-Warning "applicationIdentifier.Android is not set in ProjectSettings.asset; guessing '$guess'. Pass -PackageName to override."
    return $guess
}

$adb = Find-Adb
$apkFull = if ([System.IO.Path]::IsPathRooted($Apk)) {
    [System.IO.Path]::GetFullPath($Apk)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repo $Apk))
}
$buildInfoPath = "$apkFull.build.json"
if (-not (Test-Path -LiteralPath $apkFull)) {
    throw "APK not found at '$apkFull'. Build it first with: pwsh tools/ci/build-android.ps1 -PerfCapture"
}
if (-not (Test-Path -LiteralPath $buildInfoPath)) {
    throw "Build metadata not found at '$buildInfoPath'. Rebuild with tools/ci/build-android.ps1 -PerfCapture."
}

$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
if (-not $buildInfo.perfCapture -or $buildInfo.dirty) {
    throw 'The APK metadata does not describe a clean EF-064 performance-capture build.'
}
$localSha256 = (Get-FileHash -LiteralPath $apkFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($localSha256 -ne $buildInfo.apkSha256) {
    throw "APK hash does not match its build metadata. Expected $($buildInfo.apkSha256), got $localSha256."
}

$devices = @(& $adb devices -l | Select-Object -Skip 1 | Where-Object { $_ -match '\sdevice(?:\s|$)' })
if ($devices.Count -ne 1) {
    & $adb devices -l
    throw "Expected exactly one attached Android device; found $($devices.Count)."
}
$deviceSerial = ($devices[0] -split '\s+')[0]

$pkg = Resolve-PackageName
if ($pkg -notmatch '^[A-Za-z][A-Za-z0-9_.]+$') {
    throw "Resolved package name '$pkg' is not safe to pass to adb."
}

if (-not $OutDir) {
    $OutDir = Join-Path $repo ("artifacts/perf/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "adb      : $adb"
Write-Host "device   : $($devices[0])"
Write-Host "package  : $pkg"
Write-Host "commit   : $($buildInfo.commit)"
Write-Host "APK SHA  : $localSha256"

if (-not $SkipInstall) {
    Write-Host "install  : $apkFull"
    & $adb install -r $apkFull
    if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE." }
}

$packagePathLine = (& $adb shell pm path $pkg | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve installed APK path for '$pkg'."
}
if ($packagePathLine -notmatch '^package:(.+)$') {
    throw "Installed APK path for '$pkg' had an unexpected format: '$packagePathLine'."
}
$installedApkPath = $Matches[1].Trim()
$installedHashLine = (& $adb shell sha256sum $installedApkPath | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not hash the installed APK on the device.'
}
if ($installedHashLine -notmatch '^([0-9a-fA-F]{64})\s') {
    throw "Installed APK hash had an unexpected format: '$installedHashLine'."
}
$installedSha256 = $Matches[1].ToLowerInvariant()
if ($installedSha256 -ne $localSha256) {
    throw "Installed APK hash mismatch. Local $localSha256, installed $installedSha256."
}
Write-Host "verify   : installed APK hash matches"

$deviceFiles = "/sdcard/Android/data/$pkg/files"
$marker = "$deviceFiles/perfcapture.on"
$markerCreated = $false
$startedUtc = [DateTime]::UtcNow
$allLog = @()

try {
    & $adb shell am force-stop $pkg
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the app before capture.' }

    # Unity's Android persistentDataPath is the package's external files directory. This is
    # reachable for a release APK; run-as files/ is an internal, debuggable-only directory.
    & $adb shell mkdir -p $deviceFiles
    if ($LASTEXITCODE -ne 0) {
        # A fresh install might not have asked Android for getExternalFilesDir yet. Prime one
        # launch so Unity creates it, then stop before the measured launch.
        & $adb shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 | Out-Null
        Start-Sleep -Seconds 3
        & $adb shell am force-stop $pkg
        & $adb shell mkdir -p $deviceFiles
        if ($LASTEXITCODE -ne 0) { throw "Could not create or access '$deviceFiles'." }
    }
    & $adb shell touch $marker
    if ($LASTEXITCODE -ne 0) { throw "Could not create capture marker '$marker'." }
    $markerCreated = $true

    & $adb logcat -c
    if ($LASTEXITCODE -ne 0) { throw 'Could not clear logcat before capture.' }

    Write-Host "launch   : monkey -p $pkg 1"
    & $adb shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not launch the app.' }

    $armed = $false
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $recentLog = @(& $adb logcat -d -t 600)
        if ($recentLog | Select-String -Quiet -SimpleMatch '[EF-064-PERF] capture armed') {
            $armed = $true
            break
        }
    }
    if (-not $armed) {
        throw 'Capture marker was created, but the APK did not report that the harness armed.'
    }

    Write-Host ""
    Write-Host "CAPTURE ARMED for $Seconds seconds. Drive the app now: enter a match and exercise a full board."
    Write-Host ""
    Start-Sleep -Seconds $Seconds

    Write-Host "flush    : backgrounding app to trigger OnApplicationPause"
    & $adb shell input keyevent KEYCODE_HOME
    if ($LASTEXITCODE -ne 0) { throw 'Could not background the app to flush the capture.' }

    $complete = $false
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $allLog = @(& $adb logcat -d)
        if ($allLog | Select-String -Quiet -SimpleMatch '[EF-064-PERF] CAPTURE-COMPLETE') {
            $complete = $true
            break
        }
    }
    if (-not $complete) { throw 'The app did not report CAPTURE-COMPLETE after backgrounding.' }
} finally {
    if ($markerCreated) {
        & $adb shell rm -f $marker | Out-Null
    }
}

$evidenceLog = @($allLog | Select-String -Pattern 'EF-064-PERF|\[SafeAreaCamera\]')
$logPath = Join-Path $OutDir 'device-log.txt'
[System.IO.File]::WriteAllLines(
    $logPath,
    [string[]]@($evidenceLog | ForEach-Object { $_.Line }),
    [System.Text.UTF8Encoding]::new($false))

if (-not ($evidenceLog | Where-Object { $_.Line.Contains('[SafeAreaCamera]') })) {
    throw 'No SafeAreaCamera device evidence was logged. The capture did not reach a board scene.'
}

$captureNames = @(
    [regex]::Matches(($allLog -join "`n"), 'perf-\d{8}-\d{6}(?:-summary)?\.(?:csv|txt)') |
        ForEach-Object { $_.Value } |
        Sort-Object -Unique
)
if ($captureNames.Count -ne 2) {
    throw "Expected one CSV and one summary from this run; log named $($captureNames.Count) capture file(s)."
}

foreach ($name in $captureNames) {
    $destination = Join-Path $OutDir $name
    & $adb pull "$deviceFiles/$name" $destination
    if ($LASTEXITCODE -ne 0) { throw "Failed to pull '$name'." }
}

$manifest = [ordered]@{
    schema = 1
    startedUtc = $startedUtc.ToString('O')
    completedUtc = [DateTime]::UtcNow.ToString('O')
    seconds = $Seconds
    device = $devices[0]
    deviceSerial = $deviceSerial
    package = $pkg
    installedApkPath = $installedApkPath
    apkSha256 = $installedSha256
    build = $buildInfo
} | ConvertTo-Json -Depth 4
$manifestPath = Join-Path $OutDir 'capture-manifest.json'
[System.IO.File]::WriteAllText(
    $manifestPath,
    $manifest + "`n",
    [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "pulled to: $OutDir"
Get-ChildItem -LiteralPath $OutDir | ForEach-Object {
    Write-Host ("  {0}  {1} bytes" -f $_.Name, $_.Length)
}

$summary = Get-ChildItem -LiteralPath $OutDir -Filter '*-summary.txt' | Select-Object -First 1
if (-not $summary) { throw 'Capture summary was not pulled.' }
Write-Host ""
Write-Host "===== SUMMARY ====="
Get-Content -LiteralPath $summary.FullName | ForEach-Object { Write-Host $_ }
