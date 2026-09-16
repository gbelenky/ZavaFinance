#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SqlServer,
    [Parameter(Mandatory)][string]$SqlDatabase,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.-]{0,63}$')][string]$CatalogVersion,
    [Parameter(Mandatory)][ValidatePattern('^https://[a-z0-9-]+\.search\.windows\.net/?$')][string]$SearchEndpoint,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{1,126}[a-z0-9]$')][string]$SearchIndex,
    [Parameter(Mandatory)][ValidatePattern('^https://[a-z0-9-]+\.(openai\.azure\.com|cognitiveservices\.azure\.com)/?$')][string]$EmbeddingEndpoint,
    [string]$EmbeddingDeployment = 'text-embedding-3-large',
    [ValidateRange(1,3072)][int]$EmbeddingDimensions = 1536,
    [string]$SqlClientDirectory = (Join-Path $PSScriptRoot '..\tests\ZavaFinance.Tests\bin\Release\net10.0')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Resolver-Sql.ps1')
$SearchEndpoint = $SearchEndpoint.TrimEnd('/')
$EmbeddingEndpoint = $EmbeddingEndpoint.TrimEnd('/')
$versionSuffix = $CatalogVersion.ToLowerInvariant().Replace('.', '-')
if (!$SearchIndex.EndsWith($versionSuffix)) { throw 'Use a version-specific SearchIndex ending with the catalogue version (dots replaced by hyphens).' }

function Invoke-AuthenticatedJson([string]$Method, [string]$Uri, [string]$Resource, $Body = $null, [switch]$AllowNotFound) {
    $token = az account get-access-token --resource $Resource --query accessToken --output tsv
    if ($LASTEXITCODE -ne 0 -or !$token) { throw "Azure CLI could not acquire a token for $Resource." }
    try {
        $args = @{ Method=$Method; Uri=$Uri; Headers=@{Authorization="Bearer $($token.Trim())"}; ContentType='application/json'; TimeoutSec=120; SkipHttpErrorCheck=$true }
        if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 40 -Compress }
        $response = Invoke-WebRequest @args
        if ($AllowNotFound -and $response.StatusCode -eq 404) { return $null }
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            throw "HTTP $($response.StatusCode) from $Uri : $($response.Content)"
        }
        if ($response.Content) { return ($response.Content | ConvertFrom-Json) }
    } finally { $token = $null }
}

$catalog = Invoke-ResolverSql -Server $SqlServer -Database $SqlDatabase -SqlClientDirectory $SqlClientDirectory -Query @'
SELECT catalog_version, entity_id, entity_kind, canonical_name, aliases_json, definition,
       parent_id, hierarchy_path, kpi_code, region_code, department_code, department_group, is_reportable
