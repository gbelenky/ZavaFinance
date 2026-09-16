#Requires -Version 7.0
function Invoke-ResolverSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Query,
        [hashtable]$Parameters = @{},
        [string]$SqlClientDirectory = (Join-Path $PSScriptRoot '..\tests\ZavaFinance.Tests\bin\Release\net10.0')
    )
    $ErrorActionPreference = 'Stop'
    $assembly = Join-Path $SqlClientDirectory 'runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll'
    if (!(Test-Path -LiteralPath $assembly)) {
        throw "Build tests\ZavaFinance.Tests in Release first, or supply SqlClientDirectory containing its dependencies."
    }
    [AppContext]::SetSwitch('Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows', $true)
    foreach ($name in @('Microsoft.Extensions.Logging.Abstractions.dll', 'Microsoft.Data.SqlClient.Extensions.Abstractions.dll',
                         'Microsoft.IdentityModel.Abstractions.dll', 'Microsoft.Identity.Client.dll', 'Microsoft.SqlServer.Server.dll')) {
        $path = Join-Path $SqlClientDirectory $name
        if (Test-Path -LiteralPath $path) { [void][Reflection.Assembly]::LoadFrom($path) }
    }
    [void][Reflection.Assembly]::LoadFrom($assembly)
    $token = az account get-access-token --resource 'https://database.windows.net/' --query accessToken --output tsv
    if ($LASTEXITCODE -ne 0 -or !$token) { throw 'Azure CLI could not acquire the delegated SQL token.' }
    $builder = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = "tcp:$Server,1433"
    $builder['Initial Catalog'] = $Database
    $builder['Encrypt'] = 'True'
    $builder['Trust Server Certificate'] = $false
    $builder['Connect Timeout'] = 30
    $builder['Application Name'] = 'ZavaResolverPublisher'
    $connection = [Microsoft.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.AccessToken = $token.Trim()
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $Query
        foreach ($name in $Parameters.Keys) {
            $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::VarChar, 200)
            $parameter.Value = $Parameters[$name]
        }
        try {
            $reader = $command.ExecuteReader()
            try {
                $table = [System.Data.DataTable]::new()
                $table.Load($reader)
                return ,$table
            } finally { $reader.Dispose() }
        } finally { $command.Dispose() }
    } finally {
        $connection.Dispose()
        $token = $null
    }
}
