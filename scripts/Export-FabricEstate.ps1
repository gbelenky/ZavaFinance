#Requires -Version 7.4
<#
.SYNOPSIS
Inventories or exports a quiesced Fabric estate into a new customer-controlled directory.
.DESCRIPTION
See docs\fabric-estate-migration.md. No logins, grants, publication or cutover are performed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceConfig,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$InventoryOnly,
    [switch]$SourceQuiesced,
    [switch]$DefinitionContentApproved,
    [switch]$AllowPreviewDataAgent,
    [string]$SqlClientDirectory
)
$ErrorActionPreference = 'Stop'
foreach ($helper in 'Fabric-Estate','Fabric-EstatePackage','Fabric-EstateFabric','Fabric-EstateSql','Fabric-EstateWorkflow') {
    . (Join-Path $PSScriptRoot "$helper.ps1")
}
Export-FabricEstate @PSBoundParameters | ConvertTo-Json -Depth 100
