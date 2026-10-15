#Requires -Version 7.6
param(
    [string]$LiveServer,
    [string]$LiveDatabase,
    [string]$LiveSqlClientDirectory = (Join-Path $PSScriptRoot '..\src\dotnet\tests\ZavaFinance.Tests\bin\Release\net10.0')
)
$ErrorActionPreference = 'Stop'
$liveRequested = $PSBoundParameters.ContainsKey('LiveServer') -or $PSBoundParameters.ContainsKey('LiveDatabase') -or $PSBoundParameters.ContainsKey('LiveSqlClientDirectory')
if ($liveRequested -and ([string]::IsNullOrWhiteSpace($LiveServer) -or [string]::IsNullOrWhiteSpace($LiveDatabase) -or [string]::IsNullOrWhiteSpace($LiveSqlClientDirectory))) {
    throw 'Live SQL checks require nonempty LiveServer, LiveDatabase and LiveSqlClientDirectory values.'
}
. (Join-Path $PSScriptRoot '..\scripts\Fabric-EstateSql.ps1')
$script:assertions = 0

function Assert-True($Condition, [string]$Message) {
    $script:assertions++
    if (!$Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $script:assertions++
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -like $Pattern) { return }
        throw "Expected '$Pattern'; got '$($_.Exception.Message)'"
    }
    throw "Expected failure: $Pattern"
}
function Copy-Snapshot($Snapshot) {
    ConvertFrom-Json -AsHashtable -DateKind String -InputObject (ConvertTo-Json -Depth 30 -InputObject $Snapshot)
}
function New-Column($Table, $Name, $Type = 'varchar', $Scale = '0', $Schema = 'dbo') {
    [ordered]@{schemaName=$Schema;tableName=$Table;columnName=$Name;ordinal='1';type=$Type;maxLength='100';precision='38';scale=$Scale;nullable='0'}
}
function Reset-Fixture {
    $script:columns = @()
    $script:tableNames = @()
    $script:financial = @()
    $script:objects = @()
    $script:releases = @()
    $script:rowCount = '1'
    $script:failQuery = ''
    $script:viewDefinition = '1'
    $script:badFloatFormat = $false
    $script:glPeriodMismatch = $false
    $script:expectedSqlClientDirectory = 'mock'
    $script:queries = [Collections.Generic.List[string]]::new()
}
# The only database boundary is replaced before any snapshot call; no token or connection is used.
function Invoke-ResolverSql {
    param($Server, $Database, $Query, $Parameters, $SqlClientDirectory)
    if ($Server -cne 'mock' -or $Database -cne 'mock' -or
        [IO.Path]::GetFullPath($SqlClientDirectory) -cne [IO.Path]::GetFullPath($script:expectedSqlClientDirectory)) {
        throw 'Connection arguments not forwarded'
    }
    $script:queries.Add($Query)
    if ($script:failQuery -and $Query.Contains($script:failQuery)) { throw 'Inventory access denied' }
    $rows = @(switch -Regex ($Query) {
        'fabric-estate:preconditions' { @(@{canViewDefinition=$script:viewDefinition}); break }
        'fabric-estate:columns' { $script:columns; break }
        'fabric-estate:tables' { $script:tableNames; break }
        'fabric-estate:float-format' {
            @([ordered]@{one=$(if ($script:badFloatFormat) {'unsupported'} else {'3FF0000000000000'});negative='BFF8000000000000';fraction='3FB999999999999A'})
            break
        }
        'fabric-estate:count' { @(@{rowCount=$script:rowCount}); break }
        'fabric-estate:gl-period' { if ($script:glPeriodMismatch) { @(@{mismatch='1'}) }; break }
        'fabric-estate:financial' { $script:financial; break }
        'fabric-estate:release' { $script:releases; break }
        'fabric-estate:objects' { $script:objects; break }
        'fabric-estate:(permissions|principals|policies)' { @(); break }
        default { throw "Unexpected SQL: $Query" }
    })
    $result = [Data.DataTable]::new()
    if (@($rows).Count) {
        foreach ($key in $rows[0].Keys) { [void]$result.Columns.Add($key, [string]) }
        foreach ($item in $rows) {
            $row = $result.NewRow()
            foreach ($key in $item.Keys) { $row[$key] = if ($null -eq $item[$key]) { [DBNull]::Value } else { $item[$key] } }
            $result.Rows.Add($row)
        }
    }
    return ,$result
}
function Get-Snapshot { Get-FabricEstateSqlSnapshot -Server 'mock' -Database 'mock' -SqlClientDirectory 'mock' @args }

