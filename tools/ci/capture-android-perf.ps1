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

.EXAMPLE
  pwsh tools/ci/capture-android-perf.ps1 -Seconds 180
#>
[CmdletBinding()]
param(
    [string]$Apk = "Builds/Android/Emberfall.apk",
    [string]$PackageName,
    [int]$Seconds = 120,
    [string]$OutDir,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repo

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
    $product = if ($text -match 'productName:\s*(.+)')  { $Matches[1].Trim() } else { 'Emberfall' }
    $guess = "com.$company.$product" -replace '\s',''
    Write-Warning "applicationIdentifier.Android is not set in ProjectSettings.asset; guessing '$guess'. Pass -PackageName to override."
    return $guess
}

$adb = Find-Adb
Write-Host "adb      : $adb"

$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\sdevice$' }
if (-not $devices) {
    & $adb devices
    throw "No Android device attached. Connect a device with USB debugging enabled, accept the RSA prompt, and re-run."
}
Write-Host "device   : $($devices[0])"

$pkg = Resolve-PackageName
Write-Host "package  : $pkg"

if (-not $SkipInstall) {
    if (-not (Test-Path $Apk)) {
        throw "APK not found at '$Apk'. Build it first: pwsh tools/ci/build-android.ps1  (EF-060). Or pass -Apk / -SkipInstall."
    }
    Write-Host "install  : $Apk"
    & $adb install -r $Apk
    if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE." }
}

# Clear logcat so the [EF-064-PERF] lines we read back are from THIS run and not a previous one.
& $adb logcat -c

Write-Host "launch   : monkey -p $pkg 1"
& $adb shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 | Out-Null

Write-Host ""
Write-Host "Capturing for $Seconds s. Drive the app now - get into a match with a full board;"
Write-Host "an idle main menu measures nothing that matters."
Write-Host ""
Start-Sleep -Seconds $Seconds

Write-Host "flush    : backgrounding app to trigger OnApplicationPause"
& $adb shell input keyevent KEYCODE_HOME
Start-Sleep -Seconds 3

# Recover the output directory from the app's own log line rather than assuming internal vs
# external storage -- which of the two persistentDataPath resolves to is a Player Setting.
$log = & $adb logcat -d | Select-String -Pattern 'EF-064-PERF'
if (-not $log) {
    Write-Warning "No [EF-064-PERF] lines in logcat. The capture component did not arm."
    Write-Warning "It arms on a development build, or when files/perfcapture.on exists:"
    Write-Warning "  $adb shell run-as $pkg touch files/perfcapture.on"
    throw "Capture did not run."
}
$log | ForEach-Object { Write-Host "  $_" }

if (-not $OutDir) { $OutDir = Join-Path $repo ("artifacts/perf/" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Files land in the app sandbox; run-as is what reaches it on a debug-signed build.
$listing = & $adb shell run-as $pkg ls files 2>&1
$captures = $listing | Where-Object { $_ -match '^perf-.*\.(csv|txt)$' }
if (-not $captures) {
    Write-Warning "run-as could not list files (release-signed APK?). Falling back to external storage."
    $captures = & $adb shell ls "/sdcard/Android/data/$pkg/files" 2>&1 | Where-Object { $_ -match '^perf-' }
    foreach ($f in $captures) { & $adb pull "/sdcard/Android/data/$pkg/files/$f" (Join-Path $OutDir $f) }
} else {
    foreach ($f in $captures) {
        # `run-as cat` is the portable way out of the internal sandbox; adb pull cannot reach it.
        & $adb shell run-as $pkg cat "files/$f" > (Join-Path $OutDir $f)
    }
}

Write-Host ""
Write-Host "pulled to: $OutDir"
Get-ChildItem $OutDir | ForEach-Object { Write-Host ("  {0}  {1} bytes" -f $_.Name, $_.Length) }

$summary = Get-ChildItem $OutDir -Filter '*-summary.txt' | Select-Object -First 1
if ($summary) {
    Write-Host ""
    Write-Host "===== SUMMARY ====="
    Get-Content $summary.FullName | ForEach-Object { Write-Host $_ }
}
