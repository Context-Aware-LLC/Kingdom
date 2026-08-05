#!/usr/bin/env pwsh
#
# Canonical-vocabulary landing gate.
#
# Usage:
#   pwsh tools/ci/vocabulary-gate.ps1
#   pwsh tools/ci/vocabulary-gate.ps1 -Root C:\path\to\checkout
#   pwsh tools/ci/vocabulary-gate.ps1 -MigrationNote docs\migration-note.md
#   pwsh tools/ci/vocabulary-gate.ps1 -SelfTest
#
# This is a blocking CI gate for active source, test, Unity, and tooling assets.

[CmdletBinding()]
param(
    [string] $Root,
    [string[]] $MigrationNote = @(),
    [switch] $SummaryOnly,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Join-CodePoints {
    param([int[]] $CodePoints)
    return ([char[]] $CodePoints) -join ''
}

function Convert-PascalCase {
    param([string] $Value)
    return $Value.Substring(0, 1) + $Value.Substring(1).ToLowerInvariant()
}

function New-Patterns {
    # Keep the gate in its own active scan scope without creating an exemption for the checker.
    $word = @{
        a = Join-CodePoints 84,65,85,78,84
        b = Join-CodePoints 67,72,65,82,71,69
        c = Join-CodePoints 82,85,83,72
        d = Join-CodePoints 68,73,86,73,78,69
        e = Join-CodePoints 83,72,73,69,76,68
        f = Join-CodePoints 66,65,84,84,76,69,67,82,89
        g = Join-CodePoints 68,69,65,84,72,82,65,84,84,76,69
        h = Join-CodePoints 76,73,70,69,83,84,69,65,76
        i = Join-CodePoints 87,73,78,68,70,85,82,89
        j = Join-CodePoints 83,84,69,65,76,84,72
        k = Join-CodePoints 80,79,73,83,79,78,79,85,83
        l = Join-CodePoints 83,80,69,76,76
        m = Join-CodePoints 68,65,77,65,71,69
        n = Join-CodePoints 70,82,69,69,90,69
        o = Join-CodePoints 70,82,79,90,69,78
        p = Join-CodePoints 79,86,69,82,76,79,65,68
        q = Join-CodePoints 82,69,66,79,82,78
        r = Join-CodePoints 68,73,83,67,79,86,69,82
        s = Join-CodePoints 68,73,83,67,79,86,69,82,89
        t = Join-CodePoints 68,73,83,67,79,86,69,82,73,78,71
        u = Join-CodePoints 68,73,83,67,79,86,69,82,69,68
        v = Join-CodePoints 68,73,83,67,79,86,69,82,83
    }

    $underscore = '_'
    $upper = @(
        $word.a, $word.b, $word.c,
        ($word.d + $underscore + $word.e), $word.f, $word.g,
        $word.h, $word.i, $word.j, $word.k,
        ($word.l + $underscore + $word.m), $word.n, $word.o,
        $word.p, $word.q, $word.r, $word.s,
        $word.t, $word.u, $word.v
    )

    $broad = @(
        $word.a, $word.b, $word.c,
        ($word.d + '[ _-]' + $word.e), $word.f, $word.g,
        $word.h, $word.i, $word.j, $word.k,
        ($word.l + '[ _-]' + $word.m), $word.n, $word.o,
        $word.p, $word.q, $word.r, $word.s,
        $word.t, $word.u, $word.v
    )

    $pascal = @{}
    foreach ($key in $word.Keys) { $pascal[$key] = Convert-PascalCase $word[$key] }

    $action = [regex]::Escape('Action.')
    $verb = [regex]::Escape('Verb.')
    $keyword = [regex]::Escape('Keyword.')
    $pendingChoice = [regex]::Escape('PendingChoiceKind.')
    $is = 'Is'
    $hasUsed = 'HasUsed'
    $nextTurn = 'NextTurn'
    $lock = 'Lock'
    $handler = 'Handler'
    $on = 'on'

    $compound = @(
        ($is + $pascal.o),
        ($hasUsed + $pascal.q),
        ($nextTurn + $pascal.p + $lock),
        ($pascal.p + $lock),
        ($pascal.n + $handler),
        ($pascal.p + $handler),
        ($on + $pascal.n),
        ($on + $pascal.r),
        ($pendingChoice + $pascal.r),
        ($action + $word.r), ($action + $word.n), ($action + $word.p),
        ($verb + $word.r), ($verb + $word.n), ($verb + $word.p)
    )
    foreach ($term in @($word.a, $word.b, $word.c, ($word.d + $underscore + $word.e),
        $word.f, $word.g, $word.h, $word.i, $word.j, $word.k,
        ($word.l + $underscore + $word.m), $word.n, $word.p, $word.q)) {
        $compound += $keyword + $term
    }

    return [pscustomobject]@{
        Words = $word
        Broad = [regex]::new('\b(' + ($broad -join '|') + ')\b',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        Upper = [regex]::new('\b(' + ($upper -join '|') + ')\b',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        Compound = [regex]::new('(' + ($compound -join '|') + ')',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
}

function Test-ExcludedPath {
    param([string] $RelativePath)
    $segments = $RelativePath -replace '\\', '/' -split '/'
    return $segments -contains 'bin' -or $segments -contains 'obj' -or
        $segments -contains '.utmp' -or $segments -contains 'hist-docs' -or
        $segments -contains 'source-snapshots' -or $segments -contains 'refactor-audit'
}

function Get-ActiveFiles {
    param([string] $ScanRoot)
    $scope = @('CLAUDE.md', 'src', 'tests', 'unity/Emberfall/Assets', 'tooling', 'tools', '.github')
    foreach ($relative in $scope) {
        $entry = Join-Path $ScanRoot $relative
        if (-not (Test-Path -LiteralPath $entry)) { continue }
        if ((Get-Item -LiteralPath $entry).PSIsContainer) {
            Get-ChildItem -LiteralPath $entry -File -Recurse | ForEach-Object {
                $path = $_.FullName
                $rootPrefix = $ScanRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $relativePath = $path.Substring($rootPrefix.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                if (-not (Test-ExcludedPath $relativePath)) { $_ }
            }
        }
        else {
            if (-not (Test-ExcludedPath $relative)) { Get-Item -LiteralPath $entry }
        }
    }
}

function Test-TextFile {
    param([IO.FileInfo] $File)
    $stream = [IO.File]::OpenRead($File.FullName)
    try {
        $length = [Math]::Min($stream.Length, 8192)
        $bytes = New-Object byte[] $length
        [void] $stream.Read($bytes, 0, $length)
        return -not ($bytes -contains 0)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RootRelativePath {
    param([string] $ScanRoot, [string] $FullPath)
    $prefix = $ScanRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $FullPath.Substring($prefix.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -replace '\\', '/'
}

function Convert-PathToSearchText {
    param([string] $RelativePath)
    $withAcronymBoundaries = $RelativePath -creplace '([A-Z]+)([A-Z][a-z])', '$1 $2'
    $withCamelCaseBoundaries = $withAcronymBoundaries -creplace '([a-z0-9])([A-Z])', '$1 $2'
    return $withCamelCaseBoundaries -replace '[/\\._-]+', ' '
}

function Invoke-VocabularyScan {
    param(
        [string] $ScanRoot,
        [string[]] $AllowedNotes,
        [switch] $Quiet
    )

    $patterns = New-Patterns
    $normalRoot = (Resolve-Path -LiteralPath $ScanRoot).Path
    $allowedLicensePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @(
        'unity/Emberfall/Assets/Art/Fonts/AlfaSlabOne-OFL.txt',
        'unity/Emberfall/Assets/Art/Fonts/GoogleSans-OFL.txt',
        'unity/Emberfall/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt'
    )) { [void] $allowedLicensePaths.Add($path) }

    $allowedNotePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($note in $AllowedNotes) {
        $fullNote = if ([IO.Path]::IsPathRooted($note)) { $note } else { Join-Path $normalRoot $note }
        if (-not (Test-Path -LiteralPath $fullNote -PathType Leaf)) {
            throw "Migration note does not exist: $note"
        }
        [void] $allowedNotePaths.Add((Get-RootRelativePath $normalRoot (Resolve-Path -LiteralPath $fullNote).Path))
    }

    $licensePhrase = [regex]::new('\bfree\s+of\s+' + [regex]::Escape($patterns.Words.b.ToLowerInvariant()) + '\b',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $allowedLicenseHitsSeen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $counts = @{ Broad = 0; Upper = 0; Compound = 0; Path = 0; Allowed = 0; Unexplained = 0; Files = 0 }
    $findings = [System.Collections.Generic.List[object]]::new()

    foreach ($file in Get-ActiveFiles $normalRoot) {
        $relative = Get-RootRelativePath $normalRoot $file.FullName
        $pathText = Convert-PathToSearchText $relative
        foreach ($match in $patterns.Broad.Matches($pathText)) {
            $counts.Path++
            $counts.Unexplained++
            $findings.Add([pscustomobject]@{ Path = $relative; Line = $null; Token = $match.Value; Pattern = 'path' })
        }

        if (-not (Test-TextFile $file)) { continue }
        $counts.Files++
        $lines = [IO.File]::ReadAllLines($file.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            $lineNumber = $index + 1
            foreach ($entry in @(
                [pscustomobject]@{ Name = 'broad'; Regex = $patterns.Broad; Count = 'Broad' },
                [pscustomobject]@{ Name = 'canonical-uppercase'; Regex = $patterns.Upper; Count = 'Upper' },
                [pscustomobject]@{ Name = 'compound'; Regex = $patterns.Compound; Count = 'Compound' }
            )) {
                foreach ($match in $entry.Regex.Matches($line)) {
                    $counts[$entry.Count]++
                    $licenseException = $allowedLicensePaths.Contains($relative) -and
                        $match.Value.Equals($patterns.Words.b, [StringComparison]::OrdinalIgnoreCase) -and
                        $licensePhrase.IsMatch($line) -and $allowedLicenseHitsSeen.Add($relative)
                    $isAllowed = $allowedNotePaths.Contains($relative) -or $licenseException
                    if ($isAllowed) {
                        $counts.Allowed++
                        continue
                    }
                    $counts.Unexplained++
                    $findings.Add([pscustomobject]@{ Path = $relative; Line = $lineNumber; Token = $match.Value; Pattern = $entry.Name })
                }
            }
        }
    }

    if (-not $Quiet -and -not $SummaryOnly) {
        foreach ($finding in $findings | Sort-Object Path, Line, Pattern, Token) {
            if ($finding.Pattern -eq 'path') {
                Write-Host ('VOCABULARY GATE FAIL [path] {0} token={1}' -f $finding.Path, $finding.Token)
            }
            else {
                Write-Host ('VOCABULARY GATE FAIL [{0}] {1}:{2} token={3}' -f $finding.Pattern, $finding.Path, $finding.Line, $finding.Token)
            }
        }
    }
    if (-not $Quiet) {
        Write-Host ('ACTIVE_FILES={0}' -f $counts.Files)
        Write-Host ('BROAD_HITS={0}' -f $counts.Broad)
        Write-Host ('CANONICAL_UPPERCASE_HITS={0}' -f $counts.Upper)
        Write-Host ('COMPOUND_SYMBOL_HITS={0}' -f $counts.Compound)
        Write-Host ('PATH_NAME_HITS={0}' -f $counts.Path)
        Write-Host ('ALLOWED_HITS={0}' -f $counts.Allowed)
        Write-Host ('UNEXPLAINED_HITS={0}' -f $counts.Unexplained)
    }
    return [pscustomobject]$counts
}

function Invoke-SelfTest {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('vocabulary-gate-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $testRoot 'src') | Out-Null
        $font = Join-Path $testRoot 'unity/Emberfall/Assets/Art/Fonts/AlfaSlabOne-OFL.txt'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $font) | Out-Null
        [IO.File]::WriteAllText((Join-Path $testRoot 'src/clean.cs'), 'public sealed class Clean {}')
        $patterns = New-Patterns
        [IO.File]::WriteAllText($font, 'Permission is granted, free of ' + $patterns.Words.b.ToLowerInvariant() + ', to any person.')

        $clean = Invoke-VocabularyScan -ScanRoot $testRoot -AllowedNotes @() -Quiet
        if ($clean.Unexplained -ne 0 -or $clean.Allowed -ne 1) {
            throw "self-test clean case failed: unexplained=$($clean.Unexplained), allowed=$($clean.Allowed)"
        }

        $note = Join-Path $testRoot 'tools/migration-note.md'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $note) | Out-Null
        [IO.File]::WriteAllText($note, $patterns.Words.k)
        $noted = Invoke-VocabularyScan -ScanRoot $testRoot -AllowedNotes @('tools/migration-note.md') -Quiet
        if ($noted.Unexplained -ne 0 -or $noted.Allowed -ne 3) {
            throw "self-test migration-note case failed: unexplained=$($noted.Unexplained), allowed=$($noted.Allowed)"
        }
        Remove-Item -LiteralPath $note -Force

        $pathFailure = Join-Path $testRoot ('src/' + $patterns.Words.g + 'Fixture.cs')
        [IO.File]::WriteAllText($pathFailure, 'public sealed class Fixture {}')
        $pathRed = Invoke-VocabularyScan -ScanRoot $testRoot -AllowedNotes @() -Quiet
        if ($pathRed.Unexplained -ne 1 -or $pathRed.Path -ne 1) {
            throw "self-test path case failed: unexplained=$($pathRed.Unexplained), path=$($pathRed.Path)"
        }
        Remove-Item -LiteralPath $pathFailure -Force

        [IO.File]::WriteAllText((Join-Path $testRoot 'src/bad.cs'), $patterns.Words.j + [Environment]::NewLine + 'Keyword.' + $patterns.Words.a)
        $red = Invoke-VocabularyScan -ScanRoot $testRoot -AllowedNotes @() -Quiet
        if ($red.Unexplained -lt 4) {
            throw "self-test failing case did not report every pattern match: unexplained=$($red.Unexplained)"
        }

        Write-Host 'SELF_TEST_CLEAN=PASS'
        Write-Host 'SELF_TEST_PATH_FAILURE_DETECTED=PASS'
        Write-Host ('SELF_TEST_PATH_FAILURE_HITS={0}' -f $pathRed.Path)
        Write-Host 'SELF_TEST_FAILURE_DETECTED=PASS'
        Write-Host ('SELF_TEST_FAILURE_HITS={0}' -f $red.Unexplained)
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    }
}

if (-not $Root) {
    $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

$result = Invoke-VocabularyScan -ScanRoot $Root -AllowedNotes $MigrationNote
if ($result.Unexplained -gt 0) { exit 1 }
Write-Host 'VOCABULARY_GATE=PASS'
exit 0