$mockDefinition = (Get-Command Invoke-ResolverSql).Definition
. (Join-Path $PSScriptRoot '..\scripts\Fabric-EstateSql.ps1')
Assert-True ((Get-Command Invoke-ResolverSql).Definition -ceq $mockDefinition) 'Reloading the component must not dot-source the SQL helper again'

Reset-Fixture
$empty = Get-Snapshot
$json = ConvertTo-Json -Depth 30 -InputObject $empty
$roundtrip = ConvertFrom-Json -AsHashtable -InputObject $json
foreach ($section in 'columns','tables','financial','resolverRelease','sqlObjects','permissions','principals','policies') {
    Assert-True ($roundtrip[$section] -is [array] -and $roundtrip[$section].Count -eq 0) "Empty $section must serialize as []"
}
$differences = Compare-FabricEstateSqlSnapshot -Expected $empty -Actual $roundtrip
Assert-True ($differences -is [array] -and $differences.Count -eq 0) 'Equal empty snapshots must return []'
Assert-Throws { Get-Snapshot -RequireZavaContract } '*Missing required Zava table*'
$script:expectedSqlClientDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\dotnet\tests\ZavaFinance.Tests\bin\Release\net10.0'
foreach ($directory in @('', '  ', $null)) {
    $generic = Get-FabricEstateSqlSnapshot -Server mock -Database mock -SqlClientDirectory $directory -RequireZavaContract:$false
    Assert-True ($generic.sqlObjects -is [array] -and $generic.sqlObjects.Count -eq 0) 'Empty dependency path and boolean false must support generic SQL snapshots'
}
$script:expectedSqlClientDirectory = 'mock'
Assert-Throws { Get-FabricEstateSqlSnapshot -Server mock -Database mock -SqlClientDirectory mock -RequireZavaContract:$true } '*Missing required Zava table*'

$script:tableNames = @([ordered]@{schemaName='odd]schema';tableName="odd]table'";})
$script:columns = @(New-Column "odd]table'" 'id' 'int' '0' 'odd]schema')
$script:rowCount = '9007199254740993'
$single = Get-Snapshot
Assert-True ($single.tables -is [array] -and $single.tables.Count -eq 1) 'Singleton table must remain an array'
Assert-True ($single.tables[0].rowCount -ceq '9007199254740993') 'Count must retain exact string'
Assert-True (($script:queries -join "`n").Contains("[odd]]schema].[odd]]table']")) 'SQL identifiers must double closing brackets'
Assert-True (@($script:queries | Where-Object { $_.Contains('fabric-estate:count') -and $_.Contains('AS [rowCount]') }).Count -eq 1) 'Table count query must delimit the reserved ROWCOUNT alias'
$changed = Copy-Snapshot $single
$changed.tables[0].rowCount = '9007199254740992'
$differences = Compare-FabricEstateSqlSnapshot $single $changed
Assert-True ($differences -is [array] -and $differences.Count -eq 1) 'Count drift must produce a singleton array'
Assert-True ((ConvertTo-Json -InputObject $differences -Compress).StartsWith('[')) 'Singleton difference JSON must be an array'
$changed = Copy-Snapshot $single
$changed.columns[0].scale = '1'
Assert-True ((Compare-FabricEstateSqlSnapshot $single $changed).Count -eq 1) 'Schema drift must fail comparison'
$changed = Copy-Snapshot $single
$changed.columns[0].columnName = 'ID'
Assert-True ((Compare-FabricEstateSqlSnapshot $single $changed).Count -eq 1) 'Comparison must be case-sensitive'
$changed = Copy-Snapshot $single
$changed['extra'] = 'not ignored'
Assert-True ((Compare-FabricEstateSqlSnapshot $single $changed).Count -eq 1) 'Additional properties must not be ignored'
Assert-Throws { Compare-FabricEstateSqlSnapshot @{} @{} } '*Missing snapshot section*'

