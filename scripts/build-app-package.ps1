#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the Teams / Microsoft 365 Copilot app package.

.DESCRIPTION
    Substitutes the manifest placeholders, generates the two required icons if they
    are absent. With -Variant DotNet, writes
    appPackage\dotnet\build\zavafinance-one.zip.
    With -Variant Python, uses the shared native Activity icons and writes
    appPackage\python\build\zavafinance-one-python.zip.
    BotId must be the application/client ID of the target bot, not its ARM resource ID.
    UserAuthAppId identifies the separate authorized finance OAuth application.
    UserAuthResource preserves the selected bot's mcs token-exchange resource;
    its URI can differ from the finance app URI used by the connection scopes.
    Supply AppVersion when updating an installed package; otherwise keep the template version.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BotId,
    [Parameter(Mandatory)] [string] $AppHostName,
    [ValidateSet('DotNet', 'Python')] [string] $Variant = 'DotNet',
    [Parameter(Mandatory)] [string] $UserAuthAppId,
    [Parameter(Mandatory)] [string] $UserAuthResource,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [ValidateLength(5, 256)] [string] $AppVersion
)

$ErrorActionPreference = 'Stop'

$parsedBotId = [guid]::Empty
if (-not [guid]::TryParse($BotId, [ref] $parsedBotId) -or $parsedBotId -eq [guid]::Empty) {
    throw 'BotId must be a nonempty application/client GUID.'
}
if ([Uri]::CheckHostName($AppHostName) -ne [UriHostNameType]::Dns) {
    throw 'AppHostName must be a DNS host name, without a scheme, port or path.'
}
$parsedUserAuthId = [guid]::Empty
$resourceUri = $null
if (-not [guid]::TryParse($UserAuthAppId, [ref] $parsedUserAuthId) -or $parsedUserAuthId -eq [guid]::Empty) {
    throw 'UserAuthAppId must be a nonempty application/client GUID.'
}
if ($parsedUserAuthId -eq $parsedBotId) {
    throw 'UserAuthAppId must identify the separate finance OAuth application, not the bot identity.'
}
if (-not [Uri]::TryCreate($UserAuthResource, [UriKind]::Absolute, [ref] $resourceUri) `
    -or $resourceUri.Scheme -notin @('api', 'https') `
    -or $resourceUri.Query -or $resourceUri.Fragment -or $resourceUri.UserInfo) {
    throw 'UserAuthResource must be the API application ID URI, without credentials, query or fragment.'
}

# --- Manifest -------------------------------------------------------------
$packageDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage'
$manifest = Get-Content (Join-Path $packageDir 'manifest.json') -Raw
$manifest = $manifest.Replace('${{BOT_ID}}', $parsedBotId.ToString()).Replace('${{APP_HOSTNAME}}', $AppHostName)
$variantManifest = $manifest | ConvertFrom-Json
if ($PSBoundParameters.ContainsKey('AppVersion')) {
    $variantManifest.version = $AppVersion
}
$variantManifest.webApplicationInfo.id = $parsedUserAuthId.ToString()
$variantManifest.webApplicationInfo.resource = $UserAuthResource
$displayName = if ($Variant -eq 'Python') { 'Zava Finance Python' } else { 'Zava Finance .NET' }
$variantManifest.name.short = $displayName
$variantManifest.name.full = $displayName
$variantManifest.accentColor = '#087F8C'
$manifest = $variantManifest | ConvertTo-Json -Depth 30
$iconDirectory = Join-Path $packageDir 'icons'
$iconScript = 'new-icons.ps1'
$packageDir = Join-Path $packageDir $Variant.ToLowerInvariant()
$packageFileName = if ($Variant -eq 'Python') { 'zavafinance-one-python.zip' } else { 'zavafinance-one.zip' }

$buildDir = Join-Path $packageDir 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Set-Content -Path (Join-Path $buildDir 'manifest.json') -Value $manifest -NoNewline

# --- Icons ----------------------------------------------------------------
# color.png must be 192x192; outline.png must be 32x32, transparent, and a single
# flat colour. Both are committed under appPackage/icons. They are regenerated here only
# if missing, so the package stays buildable from a clean checkout.
$colorSource = Join-Path $iconDirectory 'color.png'
$outlineSource = Join-Path $iconDirectory 'outline.png'

if (-not (Test-Path $colorSource) -or -not (Test-Path $outlineSource)) {
    Write-Host 'Icons missing; regenerating them.'
    & (Join-Path $PSScriptRoot $iconScript) -OutputDirectory $iconDirectory
}

Copy-Item $colorSource (Join-Path $buildDir 'color.png')
Copy-Item $outlineSource (Join-Path $buildDir 'outline.png')

# --- Package --------------------------------------------------------------
$zipPath = Join-Path $buildDir $packageFileName
Compress-Archive -Path (Join-Path $buildDir 'manifest.json'), `
    (Join-Path $buildDir 'color.png'), (Join-Path $buildDir 'outline.png') `
    -DestinationPath $zipPath -Force

Write-Host "App package built: $zipPath"
Write-Host 'Upload it in Teams via Apps > Manage your apps > Upload an app.'
