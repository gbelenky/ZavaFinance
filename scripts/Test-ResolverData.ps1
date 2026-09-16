#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SqlServer,
    [Parameter(Mandatory)][string]$SqlDatabase,
    [Parameter(Mandatory)][string]$CatalogVersion,
    [switch]$VerifyDemoReferenceValues,
    [string]$SqlClientDirectory = (Join-Path $PSScriptRoot '..\tests\ZavaFinance.Tests\bin\Release\net10.0')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolver-Sql.ps1')
$query = @'
WITH c AS (SELECT * FROM dbo.resolver_catalog WHERE catalog_version=@version),
     s AS (SELECT * FROM dbo.resolver_scope WHERE catalog_version=@version),
     f AS (SELECT DISTINCT region_code,department_code FROM dbo.fact_finance_monthly),
     root_scope AS (SELECT region_code,department_code FROM s WHERE node_id='org-company')
SELECT 'catalogue has 114 entities' AS check_name, COUNT(*) AS actual, 114 AS expected FROM c
UNION ALL SELECT 'scope has 336 distinct pairs', COUNT(*),336 FROM s
UNION ALL SELECT '18 executable KPIs', COUNT(*),18 FROM c WHERE entity_kind='kpi' AND is_reportable=1
UNION ALL SELECT '6 non-executable KPI families',COUNT(*),6 FROM c WHERE entity_kind='kpi_group' AND is_reportable=0
UNION ALL SELECT 'no duplicate entity IDs',COUNT(*),0 FROM (SELECT entity_id FROM c GROUP BY entity_id HAVING COUNT(*)>1) d
UNION ALL SELECT 'no duplicate scope keys',COUNT(*),0 FROM (SELECT node_id,region_code,department_code FROM s GROUP BY node_id,region_code,department_code HAVING COUNT(*)>1) d
UNION ALL SELECT 'no missing parents',COUNT(*),0 FROM c WHERE parent_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM c p WHERE p.entity_id=c.parent_id)
UNION ALL SELECT 'all scope keys exist in facts',COUNT(*),0 FROM s WHERE NOT EXISTS (SELECT 1 FROM f WHERE f.region_code=s.region_code AND f.department_code=s.department_code)
UNION ALL SELECT 'company covers every fact key',COUNT(*),0 FROM f WHERE NOT EXISTS (SELECT 1 FROM root_scope r WHERE r.region_code=f.region_code AND r.department_code=f.department_code)
UNION ALL SELECT 'every organization has a scope',COUNT(*),0 FROM c WHERE entity_kind='org' AND NOT EXISTS (SELECT 1 FROM s WHERE s.node_id=c.entity_id);
'@
$table = Invoke-ResolverSql -Server $SqlServer -Database $SqlDatabase -SqlClientDirectory $SqlClientDirectory -Query $query -Parameters @{'@version'=$CatalogVersion}
$checks = @($table | ForEach-Object { [pscustomobject]@{check=$_.check_name;actual=$_.actual;expected=$_.expected;passed=($_.actual -eq $_.expected)} })
if ($VerifyDemoReferenceValues) {
    $reference = Invoke-ResolverSql -Server $SqlServer -Database $SqlDatabase -SqlClientDirectory $SqlClientDirectory -Parameters @{'@version'=$CatalogVersion} -Query @'
WITH amounts AS (
    SELECT
        SUM(CASE WHEN kpi_component='GROSS_REVENUE' THEN amount_usd ELSE 0 END) AS gross,
        SUM(CASE WHEN kpi_component='REVENUE_DEDUCTIONS' THEN amount_usd ELSE 0 END) AS deductions,
        SUM(CASE WHEN kpi_component='COGS' THEN amount_usd ELSE 0 END) AS cogs
    FROM dbo.fact_finance_monthly f
    WHERE month_start >= '2025-11-01' AND month_start < '2025-12-01'
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s WHERE s.catalog_version=@version
          AND s.node_id='org-region-emea' AND s.region_code=f.region_code AND s.department_code=f.department_code)
)
SELECT 'EMEA Nov 2025 net revenue' AS check_name, CAST(ROUND(gross-deductions,2) AS decimal(20,2)) AS actual, CAST(85639562.37 AS decimal(20,2)) AS expected FROM amounts
UNION ALL SELECT 'EMEA Nov 2025 gross margin', CAST(ROUND((gross-deductions-cogs)/NULLIF(gross-deductions,0)*100,2) AS decimal(20,2)),42.52 FROM amounts
UNION ALL SELECT 'EMEA Nov 2025 closing headcount',CAST(SUM(k.kpi_value) AS decimal(20,2)),3258
FROM dbo.fact_kpi_monthly k
WHERE k.month_start='2025-11-01' AND k.kpi_code='KPI-013'
  AND EXISTS (SELECT 1 FROM dbo.resolver_scope s WHERE s.catalog_version=@version
      AND s.node_id='org-region-emea' AND s.region_code=k.region_code AND s.department_code=k.department_code);
'@
    $checks += @($reference | ForEach-Object { [pscustomobject]@{check=$_.check_name;actual=$_.actual;expected=$_.expected;passed=($_.actual -eq $_.expected)} })
}
$checks | ConvertTo-Json
if (@($checks | Where-Object passed -eq $false).Count) { throw 'Resolver data invariants failed. Do not activate this release.' }
