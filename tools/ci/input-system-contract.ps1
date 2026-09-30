$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assetPath = Join-Path $repoRoot 'unity\Assets\Settings\ActionSet.inputactions'
$metaPath = "$assetPath.meta"
$presentationPath = Join-Path $repoRoot 'unity\Assets\Scripts\Presentation'
$wrapperPath = Join-Path $presentationPath 'EmberfallActions.cs'
$assemblyPath = Join-Path $presentationPath 'Emberfall.Presentation.asmdef'

$asset = Get-Content -LiteralPath $assetPath -Raw | ConvertFrom-Json
$map = $asset.maps | Where-Object name -eq 'ScreenActions'
if ($null -eq $map) { throw 'ActionSet must contain the ScreenActions map.' }

$press = $map.actions | Where-Object name -eq 'Press'
$position = $map.actions | Where-Object name -eq 'Position'
if ($null -eq $press -or $press.type -ne 'Button' -or $press.expectedControlType -notin @('', 'Button')) {
    throw 'ScreenActions/Press must be a Button action expecting a Button control.'
}
if ($null -eq $position -or $position.type -ne 'PassThrough' -or $position.expectedControlType -ne 'Vector2') {
    throw 'ScreenActions/Position must be a Pass Through action expecting a Vector2 control.'
}

$pressBindings = @($map.bindings | Where-Object action -eq 'Press')
$positionBindings = @($map.bindings | Where-Object action -eq 'Position')
if ($pressBindings.Count -ne 1 -or $pressBindings[0].path -ne '<Pointer>/press') {
    throw 'ScreenActions/Press must have the single binding <Pointer>/press.'
}
if ($positionBindings.Count -ne 1 -or $positionBindings[0].path -ne '<Pointer>/position') {
    throw 'ScreenActions/Position must have the single binding <Pointer>/position.'
}
if (@($map.actions | Where-Object name -in @('Drag', 'Release', 'TapClick')).Count -ne 0) {
    throw 'Drag, Release, and TapClick must be derived from Press plus Position, not separate actions.'
}

$meta = Get-Content -LiteralPath $metaPath -Raw
if ($meta -notmatch '(?m)^\s*generateWrapperCode:\s*1\s*$' -or
    $meta -notmatch '(?m)^\s*wrapperCodePath:\s*Assets/Scripts/Presentation/EmberfallActions\.cs\s*$' -or
    -not (Test-Path -LiteralPath $wrapperPath)) {
    throw 'ActionSet must generate EmberfallActions.cs inside the Presentation assembly.'
}

$assembly = Get-Content -LiteralPath $assemblyPath -Raw | ConvertFrom-Json
if ('Unity.InputSystem' -notin @($assembly.references)) {
    throw 'Emberfall.Presentation must reference the Unity.InputSystem assembly.'
}

$invalid = Get-ChildItem -LiteralPath $presentationPath -Filter '*.cs' -Recurse |
    Select-String -Pattern 'ButtonControl\.|\bInput\.(mousePosition|GetMouseButton|GetMouseButtonDown|GetMouseButtonUp|GetKeyDown)\b|InputSystem\.EnhancedTouch\.Enable\(\)' |
    Where-Object { $_.Line -notmatch '^\s*///?' }
if ($invalid) {
    $details = ($invalid | ForEach-Object { "{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim() }) -join [Environment]::NewLine
    throw "Presentation input still uses invalid or legacy polling APIs:$([Environment]::NewLine)$details"
}

Write-Host 'Input System contract passed: Press + Position drive pointer gestures.'
