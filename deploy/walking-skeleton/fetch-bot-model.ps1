<#
.SYNOPSIS
    Copies the champion bot model from the private training repo into tooling/data/bot-model/ and
    refuses to proceed unless the C# encoder agrees with the model's parity fixture.

.DESCRIPTION
    Run this before `fly deploy`: Dockerfile.server COPYs tooling/data/bot-model/, which is
    gitignored in this repo and filled only by this script.

    Reads <TrainingRepo>/models/champion.json ({"model": "models/h4"}), copies that folder's
    model.onnx and bot-model.json, then runs NetSoak's onnx-parity against the folder's
    parity-fixture.jsonl. Exits non-zero, naming the cause, when champion.json or the fixture is
    missing, a copy fails, parity does not run, or encodingMismatches is above 0.

.EXAMPLE
    pwsh deploy/walking-skeleton/fetch-bot-model.ps1 -TrainingRepo C:\Users\User\Documents\kingdom-training
#>
param(
    [Parameter(Mandatory = $true)][string]$TrainingRepo
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    [Console]::Error.WriteLine("fetch-bot-model: $message")
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$target = Join-Path $repoRoot 'tooling/data/bot-model'

if (-not (Test-Path -LiteralPath $TrainingRepo -PathType Container)) { Fail "training repo not found: $TrainingRepo" }

$championFile = Join-Path $TrainingRepo 'models/champion.json'
if (-not (Test-Path -LiteralPath $championFile -PathType Leaf)) { Fail "no champion.json at $championFile" }

try {
    $champion = [System.IO.File]::ReadAllText($championFile) | ConvertFrom-Json
} catch {
    Fail "champion.json is not valid JSON: $($_.Exception.Message)"
}
if (-not $champion.model) { Fail "champion.json has no 'model' entry: $championFile" }

$source = Join-Path $TrainingRepo $champion.model
if (-not (Test-Path -LiteralPath $source -PathType Container)) { Fail "champion model folder not found: $source" }

$fixture = Join-Path $source 'parity-fixture.jsonl'
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { Fail "no parity-fixture.jsonl at $fixture" }

$files = 'model.onnx', 'bot-model.json'
foreach ($name in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { Fail "champion folder lacks $name`: $source" }
}

# Replace the folder wholesale so a stale file from an earlier champion cannot ride along.
try {
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in $files) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
    }
} catch {
    Fail "copy failed: $($_.Exception.Message)"
}
foreach ($name in $files) {
    $a = (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath (Join-Path $target $name) -Algorithm SHA256).Hash
    if ($a -ne $b) { Fail "copy of $name does not match its source (sha256 $a vs $b)" }
}
Write-Host "fetch-bot-model: copied $($champion.model) -> tooling/data/bot-model/"

Push-Location $repoRoot
try {
    $output = & dotnet run --project src/Kingdom.Tools.NetSoak -c Release -- onnx-parity $fixture $target 2>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
} finally {
    Pop-Location
}
$output | ForEach-Object { Write-Host $_ }

$parityLine = $output | Where-Object { $_ -match '^\[onnx-parity\] decisions=' } | Select-Object -Last 1
if (-not $parityLine) { Fail "onnx-parity printed no result line (dotnet exit $exit)" }
if ($parityLine -match 'encodingMismatches=(\d+)') {
    if ([int]$Matches[1] -gt 0) { Fail "encoder parity broken: encodingMismatches=$($Matches[1])" }
} else {
    Fail "could not read encodingMismatches from: $parityLine"
}
if ($exit -ne 0) { Fail "onnx-parity exited $exit" }

Write-Host 'fetch-bot-model: parity OK'
exit 0
