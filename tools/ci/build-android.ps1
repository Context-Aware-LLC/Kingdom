# EF-060 · Headless Android APK build harness
#
# Switches Unity to the Android build target, runs KingdomBuild.BuildAndroid()
# headlessly (IL2CPP + ARM64, debug keystore), and exits non-zero on any failure.
#
# IMPORTANT: compile errors arrive in the Unity log typed as Log, not Error.
# This script keys off message text, not log severity, to catch them.
#
# Usage: pwsh tools/ci/build-android.ps1
#        pwsh tools/ci/build-android.ps1 -PerfCapture
#        pwsh tools/ci/build-android.ps1 -PlayRelease
#
# -PlayRelease builds a Google Play AAB signed with the UPLOAD key instead of a debug APK. The
# keystore lives outside the repo (default below); its password comes from
# $env:KINGDOM_UPLOAD_KEYSTORE_PASS or is prompted for, and is never written to disk. The
# version and version code bump themselves (StoreVersionBump.cs); -VersionCode N overrides the
# code when Play needs a specific one.
#        pwsh tools/ci/build-android.ps1 -UnityPath "C:\path\to\Unity.exe"
[CmdletBinding()]
param(
    # Hub's default install location, which differs per platform (and the Linux binary has no
    # .exe). Override with -UnityPath for any non-Hub install. Note an Android build also needs
    # the Android module installed for that Editor, which a bare headless install will not have.
    [string]$UnityPath = $(if ($IsWindows) { 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe' }
                           else            { "$HOME/Unity/Hub/Editor/6000.6.4f1/Editor/Unity" }),
    [switch]$PerfCapture,
    [switch]$PlayRelease,
    [string]$Keystore = 'C:\Users\User\.keystore\KingdomOfBadIdeas\kobi-upload.keystore',
    [string]$KeyAlias = 'kobi-upload',
    [int]$VersionCode = 0
)

$ErrorActionPreference = 'Stop'

# Path.Combine, never a literal '\' - a backslash is a legal filename character on Linux.
$REPO_ROOT  = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))
$PROJECT    = [System.IO.Path]::Combine($REPO_ROOT, 'unity')
$TIMESTAMP  = (Get-Date -Format 'yyyyMMdd-HHmmss')
$LOG        = Join-Path ([System.IO.Path]::GetTempPath()) "unity-build-android-$TIMESTAMP.log"
$APK        = if ($PlayRelease) { [System.IO.Path]::Combine($PROJECT, 'Builds', 'Android', 'KingdomOfBadIdeas.aab') }
              else              { [System.IO.Path]::Combine($PROJECT, 'Builds', 'Android', 'Kingdom.apk') }
$BUILD_INFO = "$APK.build.json"

$BUILD_COMMIT = (& git -C $REPO_ROOT rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $BUILD_COMMIT) {
    Write-Error 'Could not resolve the build commit.'
    exit 1
}
$BUILD_STATUS = @(& git -C $REPO_ROOT status --porcelain --untracked-files=all)
$BUILD_DIRTY = $BUILD_STATUS.Count -gt 0
$DIRTY_SUFFIX = if ($BUILD_DIRTY) { '-dirty' } else { '' }
if ($PlayRelease -and $PerfCapture) {
    Write-Error 'A Play release cannot also be a performance capture build.'
    exit 1
}
if ($PlayRelease) {
    if (-not (Test-Path -LiteralPath $Keystore)) {
        Write-Error "Upload keystore not found at '$Keystore'. Pass -Keystore."
        exit 1
    }
    $uploadPass = $env:KINGDOM_UPLOAD_KEYSTORE_PASS
    if (-not $uploadPass) {
        $secure = Read-Host -AsSecureString 'Upload keystore password (KeePass)'
        $uploadPass = [System.Net.NetworkCredential]::new('', $secure).Password
    }
    if (-not $uploadPass) {
        Write-Error 'No upload keystore password given.'
        exit 1
    }
    if ($BUILD_DIRTY) {
        Write-Host 'build-android: WARNING - building a Play release from a dirty worktree.'
    }
}
if ($PerfCapture -and $BUILD_DIRTY) {
    Write-Error 'A performance capture build must come from a clean worktree so its commit identity is exact.'
    $BUILD_STATUS | ForEach-Object { Write-Host "  $_" }
    exit 1
}