Reset-Fixture
$script:tableNames = @([ordered]@{schemaName='dbo';tableName='fact_finance_monthly'})
$script:columns = @(
    New-Column 'fact_finance_monthly' 'month_start' 'date'
    New-Column 'fact_finance_monthly' 'region_code'
    New-Column 'fact_finance_monthly' 'department_code'
    New-Column 'fact_finance_monthly' 'kpi_component'
    New-Column 'fact_finance_monthly' 'amount_usd' 'decimal' '8'
)
$script:rowCount = '3'
$script:financial = @([ordered]@{monthStart='2025-11-01';regionCode='EMEA';departmentCode='IT';keyValue='COGS';sum='123456789012345678901234567890.12345678';rowCount='3';nonNullCount='3'})
$source = Get-Snapshot
Assert-True ($source.financial -is [array] -and $source.financial.Count -eq 1) 'Singleton financial profile must remain an array'
Assert-True (($script:queries -join "`n") -match 'SUM\(CAST\(\[amount_usd\] AS decimal\(38,8\)\)\)') 'Financial SUM must preserve declared scale'
Assert-True (@($script:queries | Where-Object { $_.Contains('fabric-estate:financial') -and $_.Contains('AS [rowCount]') }).Count -eq 1) 'Financial query must delimit the reserved ROWCOUNT alias'
$changed = Copy-Snapshot $source
$changed.financial[0].sum = '123456789012345678901234567890.12345679'
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed).Count -eq 1) 'Financial differences below double precision must be detected'
$changed = Copy-Snapshot $source
$changed.financial[0].sum = '123456789012345678901234567890.123456780'
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed).Count -eq 1) 'Decimal scale strings must compare exactly'
$script:financial = @()
Assert-Throws { Get-Snapshot } '*Financial groups do not reconcile*'
$script:columns[0].type = 'varchar'
Assert-Throws { Get-Snapshot } '*must be a SQL date/time type*'
$script:columns[0].type = 'date'
$script:columns[1].type = 'int'
Assert-Throws { Get-Snapshot } '*must be a SQL character type*'
$script:columns[1].type = 'varchar'
$script:columns[-1].scale = '8);DROP'
Assert-Throws { Get-Snapshot } '*Invalid financial scale*'
$script:columns[-1].scale = '8'
$script:columns[-1].type = 'varchar'
Assert-Throws { Get-Snapshot } '*supported numeric*'
$script:columns = @($script:columns | Where-Object columnName -ne 'amount_usd')
Assert-Throws { Get-Snapshot } '*Missing financial column*'

