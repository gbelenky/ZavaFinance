#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$WorkspaceId,
    [Parameter(Mandatory)][guid]$LakehouseId,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.-]{0,63}$')][string]$CatalogVersion,
    [string]$SearchIndex = '',
    [string]$EmbeddingDeployment = 'text-embedding-3-large',
    [ValidateRange(1,3072)][int]$EmbeddingDimensions = 1536,
    [switch]$ActivateRelease,
    [ValidateRange(60,7200)][int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ActivateRelease -and !$SearchIndex) { throw 'SearchIndex is required when activating a verified release.' }
$baseUri = 'https://api.fabric.microsoft.com/v1'
$workspaceUri = "$baseUri/workspaces/$WorkspaceId"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

function Invoke-Fabric([string]$Method, [string]$Uri, $Body = $null) {
    if (![uri]::IsWellFormedUriString($Uri, [UriKind]::Absolute) -or ([uri]$Uri).Host -ne 'api.fabric.microsoft.com') {
        throw "Unexpected Fabric operation location: $Uri"
    }
    $token = az account get-access-token --resource 'https://api.fabric.microsoft.com' --query accessToken --output tsv
    if ($LASTEXITCODE -ne 0 -or !$token) { throw 'Azure CLI could not acquire the Fabric token.' }
    try {
        $args = @{ Method=$Method; Uri=$Uri; Headers=@{Authorization="Bearer $($token.Trim())"}; ContentType='application/json'; TimeoutSec=120 }
        if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 30 -Compress }
        Invoke-WebRequest @args
    } finally { $token = $null }
}

function Wait-FabricOperation($Response) {
    if ($Response.StatusCode -ne 202) { return ($Response.Content | ConvertFrom-Json) }
    $location = [string]($Response.Headers['Location'] | Select-Object -First 1)
    $operationPath = ([uri]$location).AbsolutePath
    if ($operationPath -notmatch '^/v1/(operations/[a-fA-F0-9-]+|workspaces/[a-fA-F0-9-]+/items/[a-fA-F0-9-]+/jobs/instances/[a-fA-F0-9-]+)$') {
        throw "Unexpected Fabric operation path: $operationPath"
    }
    # Regional Location headers are polled through the documented global API.
    $location = "https://api.fabric.microsoft.com$operationPath"
    do {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Fabric operation timed out. Inspect $location before retrying." }
        $delay = 15
        if ($Response.Headers.ContainsKey('Retry-After')) { $delay = [Math]::Max(1, [int]($Response.Headers['Retry-After'] | Select-Object -First 1)) }
        Start-Sleep -Seconds $delay
        $Response = Invoke-Fabric GET $location
        $state = $Response.Content | ConvertFrom-Json
        if ($state.status -in @('Failed','Cancelled','Canceled')) { throw "Fabric operation $($state.status): $($state | ConvertTo-Json -Depth 10 -Compress)" }
    } while ($state.status -notin @('Succeeded','Completed'))
    if ($location -match '/operations/') { return ((Invoke-Fabric GET "$location/result").Content | ConvertFrom-Json) }
    return $state
}

$settings = @{
    workspace_id="$WorkspaceId"; lakehouse_id="$LakehouseId"; catalog_version=$CatalogVersion
    search_index=$SearchIndex; embedding_deployment=$EmbeddingDeployment; embedding_dimensions=$EmbeddingDimensions
    activate_release=[bool]$ActivateRelease
} | ConvertTo-Json -Compress
$settingsLiteral = $settings | ConvertTo-Json -Compress
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fabric\resolver_catalog.py') -Raw
$notebook = @{
    nbformat=4; nbformat_minor=5
    metadata=@{ language_info=@{name='python'}; kernel_info=@{name='synapse_pyspark'} }
    cells=@(
        @{cell_type='code'; execution_count=$null; outputs=@(); metadata=@{};
          source=@("import json`nresolver_settings = json.loads($settingsLiteral)`n")},
        @{cell_type='code'; execution_count=$null; outputs=@(); metadata=@{}; source=@($source)}
    )
} | ConvertTo-Json -Depth 30 -Compress
$definition = @{format='ipynb'; parts=@(@{
    path='artifact.content.ipynb'; payloadType='InlineBase64'
    payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($notebook))
})}
$name = "Zava resolver $CatalogVersion $(if ($ActivateRelease) {'activate'} else {'stage'})"
$items = @()
$uri = "$workspaceUri/notebooks"
do {
    $page = (Invoke-Fabric GET $uri).Content | ConvertFrom-Json
    $items += $page.value
    $uri = if ($page.PSObject.Properties.Name -contains 'continuationUri') { $page.continuationUri } else { $null }
} while ($uri)
$matches = @($items | Where-Object displayName -eq $name)
if ($matches.Count -gt 1) { throw "Multiple notebooks named $name; resolve explicitly before running." }
if ($matches.Count -eq 1) {
    $item = $matches[0]
    # Never update an active run's code. A completed version-specific notebook is safe to retry.
    $jobs = (Invoke-Fabric GET "$workspaceUri/items/$($item.id)/jobs/instances").Content | ConvertFrom-Json
    if (@($jobs.value | Where-Object status -in @('NotStarted','InProgress','Running')).Count) {
        throw "Notebook $($item.id) already has an active job. Inspect it before retrying."
    }
    $update = Invoke-Fabric POST "$workspaceUri/notebooks/$($item.id)/updateDefinition" @{definition=$definition}
    if ($update.StatusCode -eq 202) { [void](Wait-FabricOperation $update) }
} else {
    $item = Wait-FabricOperation (Invoke-Fabric POST "$workspaceUri/notebooks" @{
        displayName=$name; description='Versioned Zava reporting metadata; original finance tables remain read-only.'; definition=$definition
    })
}
Write-Host "Notebook $($item.id): $name"
$run = Invoke-Fabric POST "$workspaceUri/items/$($item.id)/jobs/RunNotebook/instances"
$job = Wait-FabricOperation $run
$job | Select-Object id, itemId, status, startTimeUtc, endTimeUtc, failureReason, exitValue | ConvertTo-Json -Depth 10
