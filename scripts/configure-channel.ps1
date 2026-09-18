#requires -Version 7.2
<#
.SYNOPSIS
    Configures the selected native Activity bot, runtime permissions and app package.
.DESCRIPTION
    Requires a successful targeted deployment of the selected DotNet or Python service.
    Preserves that bot's existing mcs connection exactly. A missing connection requires
    explicit DelegatedScopes and UserAuthResource (or variant-prefixed azd settings).
    Reuses the authorized OBO application from azd; never edits its app registration
    or the other variant's bot. Does not read any superseded bot.
    Secure Bicep parameters are supplied through process environment variables.
    TokenExchangeResource is an alias for UserAuthResource. An existing connection's
    token-exchange URI may identify its own bot while scopes target the finance app.
    ONE/PYTHON_USER_AUTH_RESOURCE and ONE/PYTHON_DELEGATED_SCOPES must match any
    existing connection. ONE/PYTHON_APP_PACKAGE_VERSION versions an installation update.
#>
[CmdletBinding()]
param(
    [string] $EnvironmentName = 'zavafinance',
    [ValidateSet('DotNet', 'Python')] [string] $Variant = 'DotNet',
    [Parameter(Mandatory)] [string] $SearchResourceId,
    [Alias('TokenExchangeResource')] [string] $UserAuthResource,
    [string] $DelegatedScopes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Invoke-AzJson([string[]] $Arguments) {
    $json = & az @Arguments --output json --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Azure command failed: $($Arguments[0])." }
    if ($json) {
        try { $json | ConvertFrom-Json -Depth 50 }
        catch { throw "Azure command returned invalid JSON: $($Arguments[0])." }
    }
}

function Invoke-VariantDeployment([string] $Group, [string] $Name, [string[]] $TemplateArguments) {
    # Bicep bootstrapping can emit non-JSON stdout. Verify the persisted ARM result separately.
    $result = & az deployment group create --subscription $subscription --resource-group $Group `
        --name $Name @TemplateArguments --output none --only-show-errors 2>&1
    $result = $null
    if ($LASTEXITCODE -ne 0) { throw "Variant deployment failed: $Name." }
    $state = Invoke-AzJson @('deployment', 'group', 'show', '--subscription', $subscription,
        '--resource-group', $Group, '--name', $Name, '--query', 'properties.provisioningState')
    if ($state -ne 'Succeeded') { throw "Variant deployment $Name did not succeed." }
}

$settingsJson = & azd env get-values --environment $EnvironmentName --cwd $root --output json 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Could not read azd configuration.' }
try { $settings = $settingsJson | ConvertFrom-Json -AsHashtable }
catch { throw 'Could not parse azd configuration.' }
finally { $settingsJson = $null }
try {
$serviceName = if ($Variant -eq 'Python') { 'zavafinance-one-python' } else { 'zavafinance-one' }
$channelPrefix = if ($Variant -eq 'Python') { 'PYTHON' } else { 'ONE' }
$packageVersion = $settings["${channelPrefix}_APP_PACKAGE_VERSION"]
if (-not [string]::IsNullOrWhiteSpace($packageVersion) -and (
    $packageVersion.Length -gt 256 -or
    $packageVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')) {
    throw "${channelPrefix}_APP_PACKAGE_VERSION must be a three-part numeric version."
}
$prefix = 'AGENT_' + $serviceName.Replace('-', '_').ToUpperInvariant()
foreach ($key in @(
    'AZURE_SUBSCRIPTION_ID', 'AZURE_RESOURCE_GROUP', 'AZURE_AI_PROJECT_ENDPOINT',
    "${prefix}_NAME", "${prefix}_BOT_NAME",
    "${prefix}_BOT_RESOURCE_GROUP", "${prefix}_INSTANCE_IDENTITY_CLIENT_ID",
    "${prefix}_INSTANCE_IDENTITY_PRINCIPAL_ID",
    'OBO_CLIENT_ID', 'OBO_TENANT_ID', 'OBO_AUDIENCE', 'RESOLVER_SEARCH_ENDPOINT'
)) {
    if ([string]::IsNullOrWhiteSpace($settings[$key])) { throw "Missing azd setting: $key." }
}
if ($settings["${prefix}_NAME"] -ne $serviceName) {
    throw 'The deployment does not identify the selected native Activity agent.'
}

$subscription = [guid]::Parse($settings.AZURE_SUBSCRIPTION_ID).ToString()
$clientId = [guid]::Parse($settings["${prefix}_INSTANCE_IDENTITY_CLIENT_ID"]).ToString()
$principalId = [guid]::Parse($settings["${prefix}_INSTANCE_IDENTITY_PRINCIPAL_ID"]).ToString()
$botName = $settings["${prefix}_BOT_NAME"]
$botGroup = $settings["${prefix}_BOT_RESOURCE_GROUP"]
$botId = "/subscriptions/$subscription/resourceGroups/$botGroup/providers/Microsoft.BotService/botServices/$botName"
$projectEndpoint = $settings.AZURE_AI_PROJECT_ENDPOINT.TrimEnd('/')
$expectedEndpoint = "$projectEndpoint/agents/$serviceName/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview"
$endpointUri = [uri]::new($projectEndpoint)
if ($endpointUri.Scheme -ne 'https' -or $endpointUri.Host -notmatch '^[a-z0-9-]+\.services\.ai\.azure\.com$' `
    -or $endpointUri.AbsolutePath -notmatch '^/api/projects/[^/]+$' `
    -or $endpointUri.Query -or $endpointUri.Fragment -or $endpointUri.UserInfo) {
    throw 'AZURE_AI_PROJECT_ENDPOINT must be a Foundry project HTTPS endpoint.'
}
$otherPrefix = if ($Variant -eq 'Python') { 'AGENT_ZAVAFINANCE_ONE' } else { 'AGENT_ZAVAFINANCE_ONE_PYTHON' }
if ($clientId -eq $settings.OBO_CLIENT_ID -or $clientId -eq $settings["${otherPrefix}_INSTANCE_IDENTITY_CLIENT_ID"] `
    -or ($botName -eq $settings["${otherPrefix}_BOT_NAME"] -and $botGroup -eq $settings["${otherPrefix}_BOT_RESOURCE_GROUP"])) {
    throw 'Each variant must have a distinct bot and bot identity, separate from the shared finance OAuth app.'
}
if ($SearchResourceId -notmatch "^/subscriptions/$subscription/resourceGroups/([^/]+)/providers/Microsoft.Search/searchServices/([^/]+)$") {
    throw 'SearchResourceId must identify the existing resolver Search service in the selected subscription.'
}
$searchGroup = $Matches[1]
$searchName = $Matches[2]
if ([uri]::new($settings.RESOLVER_SEARCH_ENDPOINT).Host -ne "$searchName.search.windows.net") {
    throw 'Search resource does not match the configured resolver endpoint.'
}

$safeBotQuery = '{id:id,endpoint:properties.endpoint,clientId:properties.msaAppId,tenant:properties.msaAppTenantId}'
$bot = Invoke-AzJson @('rest', '--method', 'get', '--url', "https://management.azure.com${botId}?api-version=2022-09-15", '--query', $safeBotQuery)
if ($bot.id -ne $botId -or $bot.endpoint -ne $expectedEndpoint -or $bot.clientId -ne $clientId `
    -or $bot.tenant -ne $settings.OBO_TENANT_ID) {
    throw 'Selected bot endpoint, identity or tenant does not agree with the deployment.'
}
$identity = Invoke-AzJson @('ad', 'sp', 'show', '--id', $clientId, '--query', '{clientId:appId,principalId:id}')
if ($identity.clientId -ne $clientId -or $identity.principalId -ne $principalId) {
    throw 'The runtime principal does not match the selected bot application identity.'
}
$safeOAuthQuery = "{clientId:properties.clientId,provider:properties.serviceProviderId,scopes:properties.scopes,tenant:properties.parameters[?key=='tenantId'].value,resource:properties.parameters[?key=='tokenExchangeUrl'].value}"
$connections = @(Invoke-AzJson @(
    'rest', '--method', 'get', '--url', "https://management.azure.com$botId/connections?api-version=2022-09-15",
    '--query', "value[?name=='mcs'].$safeOAuthQuery"
))
if ($connections.Count -gt 1) { throw 'The selected bot has an ambiguous mcs connection.' }
if (-not $PSBoundParameters.ContainsKey('UserAuthResource')) {
    $UserAuthResource = $settings["${channelPrefix}_USER_AUTH_RESOURCE"]
}
if (-not $PSBoundParameters.ContainsKey('DelegatedScopes')) {
    $DelegatedScopes = $settings["${channelPrefix}_DELEGATED_SCOPES"]
}
$createConnection = $connections.Count -eq 0
if ($createConnection) {
    if ([string]::IsNullOrWhiteSpace($UserAuthResource) -or [string]::IsNullOrWhiteSpace($DelegatedScopes) `
        -or [string]::IsNullOrWhiteSpace($settings.OBO_CLIENT_SECRET)) {
        throw "Missing mcs connection: explicitly supply UserAuthResource and DelegatedScopes (or ${channelPrefix}_USER_AUTH_RESOURCE and ${channelPrefix}_DELEGATED_SCOPES), and configure OBO_CLIENT_SECRET. No scopes are inferred."
    }
    $oauth = [pscustomobject]@{
        clientId = $settings.OBO_CLIENT_ID
        provider = '30dd229c-58e3-4a48-bdfd-91ec48eb906c'
        scopes = $DelegatedScopes
        tenant = @($settings.OBO_TENANT_ID)
        resource = @($UserAuthResource)
    }
}
else {
    $oauth = $connections[0]
    if ((-not [string]::IsNullOrWhiteSpace($DelegatedScopes) -and $DelegatedScopes -cne $oauth.scopes) `
        -or (-not [string]::IsNullOrWhiteSpace($UserAuthResource) -and $UserAuthResource -cne @($oauth.resource)[0])) {
        throw 'Explicit OAuth settings differ from the selected bot connection. Review that connection separately; this script never broadens or replaces its scopes/resource.'
    }
}
$resource = @($oauth.resource)
$tenant = @($oauth.tenant)
if ($oauth.clientId -ne $settings.OBO_CLIENT_ID -or $oauth.provider -ne '30dd229c-58e3-4a48-bdfd-91ec48eb906c' `
    -or $resource.Count -ne 1 -or $tenant.Count -ne 1 -or $tenant[0] -ne $settings.OBO_TENANT_ID `
    -or [string]::IsNullOrWhiteSpace($oauth.scopes)) {
    throw 'Existing OAuth configuration does not match the authorized finance application.'
}
$tokenExchangeResource = $resource[0]
$resourceUri = $null
if (-not [uri]::TryCreate($tokenExchangeResource, [UriKind]::Absolute, [ref] $resourceUri) `
    -or $resourceUri.Scheme -notin @('api', 'https') -or $resourceUri.Query -or $resourceUri.Fragment -or $resourceUri.UserInfo) {
    throw 'The OAuth resource must be an application ID URI without credentials, query or fragment.'
}
$registration = Invoke-AzJson @('ad', 'app', 'show', '--id', $oauth.clientId,
    '--query', '{clientId:appId,tokenVersion:api.requestedAccessTokenVersion,identifierUris:identifierUris,scopes:api.oauth2PermissionScopes[?isEnabled].value}')
if ($registration.clientId -ne $oauth.clientId -or $registration.tokenVersion -ne 2 `
    -or ($settings.OBO_AUDIENCE -ne $oauth.clientId -and $settings.OBO_AUDIENCE -cnotin $registration.identifierUris)) {
    throw 'The finance OAuth app must issue v2 tokens and match the configured OBO audience. Correct the shared app registration explicitly; this script does not modify it.'
}
if ($tokenExchangeResource -cnotin $registration.identifierUris -and $tokenExchangeResource -cne "api://botid-$clientId") {
    throw 'The token-exchange resource must identify the selected bot or an exposed URI of the finance OAuth app.'
}
if ($createConnection) {
    $requestedScopes = @($oauth.scopes -split '\s+' | Where-Object { $_ })
    $financeScopes = @(
        foreach ($identifierUri in @($registration.identifierUris)) {
            foreach ($scope in @($registration.scopes)) { "$identifierUri/$scope" }
        }
    )
    $allowedScopes = @('openid', 'profile', 'offline_access') + $financeScopes
    if (-not $requestedScopes.Count -or @($requestedScopes | Where-Object { $_ -cnotin $allowedScopes }).Count `
        -or -not @($requestedScopes | Where-Object { $_ -cin $financeScopes }).Count) {
        throw 'New mcs scopes must explicitly request an enabled scope on the authorized finance app resource. Wildcards, .default and other APIs are not accepted.'
    }
}