Reset-Fixture
$script:tableNames = @([ordered]@{schemaName='dbo';tableName='fact_finance_monthly'})
$script:columns = @(
    New-Column 'fact_finance_monthly' 'month_start' 'date'
    New-Column 'fact_finance_monthly' 'region_code'
    New-Column 'fact_finance_monthly' 'department_code'
    New-Column 'fact_finance_monthly' 'kpi_component'
    New-Column 'fact_finance_monthly' 'amount_usd' 'float'
)
$script:rowCount = '3'
$script:financial = @([ordered]@{monthStart='2025-11-01';regionCode='EMEA';departmentCode='IT';keyValue='COGS';binaryParts='1019:7205759403792794;1020:7205759403792794;:';rowCount='3';nonNullCount='2'})
$floating = Get-Snapshot
$exactFractionSum = '0.3000000000000000166533453693773481063544750213623046875'
Assert-True ($floating.financial[0].sum -ceq $exactFractionSum) 'Float totals must represent the exact stored 0.1 + 0.2, without decimal rounding'
Assert-True ($floating.financial[0].rowCount -ceq '3' -and $floating.financial[0].nonNullCount -ceq '2') 'Float reconciliation must retain total and non-null counts'
Assert-True (!$floating.financial[0].Contains('binaryParts')) 'Internal exponent groups must not leak into the snapshot contract'
$floatQuery = @($script:queries | Where-Object { $_.Contains('fabric-estate:financial-float') })[0]
Assert-True ($floatQuery.Contains('STRING_AGG') -and $floatQuery.Contains('SUM(CAST(estate_float.significand AS decimal(38,0)))')) 'SQL must reduce exponent parts to one row per financial group and sum integer significands'
Assert-True ($floatQuery -notmatch 'CAST\(\[amount_usd\] AS decimal' -and $floatQuery.Contains('AS [rowCount]')) 'Float measures must not be rounded to a chosen decimal scale'
$script:financial[0].binaryParts = ':;1020:7205759403792794;1019:7205759403792794'
Assert-True ((Compare-FabricEstateSqlSnapshot $floating (Get-Snapshot)).Count -eq 0) 'Exponent aggregation order must not affect reconciliation'
$script:columns[-1].type = 'real'
Assert-True ((Get-Snapshot).financial[0].sum -ceq $exactFractionSum) 'Real measures must use the same exact widened-binary reconciliation path'
$script:badFloatFormat = $true
Assert-Throws { Get-Snapshot } '*IEEE 754*'
Assert-True ((ConvertTo-FabricEstateFloatSum '1076:0;1023:4503599627370496' '3') -ceq '1') 'Cancellation must preserve a small residual beside large float values'
Assert-True ((ConvertTo-FabricEstateFloatSum '1022:-4503599627370496' '1') -ceq '-0.5') 'Negative exact sums must preserve their sign'
Assert-True ((ConvertTo-FabricEstateFloatSum '0:0' '1') -ceq '0') 'A non-null zero must remain zero'
Assert-True ($null -eq (ConvertTo-FabricEstateFloatSum ':' '0')) 'All-null groups must remain null, not zero'
$subnormal = ConvertTo-FabricEstateFloatSum '0:1' '1'
Assert-True ($subnormal.Length -eq 1076 -and [BitConverter]::DoubleToInt64Bits([double]::Parse($subnormal, [Globalization.CultureInfo]::InvariantCulture)) -eq 1) 'The smallest binary64 subnormal must retain its exact decimal expansion'
$maximum = [Numerics.BigInteger]::Pow(2,1024) - [Numerics.BigInteger]::Pow(2,971)
Assert-True ((ConvertTo-FabricEstateFloatSum '2046:9007199254740991' '1') -ceq $maximum.ToString([Globalization.CultureInfo]::InvariantCulture)) 'The largest finite binary64 value must not overflow decimal or double arithmetic'
$significands = [Numerics.BigInteger]::Parse('7205759403792794') * [Numerics.BigInteger]::Parse('9007199254740993')
Assert-True ((ConvertTo-FabricEstateFloatSum "1019:$significands" '9007199254740993') -ceq '900719925474099.3500000000000000055511151231257827021181583404541015625') 'Counts above double precision must not lose low-order bits'
foreach ($invalid in '2047:1','-1:1','1023:1.5','1023:1;1023:2','',':;:') {
    Assert-Throws { ConvertTo-FabricEstateFloatSum $invalid '1' } '*Invalid floating-point*'
}
Assert-Throws { ConvertTo-FabricEstateFloatSum ':' '1' } '*Invalid floating-point*'
Assert-Throws { ConvertTo-FabricEstateFloatSum '1023:1' '0' } '*Invalid floating-point*'

Reset-Fixture
$script:tableNames = @([ordered]@{schemaName='dbo';tableName='resolver_release'})
$script:columns = @('catalog_version','search_index','optional_value' | ForEach-Object { New-Column 'resolver_release' $_ })
$script:releases = @([ordered]@{catalog_version='v1';search_index='source';optional_value=$null})
$source = Get-Snapshot
Assert-True ($null -eq $source.resolverRelease[0].values.optional_value) 'SQL NULL must serialize as JSON null'
$changed = Copy-Snapshot $source
$changed.resolverRelease[0].values.search_index = 'target'
$changed.tables[0].rowCount = '2'
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed).Count -eq 2) 'Release must match before activation'
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed -IgnoreResolverRelease).Count -eq 0) 'Explicit post-activation release override'
$changed.columns[0].type = 'nvarchar'
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed -IgnoreResolverRelease).Count -eq 1) 'Release override must not ignore schema'
$source.resolverRelease[0].values['activated_at'] = '2026-09-30T19:00:00.1234567'
$roundtripObject = ConvertFrom-Json -DateKind String -InputObject (ConvertTo-Json -Depth 30 -InputObject $source)
Assert-True ((Compare-FabricEstateSqlSnapshot $source $roundtripObject).Count -eq 0) 'JSON object roundtrip must preserve exact timestamp strings'

