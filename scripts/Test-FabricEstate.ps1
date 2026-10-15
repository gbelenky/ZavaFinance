#Requires -Version 7.4
<#
.SYNOPSIS
Validates a package offline or reconciles restored data/definitions using full byte readback and SQL.
.DESCRIPTION
Run before target release activation. Technical validation is not security or cutover approval.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [string]$TargetMap,
    [string]$ReceiptPath,
    [string]$ReadbackPath,
    [string]$SqlClientDirectory,
    [switch]$PackageOnly
)
$ErrorActionPreference = 'Stop'
foreach ($helper in 'Fabric-Estate','Fabric-EstatePackage','Fabric-EstateFabric','Fabric-EstateSql','Fabric-EstateWorkflow') {
    . (Join-Path $PSScriptRoot "$helper.ps1")
}
Test-FabricEstate @PSBoundParameters | ConvertTo-Json -Depth 100