FROM dbo.resolver_catalog WHERE catalog_version = @version ORDER BY entity_id;
'@ -Parameters @{'@version'=$CatalogVersion}
if ($catalog.Rows.Count -eq 0) { throw 'Staged catalogue is empty or not yet synchronized into the SQL endpoint.' }
if (@($catalog.Rows | Group-Object entity_id | Where-Object Count -ne 1).Count) { throw 'Duplicate catalogue IDs.' }
$indexUri = "$SearchEndpoint/indexes/$SearchIndex`?api-version=2024-07-01"
$existing = Invoke-AuthenticatedJson GET $indexUri 'https://search.azure.com' -AllowNotFound
if ($existing) {
    $otherVersions = Invoke-AuthenticatedJson POST "$SearchEndpoint/indexes/$SearchIndex/docs/search?api-version=2024-07-01" 'https://search.azure.com' @{
        search='*'; filter="catalog_version ne '$CatalogVersion'"; count=$true; top=0
    }
    if ($otherVersions.'@odata.count' -ne 0) { throw 'Index contains a different catalogue version; choose a new index.' }
}
$fields = @(
    @{name='entity_id';type='Edm.String';key=$true;filterable=$true}
    @{name='catalog_version';type='Edm.String';filterable=$true}
    @{name='entity_kind';type='Edm.String';filterable=$true}
    @{name='canonical_name';type='Edm.String';searchable=$true;analyzer='en.microsoft'}
    @{name='aliases';type='Collection(Edm.String)';searchable=$true;analyzer='en.microsoft'}
    @{name='definition';type='Edm.String';searchable=$true;analyzer='en.microsoft'}
    @{name='hierarchy_path';type='Edm.String';searchable=$true}
    @{name='parent_id';type='Edm.String';filterable=$true}
    @{name='kpi_code';type='Edm.String';filterable=$true}
    @{name='region_code';type='Edm.String';filterable=$true}
    @{name='department_code';type='Edm.String';filterable=$true}
    @{name='department_group';type='Edm.String';filterable=$true}
    @{name='is_reportable';type='Edm.Boolean';filterable=$true}
    @{name='content_vector';type='Collection(Edm.Single)';searchable=$true;retrievable=$false;dimensions=$EmbeddingDimensions;vectorSearchProfile='resolver-vector'}
)
[void](Invoke-AuthenticatedJson PUT $indexUri 'https://search.azure.com' @{
    name=$SearchIndex; fields=$fields
    vectorSearch=@{
        algorithms=@(@{name='resolver-hnsw';kind='hnsw';hnswParameters=@{metric='cosine';m=4;efConstruction=400;efSearch=500}})
        profiles=@(@{name='resolver-vector';algorithm='resolver-hnsw'})
    }
    semantic=@{defaultConfiguration='resolver-semantic';configurations=@(@{
        name='resolver-semantic';prioritizedFields=@{
            titleField=@{fieldName='canonical_name'}
            prioritizedContentFields=@(@{fieldName='definition'},@{fieldName='hierarchy_path'})
            prioritizedKeywordsFields=@(@{fieldName='aliases'})
        }
    })}
})
$embeddingUri = "$EmbeddingEndpoint/openai/deployments/$([Uri]::EscapeDataString($EmbeddingDeployment))/embeddings?api-version=2024-10-21"
$documents = [Collections.Generic.List[object]]::new()
for ($offset = 0; $offset -lt $catalog.Rows.Count; $offset += 16) {
    $end = [Math]::Min($offset + 15, $catalog.Rows.Count - 1)
    $rows = @($catalog.Rows[$offset..$end])
    $inputs = @($rows | ForEach-Object { "$($_.canonical_name). $($_.hierarchy_path). $($_.definition) Aliases: $($_.aliases_json)" })
    $embeddings = Invoke-AuthenticatedJson POST $embeddingUri 'https://cognitiveservices.azure.com' @{input=$inputs;dimensions=$EmbeddingDimensions}
    if ($embeddings.data.Count -ne $rows.Count) { throw 'Embedding response count mismatch.' }
    foreach ($item in $embeddings.data) {
        if ($item.index -lt 0 -or $item.index -ge $rows.Count -or $item.embedding.Count -ne $EmbeddingDimensions) {
            throw 'Embedding response has an invalid index or vector dimension.'
        }
        $row = $rows[$item.index]
        $document = @{'@search.action'='mergeOrUpload'}
        foreach ($column in $catalog.Columns) {
            if ($column.ColumnName -eq 'aliases_json') { continue }
            $value = $row[$column]
            $document[$column.ColumnName] = if ($value -is [DBNull]) { $null } else { $value }
        }
        $document.aliases = @($row.aliases_json | ConvertFrom-Json)
        $document.content_vector = @($item.embedding)
        $documents.Add($document)
    }
}
if (@($documents | Group-Object entity_id | Where-Object Count -ne 1).Count) { throw 'Embedding response duplicated entity indexes.' }
$upload = Invoke-AuthenticatedJson POST "$SearchEndpoint/indexes/$SearchIndex/docs/index?api-version=2024-07-01" 'https://search.azure.com' @{value=@($documents.ToArray())}
if ($upload.value.Count -ne $documents.Count -or @($upload.value | Where-Object status -ne $true).Count) {
    throw "Search indexing failed: $($upload | ConvertTo-Json -Depth 10 -Compress)"
}
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
do {
    $verification = Invoke-AuthenticatedJson POST "$SearchEndpoint/indexes/$SearchIndex/docs/search?api-version=2024-07-01" 'https://search.azure.com' @{
        search='*'; select='entity_id,catalog_version'; count=$true; top=1000
    }
    if ($verification.'@odata.count' -eq $documents.Count) { break }
    Start-Sleep -Seconds 5
} while ([DateTimeOffset]::UtcNow -lt $deadline)
$expectedIds = @($documents | ForEach-Object entity_id | Sort-Object)
$actualIds = @($verification.value | ForEach-Object entity_id | Sort-Object)
if ($verification.'@odata.count' -ne $documents.Count -or
    (Compare-Object $expectedIds $actualIds) -or
    @($verification.value | Where-Object catalog_version -ne $CatalogVersion).Count) {
    throw 'Search document verification failed. Do not activate the Fabric release.'
}
$probeText = 'gross profit as a share of net sales'
$probeEmbedding = Invoke-AuthenticatedJson POST $embeddingUri 'https://cognitiveservices.azure.com' @{input=@($probeText);dimensions=$EmbeddingDimensions}
if ($probeEmbedding.data.Count -ne 1 -or $probeEmbedding.data[0].embedding.Count -ne $EmbeddingDimensions) {
    throw 'Search probe embedding response is invalid.'
}
$probe = Invoke-AuthenticatedJson POST "$SearchEndpoint/indexes/$SearchIndex/docs/search?api-version=2024-07-01" 'https://search.azure.com' @{
    search=$probeText; queryType='semantic'; semanticConfiguration='resolver-semantic'; top=3
    select='entity_id,canonical_name'; filter="entity_kind eq 'kpi' and catalog_version eq '$CatalogVersion'"
    vectorQueries=@(@{kind='vector';vector=@($probeEmbedding.data[0].embedding);fields='content_vector';k=50})
}
if (!$probe.value.Count -or !($probe.value[0].PSObject.Properties.Name -contains '@search.rerankerScore')) {
    throw 'Hybrid semantic search probe did not return reranked candidates.'
}
$expectedCandidateRank = [Array]::IndexOf(@($probe.value.entity_id), 'kpi-kpi-006') + 1
if ($expectedCandidateRank -eq 0) {
    throw 'Search probe did not retrieve gross margin in its top three candidates. Review before activating the release.'
}
[pscustomobject]@{catalogVersion=$CatalogVersion; searchIndex=$SearchIndex; documents=$documents.Count;
    embeddingDeployment=$EmbeddingDeployment; embeddingDimensions=$EmbeddingDimensions; semanticSearchVerified=$true;
    expectedCandidateRank=$expectedCandidateRank; probeCandidateIds=@($probe.value.entity_id);
    nextStep='Activate this release with Publish-ResolverData.ps1 -ActivateRelease only after reviewing the results.'} | ConvertTo-Json