if ($createConnection) {
$parameters = @{
    ONE_BOT_NAME = $botName
    OBO_CLIENT_ID = $settings.OBO_CLIENT_ID
    OBO_CLIENT_SECRET = $settings.OBO_CLIENT_SECRET
    OBO_TENANT_ID = $settings.OBO_TENANT_ID
    ONE_TOKEN_EXCHANGE_RESOURCE = $tokenExchangeResource
    ONE_DELEGATED_SCOPES = $oauth.scopes
}
$previousEnvironment = @{}
try {
    foreach ($key in $parameters.Keys) {
        $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $parameters[$key], 'Process')
    }
    Invoke-VariantDeployment -Group $botGroup -Name "$serviceName-oauth" `
        -TemplateArguments @('--parameters', (Join-Path $root 'infra\oauth.bicepparam'))
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    $parameters.Clear()
    $settings.Remove('OBO_CLIENT_SECRET')
}
}

$configuredOAuth = Invoke-AzJson @('rest', '--method', 'get',
    '--url', "https://management.azure.com$botId/connections/mcs?api-version=2022-09-15", '--query', $safeOAuthQuery)
$configuredResource = @($configuredOAuth.resource)
$configuredTenant = @($configuredOAuth.tenant)
if ($configuredOAuth.clientId -ne $oauth.clientId -or $configuredOAuth.provider -ne $oauth.provider `
    -or $configuredOAuth.scopes -cne $oauth.scopes -or $configuredResource.Count -ne 1 `
    -or $configuredResource[0] -cne $tokenExchangeResource -or $configuredTenant.Count -ne 1 `
    -or $configuredTenant[0] -ne $tenant[0]) {
    throw 'The persisted OAuth connection does not match the authorized finance application.'
}

