#Requires -Version 7.4
<#
.SYNOPSIS
Plans or applies an explicit, non-destructive Fabric estate import.
.DESCRIPTION
Without -Apply, performs read-only target preflight. See docs\fabric-estate-migration.md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$TargetMap,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [switch]$Apply,
    [switch]$TargetIsolated,
    [switch]$AllowPreviewDataAgent
)
$ErrorActionPreference = 'Stop'
foreach ($helper in 'Fabric-Estate','Fabric-EstatePackage','Fabric-EstateFabric','Fabric-EstateSql','Fabric-EstateWorkflow') {
    . (Join-Path $PSScriptRoot "$helper.ps1")
}
Import-FabricEstate @PSBoundParameters | ConvertTo-Json -Depth 100
