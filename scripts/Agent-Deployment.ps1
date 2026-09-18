#requires -Version 7.2

function Initialize-AgentEnvironment {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $EnvironmentName,
        [Parameter(Mandatory)] [ValidateSet('ONE', 'PYTHON')] [string] $Prefix
    )

    $json = & azd env get-values --environment $EnvironmentName --cwd $Root --output json 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the selected azd environment.' }
    try { $settings = $json | ConvertFrom-Json -AsHashtable }
    catch { throw 'Cannot parse the selected azd environment.' }
    finally { $json = $null }
    try {
        foreach ($name in @(
            'AZURE_SUBSCRIPTION_ID', 'AZURE_RESOURCE_GROUP', 'AZURE_AI_PROJECT_ENDPOINT',
            'OBO_CLIENT_ID', 'OBO_TENANT_ID', 'OBO_AUDIENCE', 'OBO_CLIENT_SECRET',
            'COPILOT_STUDIO_ENVIRONMENT_ID', 'COPILOT_STUDIO_SCHEMA_NAME',
            'FABRIC_SQL_ENDPOINT', 'FABRIC_DATABASE', 'FABRIC_WORKSPACE_ID',
            'FABRIC_DATA_AGENT_ID', 'RESOLVER_SEARCH_ENDPOINT', 'RESOLVER_EMBEDDING_ENDPOINT'
        )) {
            if ([string]::IsNullOrWhiteSpace($settings[$name])) { throw "Missing azd setting: $name." }
        }
        $saltName = "${Prefix}_SESSION_KEY_SALT"
        if ([string]::IsNullOrWhiteSpace($settings[$saltName])) {
            $salt = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            try {
                $result = & azd env set $saltName $salt --environment $EnvironmentName --cwd $Root 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Could not persist $saltName." }
            }
            finally {
                $salt = $null
                $result = $null
            }
        }
    }
    finally {
        $settings.Clear()
    }
}
