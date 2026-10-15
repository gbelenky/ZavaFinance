#Requires -Version 7.6
if (!(Get-Command Invoke-ResolverSql -CommandType Function -ErrorAction SilentlyContinue)) {
    . (Join-Path $PSScriptRoot 'Resolver-Sql.ps1')
}

function ConvertTo-FabricEstateSqlIdentifier([string]$Name) {
    '[' + $Name.Replace(']', ']]') + ']'
}

function Read-FabricEstateSqlRows {
    param([hashtable]$Connection, [string]$Query)
    $table = Invoke-ResolverSql @Connection -Query $Query -ErrorAction Stop
    if ($table -isnot [System.Data.DataTable]) { throw 'Invoke-ResolverSql must return a DataTable.' }
    if ($table.Rows.Count -gt 100000) { throw 'SQL snapshot exceeds the 100000-row inventory/group limit; narrow the estate.' }
    $rows = @(
        foreach ($row in $table.Rows) {
            $item = [ordered]@{}
            foreach ($column in $table.Columns) {
                $value = $row[$column.ColumnName]
                $item[$column.ColumnName] = if ($value -is [DBNull]) { $null }
                    elseif ($value -is [IFormattable]) { $value.ToString($null, [Globalization.CultureInfo]::InvariantCulture) }
                    else { [string]$value }
            }
            $item
        }
    )
    $sortKeys = [string[]]@($rows | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 10 -Compress })
    # Explicit generic binding avoids PowerShell copying the items array during overload conversion.
    [Array]::Sort[string,object]($sortKeys, $rows, [StringComparer]::Ordinal)
    return ,$rows
}

