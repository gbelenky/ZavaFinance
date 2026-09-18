#requires -Version 7.2
<#
.SYNOPSIS
    Validates and deploys only the standalone Python Activity agent.
.DESCRIPTION
    Uses the existing Foundry project and finance configuration in the chosen azd
    environment. Creates a separate session salt once, never rotates an existing
    one. Does not provision the original Channel or resume Fabric.
#>
[CmdletBinding()]
param(
    [string] $EnvironmentName = 'zavafinance',
    [Parameter(Mandatory)] [string] $OriginalBotResourceId,
    [Parameter(Mandatory)] [string] $SearchResourceId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$service = Join-Path $root 'src\ZavaFinance.One.Python'
$python = Join-Path $service '.venv\Scripts\python.exe'
if ((& git -C $root branch --show-current) -ne 'activity-protocol') {
    throw 'Python One deployment is restricted to the activity-protocol branch.'
}
if (-not (Test-Path $python -PathType Leaf)) {
    throw 'Create the Python agent virtual environment and install requirements-dev.txt first.'
}

Push-Location $service
try {
    & $python -m pip check
    if ($LASTEXITCODE -ne 0) { throw 'Python dependencies are inconsistent.' }
    & $python -m unittest discover -s tests
    if ($LASTEXITCODE -ne 0) { throw 'Python parity tests failed; deployment stopped.' }
}
finally {
    Pop-Location
}

$settingsText = & azd env get-values --environment $EnvironmentName --cwd $root --output json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the selected azd environment.' }
$settings = $settingsText | ConvertFrom-Json -AsHashtable
$settingsText = $null
if ([string]::IsNullOrWhiteSpace($settings['AZURE_AI_PROJECT_ENDPOINT'])) {
    throw 'Select the existing Foundry project before deployment.'
}
if ([string]::IsNullOrWhiteSpace($settings['PYTHON_SESSION_KEY_SALT'])) {
    $salt = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $result = & azd env set PYTHON_SESSION_KEY_SALT $salt --environment $EnvironmentName --cwd $root 2>&1
    $salt = $null
    $result = $null
    if ($LASTEXITCODE -ne 0) { throw 'Could not persist the separate Python session salt.' }
}
$settings.Clear()

& azd deploy zavafinance-one-python --environment $EnvironmentName --cwd $root --no-prompt
if ($LASTEXITCODE -ne 0) { throw 'Python hosted-agent deployment failed.' }

& (Join-Path $PSScriptRoot 'configure-one-channel.ps1') -Variant Python `
    -EnvironmentName $EnvironmentName -OriginalBotResourceId $OriginalBotResourceId `
    -SearchResourceId $SearchResourceId
Write-Host 'Python agent deployed and its separate channel configured. Interactive acceptance is a separate step.'
