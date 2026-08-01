# Run-ArtBakeoff.ps1 — drive the 5-prompt art bake-off (dev-docs/specs/card-generation-prompt.md)
# against the Gemini Interactions API (Nano Banana Pro = gemini-3-pro-image).
#
# Key discovery order: $env:GEMINI_API_KEY, else parsed from the repo-root .env
# (first var whose name contains GEMINI or GOOGLE). The key is read inside this
# script and used only in the request header — never echoed.
#
# Usage:
#   .\tooling\scripts\Run-ArtBakeoff.ps1                          # all 5 prompts
#   .\tooling\scripts\Run-ArtBakeoff.ps1 -Only art_last_rites     # one prompt (re-roll)
#   .\tooling\scripts\Run-ArtBakeoff.ps1 -Model gemini-3.1-flash-image   # Nano Banana 2
#
# Output: art-bakeoff/<model>/<artId>.png (+ timing report). art-bakeoff/ is
# gitignored scratch — winners get curated into Assets/ manually after style lock.

param(
    [string]$Model = "gemini-3-pro-image",
    [string]$Only = "",
    [string]$OutRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# ── API key: env var first, then repo-root .env ──────────────────────────────
$apiKey = $env:GEMINI_API_KEY
if (-not $apiKey) {
    $envFile = Join-Path $repoRoot ".env"
    if (Test-Path $envFile) {
        foreach ($line in Get-Content $envFile) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*"?([^"#]+?)"?\s*$') {
                $name = $matches[1]; $value = $matches[2]   # save before the next -match clobbers $matches
                if ($name -match 'GEMINI|GOOGLE') { $apiKey = $value.Trim(); break }
            }
        }
    }
}
if (-not $apiKey) { throw "No API key: set GEMINI_API_KEY or put it in the repo-root .env" }

if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot "art-bakeoff" }
$outDir = Join-Path $OutRoot $Model
New-Item -ItemType Directory -Force $outDir | Out-Null

# ── The 5 bake-off prompts, verbatim from dev-docs/specs/card-generation-prompt.md ──────────
$style = "Stylized fantasy trading-card-game illustration, painterly digital art, dramatic rim lighting, single clear subject with strong silhouette, rich saturated colors, dynamic composition, centered focal point, background supports but never competes with subject, professional card game art quality, no text, no border, no frame, no watermark."

$prompts = [ordered]@{
    "art_hero_gravekeep"      = "$style Gaunt hooded warden holding an iron bell-lantern, face half-skeletal half-living, funerary bronze armor etched with names, standing before a colossal sealed gate wrapped in chains, moths of grave-light circling, bust three-quarter framing facing slightly off-center toward viewer. Palette: bone-white, iron grey, sickly green-gold lantern light."
    "art_hero_power_quicken"  = "$style A mana crystal splitting open with vivid green shoots growing through the cracks, simple iconic composition, subject dead-center with nothing important outside the inner 60 percent of the frame. Palette: forest green, golden honey sap-light, cedar brown."
    "art_thornfang_matron"    = "$style Great briar-furred panther matriarch made of tangled thorns and moss, two small cubs of tangled thorns at her flanks, matriarch is the dominant focal subject. Palette: forest green, artichoke green, cedar brown, golden honey."
    "art_last_rites"          = "$style Gloved hands gently closing a corpse's eyes, two spectral glowing pages rising from the body like smoke, solemn funerary mood. Palette: bone-white, iron grey, funerary bronze, sickly green-gold grave-light."
    "art_elderwood_colossus"  = "$style A mountain-sized walking world-tree, crown of branches above the clouds, a village's worth of massive roots for legs, tiny landscape below for scale, low-angle heroic composition. Palette: deep forest green, golden honey light, cedar brown."
}

$uri = "https://generativelanguage.googleapis.com/v1beta/interactions"
$report = @()

foreach ($artId in $prompts.Keys) {
    if ($Only -and $artId -ne $Only) { continue }

    $body = @{
        model = $Model
        input = @(@{ type = "text"; text = $prompts[$artId] })
        # API only supports image/jpeg output; winners get converted to PNG at curation time
        # (dev-docs/pipelines/art-and-rendering.md:7 applies to the curated asset, not bake-off scratch).
        response_format = @{ type = "image"; mime_type = "image/jpeg"; aspect_ratio = "1:1" }
        store = $false
    } | ConvertTo-Json -Depth 8

    Write-Host "generating $artId ..." -NoNewline
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Method Post -Uri $uri -Body $body `
            -ContentType "application/json" -Headers @{ "x-goog-api-key" = $apiKey }
    } catch {
        $sw.Stop()
        Write-Host " FAILED ($($sw.Elapsed.TotalSeconds.ToString('0.0'))s)"
        if ($_.ErrorDetails.Message) { Write-Host "  $($_.ErrorDetails.Message)" }
        $report += [pscustomobject]@{ artId = $artId; status = "ERROR"; seconds = [math]::Round($sw.Elapsed.TotalSeconds,1); file = "" }
        continue
    }
    $sw.Stop()

    # Image lives in steps[].content[] as a part with base64 `data` (skip thought steps).
    $imgPart = $null
    foreach ($step in $resp.steps) {
        if (-not $step.content) { continue }
        foreach ($part in $step.content) {
            if ($part.data -and ($part.type -eq "image" -or $part.mime_type -like "image/*")) { $imgPart = $part }
        }
    }
    if (-not $imgPart) {
        Write-Host " NO IMAGE in response ($($sw.Elapsed.TotalSeconds.ToString('0.0'))s)"
        $report += [pscustomobject]@{ artId = $artId; status = "NO_IMAGE"; seconds = [math]::Round($sw.Elapsed.TotalSeconds,1); file = "" }
        continue
    }

    $outFile = Join-Path $outDir "$artId.jpg"
    [IO.File]::WriteAllBytes($outFile, [Convert]::FromBase64String($imgPart.data))
    $kb = [math]::Round((Get-Item $outFile).Length / 1kb)
    Write-Host " ok ($($sw.Elapsed.TotalSeconds.ToString('0.0'))s, ${kb} KB)"
    $report += [pscustomobject]@{ artId = $artId; status = "OK"; seconds = [math]::Round($sw.Elapsed.TotalSeconds,1); file = $outFile }
}

""
$report | Format-Table -AutoSize
"Output dir: $outDir"