Reset-Fixture
$script:failQuery = 'fabric-estate:objects'
Assert-Throws { Get-Snapshot } '*Inventory access denied*'
$script:failQuery = 'fabric-estate:permissions'
Assert-Throws { Get-Snapshot } '*Inventory access denied*'
$script:failQuery = ''
$script:viewDefinition = '0'
Assert-Throws { Get-Snapshot } '*VIEW DEFINITION*'
$script:viewDefinition = '1'
$script:objects = @([ordered]@{schemaName='dbo';objectName='restricted';objectType='VIEW';definition=$null})
Assert-Throws { Get-Snapshot } '*definition unavailable*'

Reset-Fixture
$names = 'dim_region','dim_department','dim_kpi','dim_date','dim_account','fact_finance_monthly','fact_kpi_monthly','fact_headcount_monthly','fact_gl_transaction','resolver_catalog','resolver_scope','resolver_calendar','resolver_release'
$script:tableNames = @($names | ForEach-Object { [ordered]@{schemaName='dbo';tableName=$_} })
$script:rowCount = '0'
Assert-Throws { Get-Snapshot -RequireZavaContract } '*Empty required Zava table*'
$script:rowCount = '1'
$definitions = @{
    fact_finance_monthly=@('month_start','kpi_component','amount_usd')
    fact_kpi_monthly=@('month_start','kpi_code','kpi_value')
    fact_headcount_monthly=@('month_start','','headcount_fte')
    fact_gl_transaction=@('posting_date','account_code','amount_usd')
}
$script:columns = @(
    foreach ($name in $names) {
        if (!$definitions.ContainsKey($name)) { New-Column $name 'id' 'int'; continue }
        $date, $key, $measure = $definitions[$name]
        New-Column $name $date 'date'
        New-Column $name 'region_code'
        New-Column $name 'department_code'
        if ($key) { New-Column $name $key }
        New-Column $name $measure 'decimal' '4'
    }
)
$script:financial = @([ordered]@{monthStart='2025-11-01';regionCode='EMEA';departmentCode='IT';keyValue=$null;sum='1.0000';rowCount='1';nonNullCount='1'})
$script:releases = @(@{id='1'})
$source = Get-Snapshot -RequireZavaContract
Assert-True ($source.tables.Count -eq 13 -and $source.financial.Count -eq 4) 'All thirteen tables and four fact profiles must be captured'
Assert-True (($source.financial | Where-Object tableName -eq 'fact_headcount_monthly').keyName -eq $null) 'Headcount must have no artificial financial key'
Assert-True (($source.financial | Where-Object tableName -eq 'fact_gl_transaction').keyName -ceq 'account_code') 'GL must reconcile by account key'
[Array]::Reverse($script:columns)
[Array]::Reverse($script:tableNames)
$reordered = Get-Snapshot -RequireZavaContract
Assert-True ((Compare-FabricEstateSqlSnapshot $source $reordered).Count -eq 0) 'SQL result order must not affect the snapshot'
$script:columns += New-Column 'fact_gl_transaction' 'transaction_date' 'date'
Assert-True ((Get-Snapshot -RequireZavaContract).financial.Count -eq 4) 'Multiple GL dates may be used only after their financial months agree'
$script:glPeriodMismatch = $true
Assert-Throws { Get-Snapshot -RequireZavaContract } '*GL dates disagree*'