$accountName = [uri]::new($projectEndpoint).Host.Split('.')[0]
$accountGroup = $settings.AZURE_RESOURCE_GROUP
$accountId = "/subscriptions/$subscription/resourceGroups/$accountGroup/providers/Microsoft.CognitiveServices/accounts/$accountName"
foreach ($grant in @(
    @{ Scope = $SearchResourceId; Role = '1407120a-92aa-4202-b7e9-c0e197c71c8f'; Group = $searchGroup
       Template = 'runtime-search-access.bicep'; Parameters = @("searchServiceName=$searchName", "agentPrincipalId=$principalId") },
    @{ Scope = $accountId; Role = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'; Group = $accountGroup
       Template = 'model-access.bicep'; Parameters = @("modelAccountName=$accountName", "agentPrincipalId=$principalId",
           "publisherPrincipalId=$principalId", 'assignPublisherAccess=false') }
)) {
    $roles = @(Invoke-AzJson @('role', 'assignment', 'list', '--subscription', $subscription,
        '--assignee-object-id', $principalId, '--scope', $grant.Scope, '--include-inherited',
        '--query', '[].roleDefinitionId'))
    if (-not ($roles | Where-Object { $_.EndsWith("/$($grant.Role)", [StringComparison]::OrdinalIgnoreCase) })) {
        # The model template's publisher parameter is unused when assignPublisherAccess=false.
        $arguments = @('--template-file', (Join-Path $root "infra\$($grant.Template)"),
            '--parameters') + $grant.Parameters
        Invoke-VariantDeployment -Group $grant.Group `
            -Name "$serviceName-$($grant.Template.Replace('.bicep', ''))" -TemplateArguments $arguments
    }
    $confirmedRoles = @(Invoke-AzJson @('role', 'assignment', 'list', '--subscription', $subscription,
        '--assignee-object-id', $principalId, '--scope', $grant.Scope, '--include-inherited',
        '--query', '[].roleDefinitionId'))
    if (-not ($confirmedRoles | Where-Object { $_.EndsWith("/$($grant.Role)", [StringComparison]::OrdinalIgnoreCase) })) {
        throw "Required runtime role $($grant.Role) was not found at $($grant.Scope)."
    }
}
$tags = Invoke-AzJson @('tag', 'update', '--resource-id', $botId, '--operation', 'Merge',
    '--tags', 'purpose=demo', 'owner=gbelenky', '--query', 'properties.tags')
if ($tags.purpose -ne 'demo' -or $tags.owner -ne 'gbelenky') {
    throw 'Selected bot demo tags were not applied.'
}

$confirmed = Invoke-AzJson @('rest', '--method', 'get',
    '--url', "https://management.azure.com${botId}?api-version=2022-09-15", '--query', $safeBotQuery)
if ($confirmed.endpoint -ne $bot.endpoint -or $confirmed.clientId -ne $bot.clientId -or $confirmed.tenant -ne $bot.tenant) {
    throw 'Selected bot endpoint or identity changed during configuration; inspect it before proceeding.'
}

$packageArguments = @{
    Variant = $Variant
    BotId = $clientId
    AppHostName = ([uri]::new($projectEndpoint).Host)
    UserAuthAppId = $oauth.clientId
    UserAuthResource = $tokenExchangeResource
}
if (-not [string]::IsNullOrWhiteSpace($packageVersion)) {
    $packageArguments.AppVersion = $packageVersion
}
& (Join-Path $PSScriptRoot 'build-app-package.ps1') @packageArguments
Write-Host "Configured $Variant bot $botName. Existing mcs scopes are preserved; no other bot was modified."
}
finally {
    $settings.Clear()
}