function ConvertTo-FabricEstateFloatSum([string]$Parts, [string]$NonNullCount) {
    if ([string]::IsNullOrEmpty($Parts) -or $NonNullCount -cnotmatch '^(0|[1-9][0-9]{0,19})$') {
        throw 'Invalid floating-point aggregate metadata.'
    }
    $components = $Parts.Split(';')
    if ($components.Count -gt 2048) { throw 'Invalid floating-point exponent inventory.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $total = [Numerics.BigInteger]::Zero
    $hasValue = $false
    foreach ($component in $components) {
        if ($component -ceq ':') {
            if (!$seen.Add('null')) { throw 'Invalid floating-point duplicate null component.' }
            continue
        }
        if ($component -cnotmatch '^(?<exponent>0|[1-9][0-9]{0,3}):(?<significand>-?(0|[1-9][0-9]{0,37}))$') {
            throw 'Invalid floating-point significand component.'
        }
        $exponent = [int]$Matches.exponent
        if ($exponent -gt 2046 -or !$seen.Add($Matches.exponent)) { throw 'Invalid floating-point exponent (non-finite or repeated).' }
        $significand = [Numerics.BigInteger]::Parse($Matches.significand, [Globalization.CultureInfo]::InvariantCulture)
        # All finite binary64 values are integer multiples of 2^-1074, including subnormals.
        $total += [Numerics.BigInteger]::op_LeftShift($significand, [Math]::Max(0, $exponent - 1))
        $hasValue = $true
    }
    if ($hasValue -ne ($NonNullCount -cne '0')) { throw 'Invalid floating-point non-null count.' }
    if (!$hasValue) { return $null }
    if ($total.IsZero) { return '0' }
    $digits = ([Numerics.BigInteger]::Abs($total) * [Numerics.BigInteger]::Pow(5,1074)).ToString([Globalization.CultureInfo]::InvariantCulture).PadLeft(1075, '0')
    $text = $digits.Insert($digits.Length - 1074, '.').TrimEnd('0').TrimEnd('.')
    if ($total.Sign -lt 0) { $text = '-' + $text }
    return $text
}

function Get-FabricEstateSqlSnapshot {
    <#
    .SYNOPSIS
    Reads a bounded SQL reconciliation baseline; does not export or restore data.
    .DESCRIPTION
    Requires database VIEW DEFINITION and SELECT on every captured table through the
    SQL analytics endpoint. Use an authorized audit identity with unfiltered data and
    complete security-metadata visibility; RLS-filtered totals are not an estate baseline.
    Uses Invoke-ResolverSql's existing delegated Azure CLI token and SQL client dependencies.
    Errors (including hidden/encrypted SQL module definitions) are fatal, never empty inventories.
    Pause writes and await endpoint synchronization: separate reads are NOT an atomic snapshot.
    Read persisted JSON with ConvertFrom-Json -AsHashtable -DateKind String to retain timestamps.

    Returns an ordered hashtable with array sections columns, tables, financial,
    resolverRelease, sqlObjects, permissions, principals, policies. Database scalars are
    invariant strings or null; counts and decimal sums never pass through floating point.
    Columns contain schemaName, tableName, columnName, ordinal, type, maxLength (bytes),
    precision, scale, nullable (0/1). No server name, database name or timestamp is compared.
    Each financial row contains schemaName, tableName, keyName, measureName, monthStart,
    regionCode, departmentCode, keyValue, sum, rowCount, nonNullCount.
    resolverRelease contains schemaName, tableName and values (all release columns).
    All arrays are ordinally sorted and retain empty/singleton JSON-array shape.

    Known facts require region_code and department_code. Monthly facts use month_start:
    finance sums amount_usd by kpi_component; KPI sums kpi_value by kpi_code; headcount
    sums headcount_fte with no extra key. The repository does not define the GL schema:
    the bounded supported GL shape is account_code, amount_usd and one or more of
    month_start, transaction_date, posting_date. Multiple dates must agree on financial
    month and nullness; month_start is preferred. Other shapes fail for explicit review.
    Dates must have SQL date/time types. Exact measures SUM as decimal(38, declared scale);
    overflow is a failure. Float/real measures sum integer significands by binary exponent
    in SQL, then combine with BigInteger into exact decimal strings, without rounding.
    Their SQL binary64 encoding is checked before use; no floating-point SUM is performed.
    Generic tables receive schema/count checks only, NOT a full content checksum.

    Limits: 1024 tables and 100000 rows per inventory, including total financial/release rows.
    SELECT TOP(limit+1) detects overflow instead of silently truncating. Counts scan tables.
    SQL modules, security policies/predicates, principals, explicit permissions and role
    memberships are inventory for MANUAL migration; nothing here claims to restore them.
    In v1, nonempty sqlObjects is an export blocker for custom SQL DDL, not an import plan.
    SIDs/owners/built-ins are retained. Unsupported permission classes retain their numeric
    securable identifier for manual review. Fabric workspace/item permissions and OneLake
    security are outside this SQL-only inventory.
    .PARAMETER RequireZavaContract
    Default false permits generic/empty lakehouses. When set, all 13 documented dbo Zava
    tables (including resolver_release) must exist and be nonempty. An unactivated empty
    resolver_release is valid for staging, but is NOT an accepted migration baseline here.
    .PARAMETER SqlClientDirectory
    Existing Release build output containing the SQL client; no modules are installed.
    Omitted, empty or whitespace values use the repository's existing Release output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [string]$SqlClientDirectory = (Join-Path $PSScriptRoot '..\src\dotnet\tests\ZavaFinance.Tests\bin\Release\net10.0'),
        [switch]$RequireZavaContract
    )
    $ErrorActionPreference = 'Stop'
    if ([string]::IsNullOrWhiteSpace($SqlClientDirectory)) {
        $SqlClientDirectory = Join-Path $PSScriptRoot '..\src\dotnet\tests\ZavaFinance.Tests\bin\Release\net10.0'
    }
    $connection = @{Server=$Server;Database=$Database;SqlClientDirectory=$SqlClientDirectory}
    $access = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:preconditions */
SELECT CONVERT(varchar(1), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')) AS canViewDefinition;
'@
    if ($access.Count -ne 1 -or $access[0].canViewDefinition -cne '1') { throw 'Database VIEW DEFINITION is required for a complete SQL inventory.' }
    $snapshot = [ordered]@{columns=@();tables=@();financial=@();resolverRelease=@();sqlObjects=@();permissions=@();principals=@();policies=@()}
    $floatFormatChecked = $false
    $tableNames = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:tables */
SELECT TOP (1025) s.name AS schemaName, t.name AS tableName
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0;
'@
    if ($tableNames.Count -gt 1024) { throw 'SQL snapshot exceeds the 1024-table limit.' }
    $required = @('dim_region','dim_department','dim_kpi','dim_date','dim_account','fact_finance_monthly',
        'fact_kpi_monthly','fact_headcount_monthly','fact_gl_transaction','resolver_catalog','resolver_scope','resolver_calendar','resolver_release')
    if ($RequireZavaContract) {
        foreach ($name in $required) {
            if (!@($tableNames | Where-Object { $_.schemaName -ceq 'dbo' -and $_.tableName -ceq $name }).Count) {
                throw "Missing required Zava table dbo.$name."
            }
        }
    }
    $snapshot.columns = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:columns */
SELECT TOP (100001) s.name AS schemaName, t.name AS tableName, c.name AS columnName,
 CONVERT(varchar(10), c.column_id) AS ordinal,
 CASE WHEN ty.is_user_defined=1 THEN SCHEMA_NAME(ty.schema_id)+'.'+ty.name ELSE ty.name END AS type,
 CONVERT(varchar(10), c.max_length) AS maxLength, CONVERT(varchar(10), c.precision) AS precision,
 CONVERT(varchar(10), c.scale) AS scale, CONVERT(varchar(1), c.is_nullable) AS nullable
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
 JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE t.is_ms_shipped=0;
'@
    $facts = @{
        fact_finance_monthly=@('kpi_component','amount_usd')
        fact_kpi_monthly=@('kpi_code','kpi_value')
        fact_headcount_monthly=@('','headcount_fte')
        fact_gl_transaction=@('account_code','amount_usd')
    }
    foreach ($table in $tableNames) {
        $qualified = "$(ConvertTo-FabricEstateSqlIdentifier $table.schemaName).$(ConvertTo-FabricEstateSqlIdentifier $table.tableName)"
        $count = Read-FabricEstateSqlRows $connection "/* fabric-estate:count */ SELECT CONVERT(varchar(20), COUNT_BIG(*)) AS [rowCount] FROM $qualified;"
        if ($count.Count -ne 1 -or $count[0].rowCount -cnotmatch '^(0|[1-9][0-9]*)$') { throw "Invalid SQL row count for $qualified." }
        $snapshot.tables += [ordered]@{schemaName=$table.schemaName;tableName=$table.tableName;rowCount=$count[0].rowCount}
        if ($RequireZavaContract -and $table.schemaName -ceq 'dbo' -and $table.tableName -cin $required -and $count[0].rowCount -ceq '0') {
            throw "Empty required Zava table $qualified."
        }
        $columns = @($snapshot.columns | Where-Object { $_.schemaName -ceq $table.schemaName -and $_.tableName -ceq $table.tableName })
        if (!$columns.Count) { throw "Column metadata unavailable for $qualified." }
        if ($table.tableName -ceq 'resolver_release') {
            $projection = @($columns | ForEach-Object {
                $id = ConvertTo-FabricEstateSqlIdentifier $_.columnName
                $style = if ($_.type -in @('binary','varbinary','timestamp','rowversion','money','smallmoney')) { 2 }
                    elseif ($_.type -in @('float','real')) { 3 } else { 126 }
                "CONVERT(nvarchar(max), $id, $style) AS $id"
            }) -join ', '
            $releaseRows = Read-FabricEstateSqlRows $connection "/* fabric-estate:release */ SELECT TOP (100001) $projection FROM $qualified;"
            if ($snapshot.resolverRelease.Count + $releaseRows.Count -gt 100000) { throw 'Release inventory exceeds the 100000-row limit.' }
            foreach ($row in $releaseRows) {
                $snapshot.resolverRelease += [ordered]@{schemaName=$table.schemaName;tableName=$table.tableName;values=$row}
            }
        }
        if ($table.tableName -cnotin $facts.Keys) { continue }
        $key, $measure = $facts[$table.tableName]
        $date = 'month_start'
        $dates = @($date)
        if ($table.tableName -ceq 'fact_gl_transaction') {
            $dates = @('month_start','transaction_date','posting_date' | Where-Object { $_ -cin $columns.columnName })
            if ($dates.Count -eq 0) { throw "GL date contract for $qualified requires month_start, transaction_date or posting_date; review its approved schema." }
            $date = $dates[0]
        }
        foreach ($name in (@($date,'region_code','department_code',$measure,$key) | Where-Object { $_ })) {
            if ($name -cnotin $columns.columnName) { throw "Missing financial column $qualified.$name." }
        }
        foreach ($name in (@('region_code','department_code',$key) | Where-Object { $_ })) {
            if (($columns | Where-Object columnName -CEQ $name).type -notin @('char','varchar','nchar','nvarchar')) {
                throw "Financial key $qualified.$name must be a SQL character type."
            }
        }
        foreach ($dateName in $dates) {
            $dateType = ($columns | Where-Object columnName -CEQ $dateName).type
            if ($dateType -notin @('date','datetime','datetime2','smalldatetime','datetimeoffset')) { throw "Financial date $qualified.$dateName must be a SQL date/time type." }
        }
        $amount = $columns | Where-Object columnName -CEQ $measure
        if ($amount.type -notin @('decimal','numeric','money','smallmoney','bigint','int','smallint','tinyint','float','real')) { throw "Financial measure $qualified.$measure must have a supported numeric type, not $($amount.type)." }
        if ($amount.scale -notmatch '^(0|[1-9][0-9]?)$' -or [int]$amount.scale -gt 38) { throw "Invalid financial scale for $qualified.$measure." }
        $scale = [int]$amount.scale
        $dateId = ConvertTo-FabricEstateSqlIdentifier $date
        $month = "DATEFROMPARTS(YEAR($dateId), MONTH($dateId), 1)"
        if ($dates.Count -gt 1) {
            $mismatches = foreach ($otherDate in $dates[1..($dates.Count - 1)]) {
                $otherId = ConvertTo-FabricEstateSqlIdentifier $otherDate
                "($month <> DATEFROMPARTS(YEAR($otherId), MONTH($otherId), 1) OR ($dateId IS NULL AND $otherId IS NOT NULL) OR ($dateId IS NOT NULL AND $otherId IS NULL))"
            }
            $periodCheck = Read-FabricEstateSqlRows $connection "/* fabric-estate:gl-period */ SELECT TOP (1) '1' AS mismatch FROM $qualified WHERE $($mismatches -join ' OR ');"
            if ($periodCheck.Count) { throw "GL dates disagree on financial month or nullness for $qualified; review the approved period mapping." }
        }
        $region = 'CONVERT(nvarchar(max), [region_code]) COLLATE Latin1_General_100_BIN2'
        $department = 'CONVERT(nvarchar(max), [department_code]) COLLATE Latin1_General_100_BIN2'
        $keyExpr = if ($key) { "CONVERT(nvarchar(max), $(ConvertTo-FabricEstateSqlIdentifier $key)) COLLATE Latin1_General_100_BIN2" } else { 'CAST(NULL AS nvarchar(1))' }
        # SQL string equality ignores trailing spaces even with BIN2; length preserves that distinction.
        $group = @($month,$region,"DATALENGTH($region)",$department,"DATALENGTH($department)") +
            $(if ($key) { @($keyExpr,"DATALENGTH($keyExpr)") } else { @() })
        $amountId = ConvertTo-FabricEstateSqlIdentifier $measure
        $query = @"
/* fabric-estate:financial */
SELECT TOP (100001) CONVERT(varchar(10), $month, 23) AS monthStart,
 $region AS regionCode, $department AS departmentCode, $keyExpr AS keyValue,
 CONVERT(varchar(100), SUM(CAST($amountId AS decimal(38,$scale)))) AS sum,
 CONVERT(varchar(20), COUNT_BIG(*)) AS [rowCount], CONVERT(varchar(20), COUNT_BIG($amountId)) AS nonNullCount
FROM $qualified GROUP BY $($group -join ', ');
"@
        if ($amount.type -in @('float','real')) {
            if (!$floatFormatChecked) {
                $format = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:float-format */
SELECT CONVERT(varchar(16), CONVERT(binary(8), CAST(1.0 AS float(53))), 2) AS one,
 CONVERT(varchar(16), CONVERT(binary(8), CAST(-1.5 AS float(53))), 2) AS negative,
 CONVERT(varchar(16), CONVERT(binary(8), CAST(0.1 AS float(53))), 2) AS fraction;
'@
                if ($format.Count -ne 1 -or $format[0].one -cne '3FF0000000000000' -or
                    $format[0].negative -cne 'BFF8000000000000' -or $format[0].fraction -cne '3FB999999999999A') {
                    throw 'SQL float conversion does not match the required IEEE 754 binary64 encoding.'
                }
                $floatFormatChecked = $true
            }
            $query = @"
/* fabric-estate:financial-float */
WITH binary_parts AS (
 SELECT $month AS monthStart, $region AS regionCode, $department AS departmentCode, $keyExpr AS keyValue,
  estate_float.binaryExponent, SUM(CAST(estate_float.significand AS decimal(38,0))) AS significandSum,
  COUNT_BIG(*) AS [rowCount], COUNT_BIG($amountId) AS nonNullCount
 FROM $qualified
 CROSS APPLY (SELECT CONVERT(bigint, CONVERT(binary(8), CAST($amountId AS float(53)))) AS bits) AS estate_bits
 CROSS APPLY (SELECT (estate_bits.bits & CONVERT(bigint, 0x7FF0000000000000)) / CONVERT(bigint, 4503599627370496) AS binaryExponent) AS estate_exponent
 CROSS APPLY (SELECT estate_exponent.binaryExponent,
  (CASE WHEN estate_bits.bits < 0 THEN -1 ELSE 1 END) *
  ((estate_bits.bits & CONVERT(bigint, 0x000FFFFFFFFFFFFF)) +
   CASE WHEN estate_exponent.binaryExponent = 0 THEN CONVERT(bigint, 0) ELSE CONVERT(bigint, 4503599627370496) END) AS significand) AS estate_float
 GROUP BY $($group -join ', '), estate_float.binaryExponent
)
SELECT TOP (100001) CONVERT(varchar(10), monthStart, 23) AS monthStart, regionCode, departmentCode, keyValue,
 STRING_AGG(CONVERT(nvarchar(max), CONCAT(CONVERT(varchar(4), binaryExponent), ':', CONVERT(varchar(100), significandSum))), ';') AS binaryParts,
 CONVERT(varchar(20), SUM([rowCount])) AS [rowCount], CONVERT(varchar(20), SUM(nonNullCount)) AS nonNullCount
FROM binary_parts GROUP BY monthStart, regionCode, DATALENGTH(regionCode), departmentCode, DATALENGTH(departmentCode), keyValue, DATALENGTH(keyValue);
"@
        }
        $financialRows = Read-FabricEstateSqlRows $connection $query
        if ($amount.type -in @('float','real')) {
            foreach ($row in $financialRows) {
                $row['sum'] = ConvertTo-FabricEstateFloatSum $row.binaryParts $row.nonNullCount
                $row.Remove('binaryParts')
            }
        }
        if ($snapshot.financial.Count + $financialRows.Count -gt 100000) { throw 'Financial inventory exceeds the 100000-row limit.' }
        $groupedCount = [System.Numerics.BigInteger]::Zero
        foreach ($row in $financialRows) {
            if ($row.rowCount -cnotmatch '^[1-9][0-9]*$') { throw "Invalid financial group count for $qualified." }
            $groupedCount += [System.Numerics.BigInteger]::Parse($row.rowCount, [Globalization.CultureInfo]::InvariantCulture)
            $entry = [ordered]@{schemaName=$table.schemaName;tableName=$table.tableName;keyName=$(if ($key) { $key } else { $null });measureName=$measure}
            foreach ($field in $row.Keys) { $entry[$field] = $row[$field] }
            $snapshot.financial += $entry
        }
        if ($groupedCount.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $count[0].rowCount) {
            throw "Financial groups do not reconcile to row count for $qualified; pause writes and retry."
        }
    }
    $snapshot.sqlObjects = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:objects */
SELECT TOP (100001) s.name AS schemaName, o.name AS objectName, o.type_desc AS objectType,
 USER_NAME(COALESCE(o.principal_id,s.principal_id)) AS ownerName, m.definition
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
 LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
WHERE o.is_ms_shipped=0 AND o.type IN ('V','P','FN','IF','TF','TR','PC','FS','FT');
'@
    foreach ($item in $snapshot.sqlObjects) {
        if ($null -eq $item.definition) { throw "SQL object definition unavailable for $($item.schemaName).$($item.objectName); review permissions/encryption or unsupported CLR objects manually." }
    }
    $snapshot.principals = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:principals */
SELECT TOP (100001) name, type_desc AS type, authentication_type_desc AS authenticationType,
 default_schema_name AS defaultSchema, USER_NAME(owning_principal_id) AS ownerName,
 CONVERT(varchar(max),sid,2) AS sid FROM sys.database_principals;
'@
    $snapshot.permissions = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:permissions */
SELECT TOP (100001) * FROM (
SELECT 'permission' AS kind, USER_NAME(p.grantee_principal_id) AS granteeName,
 USER_NAME(p.grantor_principal_id) AS grantorName, p.permission_name AS permissionName, p.state_desc AS state,
 p.class_desc AS className, os.name AS schemaName, o.name AS objectName, c.name AS columnName,
 CASE p.class WHEN 0 THEN '(database)' WHEN 1 THEN o.name WHEN 3 THEN s.name WHEN 4 THEN dp.name
 ELSE CONVERT(nvarchar(128),p.major_id) END AS securableName
FROM sys.database_permissions p
 LEFT JOIN sys.objects o ON p.class=1 AND o.object_id=p.major_id
 LEFT JOIN sys.schemas os ON os.schema_id=o.schema_id
 LEFT JOIN sys.columns c ON p.class=1 AND c.object_id=p.major_id AND c.column_id=p.minor_id
 LEFT JOIN sys.schemas s ON p.class=3 AND s.schema_id=p.major_id
 LEFT JOIN sys.database_principals dp ON p.class=4 AND dp.principal_id=p.major_id
UNION ALL
SELECT 'roleMembership', USER_NAME(member_principal_id), NULL, 'MEMBER', 'GRANT',
 'DATABASE_ROLE', NULL, NULL, NULL, USER_NAME(role_principal_id) FROM sys.database_role_members
) inventory;
'@
    $snapshot.policies = Read-FabricEstateSqlRows $connection @'
/* fabric-estate:policies */
SELECT TOP (100001) s.name AS schemaName, p.name AS policyName,
 CONVERT(varchar(1),p.is_enabled) AS enabled, CONVERT(varchar(1),p.is_schema_bound) AS schemaBound,
 OBJECT_SCHEMA_NAME(pr.target_object_id) AS targetSchema, OBJECT_NAME(pr.target_object_id) AS targetObject,
 pr.predicate_definition AS predicateDefinition, pr.predicate_type_desc AS predicateType, pr.operation_desc AS operation
FROM sys.security_policies p JOIN sys.schemas s ON s.schema_id=p.schema_id
 LEFT JOIN sys.security_predicates pr ON pr.object_id=p.object_id;
'@
    return $snapshot
}

function Compare-FabricEstateSqlSnapshot {
    <#
    .SYNOPSIS
    Returns an array of {path, expected, actual} differences, including missing members.
    .DESCRIPTION
    Accepts snapshots or ConvertFrom-Json results. Compares data counts, columns, financial
    profiles, SQL objects and resolverRelease exactly (ordinal/case-sensitive), including
    array positions, nulls and scalar types; property order is immaterial.
    By default the permissions, principals and policies sections are EXCLUDED because
    cross-tenant identities/security are a separate mandatory manual-review gate.
    Their inventories must still be present. All other properties, including SQL object
    ownership, remain compared. No custom SQL DDL is automatically imported in v1.
    Empty/singleton results remain arrays: assign the result directly and inspect .Count.
    .PARAMETER CompareSecurity
    Also compares permissions, principals (including SIDs), and security policies exactly.
    Use for source-before/source-after checks. Omit for a pre-activation restored-target
    data/schema check, and review/recreate target security separately before activation.
    .PARAMETER IgnoreResolverRelease
    Only AFTER target activation: skips resolverRelease rows and rowCount for matching
    resolver_release table identities. Table existence, schema and other selected sections
    still compare. Before activation, omit this switch for a source/restore check.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual,
        [switch]$IgnoreResolverRelease,
        [switch]$CompareSecurity
    )
    $ErrorActionPreference = 'Stop'
    $differences = [Collections.Generic.List[object]]::new()
    function ConvertTo-SnapshotMap($Value) {
        if ($Value -is [Collections.IDictionary]) { return $Value }
        if ($Value -is [pscustomobject]) {
            $map = [ordered]@{}
            foreach ($property in $Value.PSObject.Properties) { $map[$property.Name] = $property.Value }
            return $map
        }
        return $null
    }
    function Compare-Value($Left, $Right, [string]$Path) {
        $leftMap, $rightMap = (ConvertTo-SnapshotMap $Left), (ConvertTo-SnapshotMap $Right)
        if ($null -ne $leftMap -and $null -ne $rightMap) {
            $keys = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
            foreach ($key in @($leftMap.Keys) + @($rightMap.Keys)) { [void]$keys.Add([string]$key) }
            foreach ($key in $keys) {
                if (!$CompareSecurity -and $Path -ceq '$' -and $key -cin @('permissions','principals','policies')) { continue }
                if ($IgnoreResolverRelease -and $Path -ceq '$' -and $key -ceq 'resolverRelease') { continue }
                if ($IgnoreResolverRelease -and $Path -match '^\$\.tables\[\d+\]$' -and $key -ceq 'rowCount' -and
                    $leftMap.tableName -ceq 'resolver_release' -and $rightMap.tableName -ceq 'resolver_release' -and
                    $leftMap.schemaName -ceq $rightMap.schemaName) { continue }
                if ($key -cnotin $leftMap.Keys -or $key -cnotin $rightMap.Keys) {
                    $differences.Add([ordered]@{path="$Path.$key";expected=$leftMap[$key];actual=$rightMap[$key];reason='missing property'})
                } else { Compare-Value $leftMap[$key] $rightMap[$key] "$Path.$key" }
            }
        } elseif ($Left -is [array] -and $Right -is [array]) {
            for ($i=0; $i -lt [Math]::Max($Left.Count,$Right.Count); $i++) {
                if ($i -ge $Left.Count -or $i -ge $Right.Count) {
                    $differences.Add([ordered]@{path="$Path[$i]";expected=$(if ($i -lt $Left.Count) { $Left[$i] });actual=$(if ($i -lt $Right.Count) { $Right[$i] });reason='missing row'})
                } else { Compare-Value $Left[$i] $Right[$i] "$Path[$i]" }
            }
        } elseif ((ConvertTo-Json -InputObject $Left -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $Right -Depth 30 -Compress)) {
            $differences.Add([ordered]@{path=$Path;expected=$Left;actual=$Right})
        }
    }
    foreach ($snapshot in @($Expected,$Actual)) {
        $map = ConvertTo-SnapshotMap $snapshot
        foreach ($section in 'columns','tables','financial','resolverRelease','sqlObjects','permissions','principals','policies') {
            if ($null -eq $map -or $section -cnotin $map.Keys) { throw "Missing snapshot section $section." }
            if ($map[$section] -isnot [array]) { throw "Snapshot section $section must be an array." }
        }
    }
    Compare-Value $Expected $Actual '$'
    return ,$differences.ToArray()
}
