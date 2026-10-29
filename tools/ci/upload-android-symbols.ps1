# Retry a deferred upload without opening Unity or rebuilding the app.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Job, [switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'android-build-support.ps1')
Invoke-AndroidSymbolUploadJob -Job $Job -ValidateOnly:$ValidateOnly
