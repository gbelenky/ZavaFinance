#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the Teams / Microsoft 365 Copilot app package.

.DESCRIPTION
    Substitutes the manifest placeholders, generates the two required icons if they
    are absent, and produces appPackage/build/zavafinance.zip ready to upload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BotId,
    [Parameter(Mandatory)] [string] $AppHostName
)

$ErrorActionPreference = 'Stop'

$packageDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage'
$buildDir = Join-Path $packageDir 'build'

Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $buildDir | Out-Null

# --- Manifest -------------------------------------------------------------
$manifest = Get-Content (Join-Path $packageDir 'manifest.json') -Raw
$manifest = $manifest.Replace('${{BOT_ID}}', $BotId).Replace('${{APP_HOSTNAME}}', $AppHostName)
Set-Content -Path (Join-Path $buildDir 'manifest.json') -Value $manifest -NoNewline

# --- Icons ----------------------------------------------------------------
# color.png must be 192x192; outline.png must be 32x32, transparent, and a single
# flat colour. Both are committed under appPackage/. They are regenerated here only
# if missing, so the package stays buildable from a clean checkout.
$colorSource = Join-Path $packageDir 'color.png'
$outlineSource = Join-Path $packageDir 'outline.png'

if (-not (Test-Path $colorSource) -or -not (Test-Path $outlineSource)) {
    Write-Host 'Icons missing; regenerating them.'
    & (Join-Path $PSScriptRoot 'new-icons.ps1') -OutputDirectory $packageDir
}

Copy-Item $colorSource (Join-Path $buildDir 'color.png')
Copy-Item $outlineSource (Join-Path $buildDir 'outline.png')

# --- Package --------------------------------------------------------------
$zipPath = Join-Path $buildDir 'zavafinance.zip'
Compress-Archive -Path (Join-Path $buildDir '*.json'), (Join-Path $buildDir '*.png') `
    -DestinationPath $zipPath -Force

Write-Host "App package built: $zipPath"
Write-Host 'Upload it in Teams via Apps > Manage your apps > Upload an app.'
