#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the Teams / Microsoft 365 Copilot app package.

.DESCRIPTION
    Substitutes the manifest placeholders, generates the two required icons if they
    are absent, and produces appPackage\build\zavafinance.zip.
    With -Variant One, uses separate branding and writes
    appPackage\one\build\zavafinance-one.zip without changing the original package.
    With -Variant Python, reuses the One icons and writes a distinct
    appPackage\python\build\zavafinance-one-python.zip.
    BotId must be the application/client ID of the target bot, not its ARM resource ID.
    When the bot and delegated-finance OAuth application differ, supply both
    UserAuthAppId and UserAuthResource from the bot's OAuth connection.
    Supply AppVersion when updating an installed package; otherwise keep the template version.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BotId,
    [Parameter(Mandatory)] [string] $AppHostName,
    [ValidateSet('Original', 'One', 'Python')] [string] $Variant = 'Original',
    [string] $UserAuthAppId,
    [string] $UserAuthResource,
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
if ($PSBoundParameters.ContainsKey('UserAuthAppId') -or $PSBoundParameters.ContainsKey('UserAuthResource')) {
    $parsedUserAuthId = [guid]::Empty
    $resourceUri = $null
    if (-not [guid]::TryParse($UserAuthAppId, [ref] $parsedUserAuthId) -or $parsedUserAuthId -eq [guid]::Empty) {
        throw 'UserAuthAppId must be a nonempty application/client GUID.'
    }
    if (-not [Uri]::TryCreate($UserAuthResource, [UriKind]::Absolute, [ref] $resourceUri) `
        -or $resourceUri.Scheme -notin @('api', 'https') `
        -or $resourceUri.Query -or $resourceUri.Fragment -or $resourceUri.UserInfo) {
        throw 'UserAuthResource must be the API application ID URI, without credentials, query or fragment.'
    }
}

# --- Manifest -------------------------------------------------------------
$packageDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage'
$manifest = Get-Content (Join-Path $packageDir 'manifest.json') -Raw
$manifest = $manifest.Replace('${{BOT_ID}}', $parsedBotId.ToString()).Replace('${{APP_HOSTNAME}}', $AppHostName)
if ($PSBoundParameters.ContainsKey('AppVersion')) {
    $versionedManifest = $manifest | ConvertFrom-Json
    $versionedManifest.version = $AppVersion
    $manifest = $versionedManifest | ConvertTo-Json -Depth 30
}
$iconScript = 'new-icons.ps1'
$packageFileName = 'zavafinance.zip'
if ($PSBoundParameters.ContainsKey('UserAuthAppId')) {
    $authManifest = $manifest | ConvertFrom-Json
    $authManifest.webApplicationInfo.id = $parsedUserAuthId.ToString()
    $authManifest.webApplicationInfo.resource = $UserAuthResource
    $manifest = $authManifest | ConvertTo-Json -Depth 30
}
if ($Variant -in @('One', 'Python')) {
    $oneManifest = $manifest | ConvertFrom-Json
    $displayName = if ($Variant -eq 'Python') { 'Zava Finance One Python' } else { 'Zava Finance One' }
    $oneManifest.name.short = $displayName
    $oneManifest.name.full = $displayName
    $oneManifest.accentColor = '#087F8C'
    $manifest = $oneManifest | ConvertTo-Json -Depth 30
    $packageDir = Join-Path $packageDir 'one'
    $iconScript = 'new-one-icons.ps1'
    $packageFileName = 'zavafinance-one.zip'
}
$iconDirectory = $packageDir
if ($Variant -eq 'Python') {
    $packageDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage\python'
    $packageFileName = 'zavafinance-one-python.zip'
}

$buildDir = Join-Path $packageDir 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
Set-Content -Path (Join-Path $buildDir 'manifest.json') -Value $manifest -NoNewline

# --- Icons ----------------------------------------------------------------
# color.png must be 192x192; outline.png must be 32x32, transparent, and a single
# flat colour. Both are committed under appPackage/. They are regenerated here only
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