if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity not found at '$UnityPath'. Pass -UnityPath or install Unity 6000.6.4f1."
    exit 1
}

if ($PlayRelease) {
    # Check the password against the keystore NOW. Gradle only opens the keystore in
    # :launcher:signReleaseBundle, the last task of the build, so a mistyped password otherwise
    # costs the whole IL2CPP + R8 run before it is reported. This proves the STORE password and
    # that the alias exists; keytool -list never opens the key, so a key password that differs
    # from the store's still fails at signing.
    $keytool = [System.IO.Path]::Combine((Split-Path -Parent $UnityPath), 'Data', 'PlaybackEngines',
        'AndroidPlayer', 'OpenJDK', 'bin', $(if ($IsWindows) { 'keytool.exe' } else { 'keytool' }))
    if (Test-Path -LiteralPath $keytool) {
        $env:KINGDOM_KEYTOOL_PASS = $uploadPass
        try {
            $keytoolOut = & $keytool -list -keystore $Keystore -alias $KeyAlias `
                -storepass:env KINGDOM_KEYTOOL_PASS 2>&1
            $keytoolExit = $LASTEXITCODE
        } finally {
            Remove-Item Env:KINGDOM_KEYTOOL_PASS -ErrorAction SilentlyContinue
        }
        if ($keytoolExit -ne 0) {
            Write-Error "Upload keystore rejected the password or alias '$KeyAlias': $keytoolOut"
            exit 1
        }
        Write-Host "build-android: keystore password accepted for alias '$KeyAlias'"
    } else {
        Write-Host "build-android: WARNING - keytool not found at '$keytool'; password not pre-checked."
    }
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

# Advance compilation-input timestamps so Unity does not skip a stale-script compile.
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

Write-Host "build-android: Unity   $UnityPath"
Write-Host "build-android: project $PROJECT"
Write-Host "build-android: log     $LOG"
Write-Host "build-android: commit  $BUILD_COMMIT$DIRTY_SUFFIX"
Write-Host "build-android: profile $(if ($PlayRelease) { "PLAY RELEASE (AAB, upload key '$KeyAlias')" } elseif ($PerfCapture) { 'EF-064 release capture' } else { 'standard release' })"
Write-Host "build-android: compile barrier $($compileBarrierUtc.ToString('O')) ($($compileInputs.Count) input(s))"
Write-Host "build-android: launching headless (IL2CPP + ARM64)…"
Write-Host ""

$unityArgs = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-buildTarget', 'android',
    '-projectPath', "`"$PROJECT`"",
    '-executeMethod', 'Kingdom.Editor.KingdomBuild.BuildAndroid',
    '-logFile', "`"$LOG`""
)

$previousCaptureBuild = $env:KINGDOM_PERF_CAPTURE_BUILD
$previousBuildCommit = $env:KINGDOM_BUILD_COMMIT
try {
    $env:KINGDOM_PERF_CAPTURE_BUILD = if ($PerfCapture) { '1' } else { '0' }
    $env:KINGDOM_BUILD_COMMIT = $BUILD_COMMIT
    if ($PlayRelease) {
        $env:KINGDOM_PLAY_RELEASE = '1'
        $env:KINGDOM_UPLOAD_KEYSTORE = $Keystore
        $env:KINGDOM_UPLOAD_KEY_ALIAS = $KeyAlias
        $env:KINGDOM_UPLOAD_KEYSTORE_PASS = $uploadPass
        $env:KINGDOM_VERSION_CODE = if ($VersionCode -gt 0) { "$VersionCode" } else { '' }
    }

    $proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -NoNewWindow
    # Android IL2CPP builds are slow; allow 30 minutes.
    $finished = $proc.WaitForExit(1800000)
    if (-not $finished) {
        Write-Host "build-android: ERROR — Unity timed out after 30 minutes"
        try { $proc.Kill() } catch { Write-Host "build-android: WARNING — could not stop timed-out Unity process: $($_.Exception.Message)" }
        $proc.WaitForExit()
        $unityExit = 124
    } else {
        $unityExit = $proc.ExitCode
    }
} finally {
    $env:KINGDOM_PERF_CAPTURE_BUILD = $previousCaptureBuild
    $env:KINGDOM_BUILD_COMMIT = $previousBuildCommit
    # The signing environment dies with the build; the password never outlives the Unity process.
    Remove-Item Env:KINGDOM_PLAY_RELEASE, Env:KINGDOM_UPLOAD_KEYSTORE, Env:KINGDOM_UPLOAD_KEY_ALIAS,
                Env:KINGDOM_UPLOAD_KEYSTORE_PASS, Env:KINGDOM_VERSION_CODE -ErrorAction SilentlyContinue
}

# ── Log read ─────────────────────────────────────────────────────────────────
if (-not (Test-Path $LOG)) {
    Write-Host "build-android: ERROR — log file was not written ($LOG)"
    exit 1
}

$logLines = Get-Content $LOG -Encoding UTF8

# ── Compile-error detection (text-based, NOT severity-based) ─────────────────
$compileErrors = $logLines | Where-Object {
    $_ -match 'error CS\d+' -or
    $_ -match 'Assembly compilation failed' -or
    $_ -match '^ERROR in .*\.cs\(' -or
    $_ -match 'CompilerOutput:-stderr>'
}

if ($compileErrors) {
    Write-Host "build-android: COMPILE ERRORS DETECTED"
    $compileErrors | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
}

# ── Build result detection ────────────────────────────────────────────────────
$buildSucceeded = $logLines | Where-Object { $_ -match '\[KingdomBuild\] Player build Succeeded' }
$buildFailed    = $logLines | Where-Object { $_ -match '\[KingdomBuild\] Player build.*FAILED|BuildFailedException' }
$apkLine        = $logLines | Where-Object { $_ -match 'Builds[/\\]Android[/\\](Kingdom\.apk|KingdomOfBadIdeas\.aab)' } | Select-Object -Last 1

Write-Host "build-android: =============================="
Write-Host "build-android: Last 60 log lines:"
$logLines | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" }
Write-Host "build-android: =============================="

if ($buildSucceeded -and -not $compileErrors -and $unityExit -eq 0) {
    if (-not (Test-Path -LiteralPath $APK)) {
        Write-Host "build-android: FAILED — success marker found but the build output is missing at '$APK'"
        exit 1
    }

    $apkItem = Get-Item -LiteralPath $APK
    $apkSha256 = (Get-FileHash -LiteralPath $APK -Algorithm SHA256).Hash.ToLowerInvariant()
    $buildInfo = [ordered]@{
        schema = 1
        commit = $BUILD_COMMIT
        dirty = $BUILD_DIRTY
        perfCapture = [bool]$PerfCapture
        playRelease = [bool]$PlayRelease
        apkSha256 = $apkSha256
        apkBytes = $apkItem.Length
        builtUtc = [DateTime]::UtcNow.ToString('O')
        unityPath = $UnityPath
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($BUILD_INFO, $buildInfo + "`n", [System.Text.UTF8Encoding]::new($false))

    Write-Host "build-android: SUCCESS — $(if ($PlayRelease) { 'Play AAB' } else { 'APK' }) produced"
    Write-Host "build-android: SHA256  $apkSha256"
    Write-Host "build-android: metadata $BUILD_INFO"
    if ($apkLine) { Write-Host "build-android: $apkLine" }
    exit 0
} else {
    $reasons = @()
    if ($compileErrors)         { $reasons += 'compile errors' }
    if ($buildFailed)           { $reasons += 'build FAILED marker found' }
    if (-not $buildSucceeded)   { $reasons += 'no success marker found' }
    if ($unityExit -ne 0)       { $reasons += "Unity exited $unityExit" }
    Write-Host "build-android: FAILED — $($reasons -join ', ')"
    exit 1
}
