#requires -Version 7.2
<#
.SYNOPSIS
    Validates and deploys only the Python native Activity finance agent.
.DESCRIPTION
    Uses the existing Foundry project and finance configuration in the chosen azd
    environment. Creates a separate session salt once, never rotates an existing
    one. Does not modify the .NET variant or resume Fabric.
#>
[CmdletBinding()]
param(
    [string] $EnvironmentName = 'zavafinance',
    [Parameter(Mandatory)] [string] $SearchResourceId,
    [Alias('TokenExchangeResource')] [string] $UserAuthResource,
    [string] $DelegatedScopes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$service = Join-Path $root 'src\python'
$python = Join-Path $service '.venv\Scripts\python.exe'
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

. (Join-Path $PSScriptRoot 'Agent-Deployment.ps1')
Initialize-AgentEnvironment -Root $root -EnvironmentName $EnvironmentName -Prefix PYTHON

& azd deploy zavafinance-one-python --environment $EnvironmentName --cwd $root --no-prompt
if ($LASTEXITCODE -ne 0) { throw 'Python hosted-agent deployment failed.' }

$channel = @{ Variant = 'Python'; EnvironmentName = $EnvironmentName; SearchResourceId = $SearchResourceId }
foreach ($name in @('UserAuthResource', 'DelegatedScopes')) {
    if ($PSBoundParameters.ContainsKey($name)) { $channel[$name] = $PSBoundParameters[$name] }
}
& (Join-Path $PSScriptRoot 'configure-channel.ps1') @channel
Write-Host 'Python agent deployed and its separate channel configured. Interactive acceptance is a separate step.'
