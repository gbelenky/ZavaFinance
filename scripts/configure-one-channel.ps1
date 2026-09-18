#requires -Version 7.2
<#
.SYNOPSIS
    Configures only the separately deployed One bot, runtime permissions and branded package.
.DESCRIPTION
    Requires activity-protocol and a successful targeted zavafinance-one deployment,
    or zavafinance-one-python with -Variant Python.
    Reads the existing bot's mcs connection without secrets. Reuses the authorized
    OBO application from azd; it does not modify that application or the original bot.
    Secure Bicep parameters are supplied through process environment variables.
    ONE/PYTHON_USER_AUTH_RESOURCE optionally selects an already-exposed URI on the
    same finance app. ONE/PYTHON_APP_PACKAGE_VERSION versions an installation update.
#>
[CmdletBinding()]
param(
    [string] $EnvironmentName = 'zavafinance',
    [ValidateSet('One', 'Python')] [string] $Variant = 'One',
    [Parameter(Mandatory)] [string] $OriginalBotResourceId,
    [Parameter(Mandatory)] [string] $SearchResourceId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ((& git -C $root branch --show-current) -ne 'activity-protocol') {
    throw 'One configuration is restricted to the activity-protocol branch.'
}

function Invoke-AzJson([string[]] $Arguments) {
    $json = & az @Arguments --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure command failed: $($Arguments[0])." }
    if ($json) { $json | ConvertFrom-Json -Depth 50 }
}

function Invoke-OneDeployment([string] $Group, [string] $Name, [string[]] $TemplateArguments) {
    # Bicep bootstrapping can emit non-JSON stdout. Verify the persisted ARM result separately.
    & az deployment group create --subscription $subscription --resource-group $Group `
        --name $Name @TemplateArguments --output none --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "One deployment failed: $Name." }
    $state = Invoke-AzJson @('deployment', 'group', 'show', '--subscription', $subscription,
        '--resource-group', $Group, '--name', $Name, '--query', 'properties.provisioningState')
    if ($state -ne 'Succeeded') { throw "One deployment $Name did not succeed: $state." }
}

$settingsJson = & azd env get-values --environment $EnvironmentName --cwd $root --output json
if ($LASTEXITCODE -ne 0) { throw 'Could not read azd configuration.' }
$settings = $settingsJson | ConvertFrom-Json -AsHashtable
$settingsJson = $null
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
    'OBO_CLIENT_ID', 'OBO_CLIENT_SECRET', 'OBO_TENANT_ID', 'RESOLVER_SEARCH_ENDPOINT'
)) {
    if ([string]::IsNullOrWhiteSpace($settings[$key])) { throw "Missing azd setting: $key." }
}
if ($settings["${prefix}_NAME"] -ne $serviceName) {
    throw 'The deployment is not the separate One agent.'
}

$subscription = [guid]::Parse($settings.AZURE_SUBSCRIPTION_ID).ToString()
$clientId = [guid]::Parse($settings["${prefix}_INSTANCE_IDENTITY_CLIENT_ID"]).ToString()
$principalId = [guid]::Parse($settings["${prefix}_INSTANCE_IDENTITY_PRINCIPAL_ID"]).ToString()
$botName = $settings["${prefix}_BOT_NAME"]
$botGroup = $settings["${prefix}_BOT_RESOURCE_GROUP"]
$botId = "/subscriptions/$subscription/resourceGroups/$botGroup/providers/Microsoft.BotService/botServices/$botName"
$projectEndpoint = $settings.AZURE_AI_PROJECT_ENDPOINT.TrimEnd('/')
$expectedEndpoint = "$projectEndpoint/agents/$serviceName/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview"
if ($botId -eq $OriginalBotResourceId -or $clientId -eq $settings.OBO_CLIENT_ID) {
    throw 'One must use a distinct bot resource and bot application identity.'
}
if ($OriginalBotResourceId -notmatch "^/subscriptions/$subscription/resourceGroups/[^/]+/providers/Microsoft.BotService/botServices/[^/]+$") {
    throw 'OriginalBotResourceId must identify the existing source bot in the selected subscription.'
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
$original = Invoke-AzJson @('rest', '--method', 'get', '--url', "https://management.azure.com${OriginalBotResourceId}?api-version=2022-09-15", '--query', $safeBotQuery)
if ($bot.endpoint -ne $expectedEndpoint -or $bot.clientId -ne $clientId -or $bot.tenant -ne $settings.OBO_TENANT_ID `
    -or $bot.clientId -eq $original.clientId) {
    throw 'New bot endpoint, identity or tenant does not agree with the One deployment.'
}
$safeOAuthQuery = "{clientId:properties.clientId,provider:properties.serviceProviderId,scopes:properties.scopes,tenant:properties.parameters[?key=='tenantId'].value,resource:properties.parameters[?key=='tokenExchangeUrl'].value}"
$oauth = Invoke-AzJson @(
    'rest', '--method', 'get', '--url', "https://management.azure.com$OriginalBotResourceId/connections/mcs?api-version=2022-09-15",
    '--query', $safeOAuthQuery
)
$resource = @($oauth.resource)
$tenant = @($oauth.tenant)
if ($oauth.clientId -ne $settings.OBO_CLIENT_ID -or $oauth.provider -ne '30dd229c-58e3-4a48-bdfd-91ec48eb906c' `
    -or $resource.Count -ne 1 -or $tenant.Count -ne 1 -or $tenant[0] -ne $settings.OBO_TENANT_ID `
    -or [string]::IsNullOrWhiteSpace($oauth.scopes)) {
    throw 'Existing OAuth configuration does not match the authorized finance application.'
}
$tokenExchangeResource = $resource[0]
if (-not [string]::IsNullOrWhiteSpace($settings["${channelPrefix}_USER_AUTH_RESOURCE"])) {
    $tokenExchangeResource = $settings["${channelPrefix}_USER_AUTH_RESOURCE"]
}
$registration = Invoke-AzJson @('ad', 'app', 'show', '--id', $oauth.clientId,
    '--query', '{clientId:appId,tokenVersion:api.requestedAccessTokenVersion,identifierUris:identifierUris}')
if ($registration.clientId -ne $oauth.clientId -or $registration.tokenVersion -ne 2 `
    -or $tokenExchangeResource -notin $registration.identifierUris) {
    throw 'Teams SSO requires the finance OAuth app to issue v2 tokens and expose the connection resource URI. Correct the shared app registration explicitly; this script does not modify it.'
}

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
    Invoke-OneDeployment -Group $botGroup -Name "$serviceName-oauth" `
        -TemplateArguments @('--parameters', (Join-Path $root 'infra\one-oauth.bicepparam'))
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
    $parameters.Clear()
    $settings.Remove('OBO_CLIENT_SECRET')
}

$configuredOAuth = Invoke-AzJson @('rest', '--method', 'get',
    '--url', "https://management.azure.com$botId/connections/mcs?api-version=2022-09-15", '--query', $safeOAuthQuery)
$configuredResource = @($configuredOAuth.resource)
$configuredTenant = @($configuredOAuth.tenant)
if ($configuredOAuth.clientId -ne $oauth.clientId -or $configuredOAuth.provider -ne $oauth.provider `
    -or $configuredOAuth.scopes -ne $oauth.scopes -or $configuredResource.Count -ne 1 `
    -or $configuredResource[0] -ne $tokenExchangeResource -or $configuredTenant.Count -ne 1 `
    -or $configuredTenant[0] -ne $tenant[0]) {
    throw 'The persisted One OAuth connection does not match the authorized finance application.'
}

$accountName = [uri]::new($projectEndpoint).Host.Split('.')[0]
$accountGroup = $settings.AZURE_RESOURCE_GROUP
$accountId = "/subscriptions/$subscription/resourceGroups/$accountGroup/providers/Microsoft.CognitiveServices/accounts/$accountName"
foreach ($grant in @(
    @{ Scope = $SearchResourceId; Role = '1407120a-92aa-4202-b7e9-c0e197c71c8f'; Group = $searchGroup
       Template = 'one-search-access.bicep'; Parameters = @("searchServiceName=$searchName", "agentPrincipalId=$principalId") },
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
        Invoke-OneDeployment -Group $grant.Group `
            -Name "$serviceName-$($grant.Template.Replace('.bicep', ''))" -TemplateArguments $arguments
    }
    $confirmedRoles = @(Invoke-AzJson @('role', 'assignment', 'list', '--subscription', $subscription,
        '--assignee-object-id', $principalId, '--scope', $grant.Scope, '--include-inherited',
        '--query', '[].roleDefinitionId'))
    if (-not ($confirmedRoles | Where-Object { $_.EndsWith("/$($grant.Role)", [StringComparison]::OrdinalIgnoreCase) })) {
        throw "Required One runtime role $($grant.Role) was not found at $($grant.Scope)."
    }
}
$tags = Invoke-AzJson @('tag', 'update', '--resource-id', $botId, '--operation', 'Merge',
    '--tags', 'purpose=demo', 'owner=gbelenky', '--query', 'properties.tags')
if ($tags.purpose -ne 'demo' -or $tags.owner -ne 'gbelenky') {
    throw 'One Bot demo tags were not applied.'
}

$confirmed = Invoke-AzJson @('rest', '--method', 'get',
    '--url', "https://management.azure.com${OriginalBotResourceId}?api-version=2022-09-15", '--query', $safeBotQuery)
if ($confirmed.endpoint -ne $original.endpoint -or $confirmed.clientId -ne $original.clientId) {
    throw 'The original bot changed during configuration; inspect it before proceeding.'
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
Write-Host "Configured One bot $botName. Original bot endpoint and identity are unchanged."
