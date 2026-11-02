# Behavioral tests: no Unity license, network, or real credentials required.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'android-build-support.ps1')
function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-Fixture([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
$fixture = [IO.Directory]::CreateTempSubdirectory('kingdom-android-build-test-').FullName
$previousToken = $env:SENTRY_AUTH_TOKEN
$previousFailure = $env:KINGDOM_TEST_UPLOAD_FAIL
try {
    $project = Join-Path $fixture 'project with spaces'
    $gradle = Join-Path $project 'Library/Bee/Android/Prj/IL2CPP/Gradle'
    $cpp = Join-Path $project 'Library/Bee/artifacts/Android/il2cppOutput'
    $cli = Join-Path $fixture 'sentry-cli-fixture.ps1'
    Write-Fixture $cli '$global:LASTEXITCODE = if ($env:KINGDOM_TEST_UPLOAD_FAIL -eq "1") { 9 } else { 0 }; Write-Output ($args -join " ")'
    Write-Fixture (Join-Path $gradle 'launcher/build.gradle') ("executable '" + $cli.Replace('\', '/') + "'")
    $configPath = Join-Path $gradle 'launcher/kingdom-symbol-upload.json'
    $config = @{ configured = $true; organization = 'fixture'; project = 'fixture'; variant = 'release'; minified = $false }
    Write-AndroidBuildJson $configPath $config
    Write-Fixture (Join-Path $cpp 'build/arm64-v8a/libil2cpp.dbg.so') 'original-debug-data'
    Write-Fixture (Join-Path $cpp 'cpp/Symbols/il2cppFileRoot.txt') (Join-Path $cpp 'cpp')
    Write-Fixture (Join-Path $gradle 'unityLibrary/symbols/arm64-v8a/libunity.so') 'unity-symbols'
    Write-Fixture (Join-Path $project 'Library/Bee/artifacts/Android/libunity/arm64-v8a/libunity.dbg.so') 'unity-debug'
    Write-Fixture (Join-Path $project 'Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/Managed/Game.pdb') 'managed-symbols'
    Write-Fixture (Join-Path $gradle 'launcher/build/outputs/mapping/release/mapping.txt') 'stale-mapping'
    $archiveRoot = Join-Path $fixture 'apk-source'
    Write-Fixture (Join-Path $archiveRoot 'lib/arm64-v8a/libil2cpp.so') 'current-native-library'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $artifact = Join-Path $fixture 'game.apk'
    [IO.Compression.ZipFile]::CreateFromDirectory($archiveRoot, $artifact)
    $env:SENTRY_AUTH_TOKEN = 'fixture-secret-must-not-be-persisted'
    $job = New-AndroidSymbolUploadJob -Project $project -Artifact $artifact -Commit 'fixture-commit'
    $root = Split-Path -Parent $job
    $manifest = Get-Content -LiteralPath $job -Raw | ConvertFrom-Json
    Assert-True (-not $manifest.mapping) 'An unminified build reused a stale mapping.'
    Assert-True (-not ([IO.File]::ReadAllText($job).Contains($env:SENTRY_AUTH_TOKEN))) 'Job persisted the token.'
    Write-Fixture (Join-Path $cpp 'build/arm64-v8a/libil2cpp.dbg.so') 'next-build-overwrote-cache'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'payload/il2cpp/build/arm64-v8a/libil2cpp.dbg.so')) -eq 'original-debug-data') 'Upload depends on mutable cache.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'payload/il2cpp/cpp/Symbols/il2cppFileRoot.txt')) -eq (Join-Path $root 'payload/il2cpp/cpp')) 'IL2CPP still points outside snapshot.'
    Invoke-AndroidSymbolUploadJob -Job $job -ValidateOnly
    Assert-True ((Get-Content (Join-Path $root 'status.json') -Raw | ConvertFrom-Json).state -eq 'queued') 'Validation claimed a real upload.'
    $env:KINGDOM_TEST_UPLOAD_FAIL = '1'
    $failed = $false
    try { Invoke-AndroidSymbolUploadJob -Job $job } catch { $failed = $true }
    Assert-True $failed 'Uploader swallowed a CLI failure.'
    Assert-True ((Get-Content (Join-Path $root 'status.json') -Raw | ConvertFrom-Json).state -eq 'failed') 'Failure receipt missing.'
    $env:KINGDOM_TEST_UPLOAD_FAIL = '0'
    Invoke-AndroidSymbolUploadJob -Job $job
    Assert-True ((Get-Content (Join-Path $root 'status.json') -Raw | ConvertFrom-Json).state -eq 'uploaded') 'Retry did not complete.'
    Write-Fixture (Join-Path $root 'payload/il2cpp/build/arm64-v8a/libil2cpp.dbg.so') 'corrupted-snapshot'
    $failed = $false
    try { Invoke-AndroidSymbolUploadJob -Job $job } catch { $failed = $_.Exception.Message.Contains('snapshot changed') }
    Assert-True $failed 'Corrupted symbols were accepted.'

    $config.minified = $true
    Write-AndroidBuildJson $configPath $config
    Write-Fixture (Join-Path $gradle 'launcher/build/outputs/mapping/release/mapping.txt') 'current-mapping'
    $mappedJob = New-AndroidSymbolUploadJob -Project $project -Artifact $artifact -Commit 'fixture-commit-2'
    Invoke-AndroidSymbolUploadJob -Job $mappedJob
    $mappedRoot = Split-Path -Parent $mappedJob
    Assert-True ([IO.File]::ReadAllText((Join-Path $mappedRoot 'mapping.txt')) -eq 'current-mapping') 'Wrong mapping snapshot.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $mappedRoot 'upload.log')).Contains('upload-proguard')) 'Mapping was not uploaded.'
    $failed = $false
    try { Get-AndroidSymbolJobPath $mappedRoot '../../outside' | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Job accepted a path outside its snapshot.'
    $invalidBuild = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-android.ps1') -Fast -PlayRelease 2>&1
    Assert-True ($LASTEXITCODE -ne 0 -and ($invalidBuild -join "`n").Contains('Choose one')) 'Fast Play release was not rejected.'
    Write-Host 'PASS: immutable symbols, mapping selection, secret exclusion, validation, failure/retry, corruption, containment, and fast/release guard.'
    # The guard check above runs a build that is MEANT to fail, and leaves its exit code behind.
    # GitHub's pwsh step ends with `exit $LASTEXITCODE`, so without this the step fails after PASS.
    $global:LASTEXITCODE = 0
} finally {
    $env:SENTRY_AUTH_TOKEN = $previousToken
    $env:KINGDOM_TEST_UPLOAD_FAIL = $previousFailure
    # This directory was allocated by this test and contains only its small synthetic fixture.
    $ownedPrefix = Join-Path ([IO.Path]::GetTempPath()) 'kingdom-android-build-test-'
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test cleanup path is outside the allocated fixture location.'
    }
    [IO.Directory]::Delete($fixture, $true)
}