foreach ($section in 'permissions','principals','policies') {
    $changed = Copy-Snapshot $source
    $changed[$section] = @(@{name='not automatically restored'})
    Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed).Count -eq 0) "$section belongs to the default manual security gate"
    Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed -CompareSecurity).Count -gt 0) "-CompareSecurity must detect $section changes"
}
$changed = Copy-Snapshot $source
$changed.sqlObjects = @(@{schemaName='dbo';objectName='custom_view';definition='CREATE VIEW dbo.custom_view AS SELECT 1 AS id;'})
Assert-True ((Compare-FabricEstateSqlSnapshot $source $changed).Count -gt 0) 'Custom SQL objects must always compare'
$changed = Copy-Snapshot $source
$changed.principals = @(@{name='runtime';sid='source-tenant'})
$otherTenant = Copy-Snapshot $changed
$otherTenant.principals[0].sid = 'target-tenant'
Assert-True ((Compare-FabricEstateSqlSnapshot $changed $otherTenant).Count -eq 0) 'Cross-tenant SIDs must not fail default physical-clone comparison'
Assert-True ((Compare-FabricEstateSqlSnapshot $changed $otherTenant -CompareSecurity).Count -eq 1) 'Source security mutation must be detected'
$otherTenant.Remove('principals')
Assert-Throws { Compare-FabricEstateSqlSnapshot $changed $otherTenant } '*Missing snapshot section principals*'
Reset-Fixture
$script:objects = @([ordered]@{schemaName='dbo';objectName='custom_view';objectType='VIEW';definition='CREATE VIEW dbo.custom_view AS SELECT 1 AS id;'})
$custom = Get-Snapshot
Assert-True ($custom.sqlObjects -is [array] -and $custom.sqlObjects.Count -eq 1) 'Custom DDL must remain visible to the workflow export blocker'
Assert-True (($script:queries -join "`n") -notmatch '\b(CREATE|ALTER|INSERT|UPDATE|DELETE|DROP)\b') 'Snapshot inventory must remain read-only'
Reset-Fixture
$script:tableNames = @(1..1025 | ForEach-Object { [ordered]@{schemaName='dbo';tableName="table$_"} })
Assert-Throws { Get-Snapshot } '*1024-table limit*'

if ($LiveServer) {
    . (Join-Path $PSScriptRoot '..\scripts\Resolver-Sql.ps1')
    $connection = @{Server=$LiveServer;Database=$LiveDatabase;SqlClientDirectory=$LiveSqlClientDirectory}
    $synthetic = @'
FROM (VALUES
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'fraction', CAST(0.1 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'fraction', CAST(0.2 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'fraction', CAST(NULL AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'cancellation', CAST(1E16 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'cancellation', CAST(1 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'cancellation', CAST(-1E16 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'real', CAST(0.1 AS real)),
 (CAST('2025-11-01' AS date), N'emea', N'IT', N'real', CAST(1 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA ', N'IT', N'real', CAST(2 AS float(53))),
 (CAST('2025-11-01' AS date), N'EMEA', N'IT', N'null', CAST(NULL AS float(53)))
) AS synthetic(month_start,region_code,department_code,kpi_component,amount_usd)
'@
    if ([regex]::Matches($floatQuery, [regex]::Escape('FROM [dbo].[fact_finance_monthly]')).Count -ne 1) { throw 'Cannot replace the financial query source with synthetic VALUES safely.' }
    $rows = Read-FabricEstateSqlRows $connection $floatQuery.Replace('FROM [dbo].[fact_finance_monthly]', $synthetic.Trim())
    Assert-True ($rows.Count -eq 6) 'Live SQL must preserve case-sensitive and trailing-space-distinct groups'
    foreach ($row in $rows) { $row['sum'] = ConvertTo-FabricEstateFloatSum $row.binaryParts $row.nonNullCount }
    Assert-True (($rows | Where-Object keyValue -CEQ 'fraction').sum -ceq $exactFractionSum) 'Live SQL 0.1 + 0.2 must retain the exact binary sum'
    Assert-True (($rows | Where-Object keyValue -CEQ 'cancellation').sum -ceq '1') 'Live SQL must preserve the small cancellation residual'
    Assert-True (($rows | Where-Object { $_.keyValue -ceq 'real' -and $_.regionCode -ceq 'EMEA' }).sum -ceq '0.100000001490116119384765625') 'Live SQL widening of real must preserve its stored value'
    Assert-True ($null -eq ($rows | Where-Object keyValue -CEQ 'null').sum) 'Live SQL must preserve all-null groups'
    Assert-True (($rows | Where-Object keyValue -CEQ 'fraction').nonNullCount -ceq '2') 'Live SQL must count non-null measures separately'
}
$scope = if ($LiveServer) { 'mocked SQL and read-only live synthetic queries' } else { 'mocked SQL only' }
Write-Output "PASS: $script:assertions assertions; Fabric SQL snapshots and exact reconciliation ($scope)."
