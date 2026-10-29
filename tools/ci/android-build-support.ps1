# Shared by the build harness and the independent, retryable Sentry uploader.
function Write-AndroidBuildJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}

function New-AndroidSymbolUploadJob {
    param([Parameter(Mandatory)][string]$Project,
          [Parameter(Mandatory)][string]$Artifact,
          [Parameter(Mandatory)][string]$Commit)

    $gradle = Join-Path $Project 'Library/Bee/Android/Prj/IL2CPP/Gradle'
    $configuration = Get-Content -LiteralPath (Join-Path $gradle 'launcher/kingdom-symbol-upload.json') -Raw |
        ConvertFrom-Json
    if (-not $configuration.configured) {
        Write-Host 'build-android: Sentry is not configured; no upload job created.'
        return $null
    }
    if ($configuration.variant -notin @('debug', 'release')) { throw 'Invalid Android build variant.' }
    $gradleText = [IO.File]::ReadAllText((Join-Path $gradle 'launcher/build.gradle'))
    $cliPaths = @([regex]::Matches($gradleText, "(?m)^\s*executable '([^']*sentry-cli[^']*)'") |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($cliPaths.Count -ne 1 -or -not (Test-Path -LiteralPath $cliPaths[0] -PathType Leaf)) {
        throw 'Could not find this build''s Sentry CLI. Symbols were not queued.'
    }

    $jobRoot = Join-Path $Project ('Builds/SymbolUploads/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') +
        '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $payload = Join-Path $jobRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $il2cpp = Join-Path $Project 'Library/Bee/artifacts/Android/il2cppOutput'
    $sources = [ordered]@{
        'il2cpp/build' = (Join-Path $il2cpp 'build')
        'il2cpp/cpp' = (Join-Path $il2cpp 'cpp')
        'unity-symbols' = (Join-Path $gradle 'unityLibrary/symbols')
        'unity-debug' = (Join-Path $Project 'Library/Bee/artifacts/Android/libunity/arm64-v8a')
        'managed' = (Join-Path $Project 'Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/Managed')
    }
    foreach ($relative in $sources.Keys) {
        if (-not (Test-Path -LiteralPath $sources[$relative] -PathType Container)) {
            throw "Missing symbols input: $($sources[$relative])"
        }
        $destination = Join-Path $payload $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $sources[$relative] -Destination $destination -Recurse
    }
    # IL2CPP's root pointer must refer to the immutable snapshot, not a later build's cache.
    $rootPointer = Join-Path $payload 'il2cpp/cpp/Symbols/il2cppFileRoot.txt'
    if (Test-Path -LiteralPath $rootPointer) {
        [IO.File]::WriteAllText($rootPointer, (Join-Path $payload 'il2cpp/cpp'), [Text.UTF8Encoding]::new($false))
    }
    # Include the actual shipped native binaries for stack unwinding and third-party libraries.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Artifact)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch '^(base/)?lib/(arm64-v8a/[^/]+\.so)$') { continue }
            $destination = Join-Path $payload ('native/' + $Matches[2])
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        }
    } finally { $archive.Dispose() }
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'native/arm64-v8a/libil2cpp.so'))) {
        throw 'Artifact has no ARM64 IL2CPP library; refusing to queue unrelated symbols.'
    }

    $mapping = $null
    if ($configuration.minified) {
        $mapping = 'mapping.txt'
        Copy-Item -LiteralPath (Join-Path $gradle "launcher/build/outputs/mapping/$($configuration.variant)/mapping.txt") `
            -Destination (Join-Path $jobRoot $mapping)
    }
    $cliName = 'sentry-cli' + [IO.Path]::GetExtension($cliPaths[0])
    Copy-Item -LiteralPath $cliPaths[0] -Destination (Join-Path $jobRoot $cliName)
    $files = @(Get-ChildItem -LiteralPath $jobRoot -File -Recurse | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($jobRoot, $_.FullName)
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = Join-Path $jobRoot 'job.json'
    Write-AndroidBuildJson $manifest ([ordered]@{
        schema = 1; artifact = [IO.Path]::GetFullPath($Artifact)
        artifactSha256 = (Get-FileHash -LiteralPath $Artifact -Algorithm SHA256).Hash
        commit = $Commit; organization = $configuration.organization; project = $configuration.project
        cli = $cliName; mapping = $mapping; files = $files
    })
    Write-AndroidBuildJson (Join-Path $jobRoot 'status.json') @{ state = 'queued'; updatedUtc = [DateTime]::UtcNow.ToString('O') }
    return $manifest
}

function Get-AndroidSymbolJobPath {
    param([string]$Root, [string]$Relative)
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Upload job path escapes its snapshot.'
    }
    return $path
}

function Invoke-AndroidSymbolUploadJob {
    param([Parameter(Mandatory)][string]$Job, [switch]$ValidateOnly)
    $Job = (Resolve-Path -LiteralPath $Job).Path
    $root = Split-Path -Parent $Job
    $manifest = Get-Content -LiteralPath $Job -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or -not $manifest.files.Count) { throw 'Invalid upload job manifest.' }
    $lock = [IO.File]::Open((Join-Path $root 'upload.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $status = Join-Path $root 'status.json'
    $previousProperties = $env:SENTRY_PROPERTIES
    try {
        foreach ($file in $manifest.files) {
            $path = Get-AndroidSymbolJobPath $root $file.path
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
                throw "Symbol snapshot changed: $($file.path)"
            }
        }
        $cli = Get-AndroidSymbolJobPath $root $manifest.cli
        if (-not ($manifest.files.path -contains $manifest.cli)) { throw 'CLI is not covered by the snapshot manifest.' }
        if ($manifest.mapping -and -not ($manifest.files.path -contains $manifest.mapping)) {
            throw 'Mapping is not covered by the snapshot manifest.'
        }
        if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($env:SENTRY_AUTH_TOKEN)) {
            throw 'Set SENTRY_AUTH_TOKEN to upload this saved job.'
        }
        # Never read a different build's sentry.properties, and never persist the token.
        $properties = Join-Path $root 'sentry.properties'
        [IO.File]::WriteAllText($properties, '', [Text.UTF8Encoding]::new($false))
        $env:SENTRY_PROPERTIES = $properties
        $log = Join-Path $root $(if ($ValidateOnly) { 'validation.log' } else { 'upload.log' })
        if (-not $ValidateOnly) {
            Write-AndroidBuildJson $status @{ state = 'uploading'; updatedUtc = [DateTime]::UtcNow.ToString('O') }
        }
        $arguments = @('debug-files', 'upload', '--org', $manifest.organization, '--project', $manifest.project,
            '--il2cpp-mapping', (Join-Path $root 'payload'))
        if ($ValidateOnly) { $arguments += '--no-upload' }
        & $cli @arguments *> $log
        if ($LASTEXITCODE -ne 0) { throw "Sentry debug-file upload exited $LASTEXITCODE. See $log" }
        if ($manifest.mapping -and -not $ValidateOnly) {
            & $cli upload-proguard --org $manifest.organization --project $manifest.project `
                (Get-AndroidSymbolJobPath $root $manifest.mapping) *>> $log
            if ($LASTEXITCODE -ne 0) { throw "Sentry mapping upload exited $LASTEXITCODE. See $log" }
        }
        if (-not $ValidateOnly) {
            Write-AndroidBuildJson $status @{ state = 'uploaded'; updatedUtc = [DateTime]::UtcNow.ToString('O') }
        }
        Write-Host "Symbol job $(if ($ValidateOnly) { 'validated without uploading' } else { 'uploaded' }): $Job"
    } catch {
        if (-not $ValidateOnly) {
            Write-AndroidBuildJson $status @{ state = 'failed'; error = $_.Exception.Message; updatedUtc = [DateTime]::UtcNow.ToString('O') }
        }
        throw
    } finally {
        $env:SENTRY_PROPERTIES = $previousProperties
        $lock.Dispose()
    }
}
