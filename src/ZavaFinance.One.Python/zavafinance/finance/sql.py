"""Delegated, parameterized Fabric SQL reads using the public mssql-python driver."""

import asyncio
import json
import logging
import re
import struct
import threading
from decimal import Decimal

from ..config import Settings
from ..contracts import TokenProvider
from .calculations import FinanceComponents, KpiCalculator, KpiDefinition
from .periods import FinancePeriod
from .resolver import CatalogChangedException, ResolverCatalog, ResolverEntity, ResolverRelease
from .statement import OrganizationScope, StatementResult

logger = logging.getLogger(__name__)
SQL_SCOPE = "https://database.windows.net/.default"
RELEASE_SQL = "SELECT catalog_version, search_index, embedding_deployment, embedding_dimensions FROM dbo.resolver_release;"
CATALOG_SQL = """
DECLARE @version nvarchar(64) = ?;
SELECT c.entity_id, c.entity_kind, c.canonical_name, c.aliases_json, c.definition,
       c.parent_id, c.hierarchy_path, c.kpi_code, c.region_code, c.department_code,
       c.department_group, c.is_reportable
FROM dbo.resolver_catalog c
WHERE c.catalog_version = @version
  AND EXISTS (SELECT 1 FROM dbo.resolver_release r WHERE r.catalog_version = @version)
  AND (
    (c.entity_kind IN ('kpi', 'kpi_group')
     AND EXISTS (SELECT 1 FROM fact_finance_monthly))
    OR (c.entity_kind = 'org' AND EXISTS (
        SELECT 1 FROM dbo.resolver_scope s
        INNER JOIN fact_finance_monthly f
          ON f.region_code = s.region_code AND f.department_code = s.department_code
        WHERE s.catalog_version = c.catalog_version AND s.node_id = c.entity_id
    ))
  )
ORDER BY c.entity_id;
"""
COMPONENTS_SQL = """
DECLARE @start date = ?, @end date = ?, @version nvarchar(64) = ?, @node nvarchar(200) = ?,
        @searchIndex nvarchar(128) = ?, @embeddingDeployment nvarchar(64) = ?, @embeddingDimensions int = ?;
IF (SELECT COUNT(*) FROM dbo.resolver_release) <> 1
   OR NOT EXISTS (SELECT 1 FROM dbo.resolver_release
       WHERE catalog_version = @version AND search_index = @searchIndex
         AND embedding_deployment = @embeddingDeployment AND embedding_dimensions = @embeddingDimensions)
    THROW 51000, 'Resolver catalogue changed.', 1;
DECLARE @closing date = DATEADD(month, -1, @end);

WITH scoped AS (
    SELECT f.kpi_component, f.amount_usd
    FROM fact_finance_monthly f
    WHERE f.month_start >= @start AND f.month_start < @end
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
          WHERE s.catalog_version = @version AND s.node_id = @node
            AND s.region_code = f.region_code AND s.department_code = f.department_code)
),
components AS (
    SELECT
        SUM(CASE WHEN kpi_component = 'GROSS_REVENUE' THEN amount_usd ELSE 0 END) AS gross_revenue,
        SUM(CASE WHEN kpi_component = 'REVENUE_DEDUCTIONS' THEN amount_usd ELSE 0 END) AS revenue_deductions,
        SUM(CASE WHEN kpi_component = 'COGS' THEN amount_usd ELSE 0 END) AS cogs,
        SUM(CASE WHEN kpi_component = 'OPEX' THEN amount_usd ELSE 0 END) AS opex,
        SUM(CASE WHEN kpi_component = 'DA' THEN amount_usd ELSE 0 END) AS da,
        COUNT(*) AS row_count
    FROM scoped
),
budget AS (
    SELECT SUM(k.kpi_value) AS budget_net_revenue
    FROM fact_kpi_monthly k
    WHERE k.kpi_code = 'KPI-018'
      AND k.month_start >= @start AND k.month_start < @end
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
          WHERE s.catalog_version = @version AND s.node_id = @node
            AND s.region_code = k.region_code AND s.department_code = k.department_code)
),
headcount AS (
    SELECT SUM(h.headcount_fte) AS closing_headcount
    FROM fact_headcount_monthly h
    WHERE h.month_start = @closing
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
          WHERE s.catalog_version = @version AND s.node_id = @node
            AND s.region_code = h.region_code AND s.department_code = h.department_code)
),
dso AS (
    SELECT SUM(d.kpi_value * ISNULL(r.net_revenue, 0)) AS dso_weighted
    FROM fact_kpi_monthly d
    LEFT JOIN (
        SELECT region_code, department_code, month_start, kpi_value AS net_revenue
        FROM fact_kpi_monthly
        WHERE kpi_code = 'KPI-003'
    ) r
      ON r.region_code = d.region_code
     AND r.department_code = d.department_code
     AND r.month_start = d.month_start
    WHERE d.kpi_code = 'KPI-015'
      AND d.month_start >= @start AND d.month_start < @end
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
          WHERE s.catalog_version = @version AND s.node_id = @node
            AND s.region_code = d.region_code AND s.department_code = d.department_code)
),
sply AS (
    SELECT SUM(k.kpi_value) AS net_revenue_sply
    FROM fact_kpi_monthly k
    WHERE k.kpi_code = 'KPI-003'
      AND k.month_start >= DATEADD(year, -1, @start)
      AND k.month_start < DATEADD(year, -1, @end)
      AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
          WHERE s.catalog_version = @version AND s.node_id = @node
            AND s.region_code = k.region_code AND s.department_code = k.department_code)
)
SELECT c.gross_revenue, c.revenue_deductions, c.cogs, c.opex, c.da, c.row_count,
       b.budget_net_revenue, h.closing_headcount, d.dso_weighted, s.net_revenue_sply
FROM components c
CROSS JOIN budget b
CROSS JOIN headcount h
CROSS JOIN dso d
CROSS JOIN sply s;
"""


def token_attributes(token: str) -> dict[int, bytes]:
    if not isinstance(token, str) or not token.strip():
        raise ValueError("A delegated SQL token is required.")
    from mssql_python import ConstantsDDBC
    encoded = token.encode("utf-16-le")
    return {ConstantsDDBC.SQL_COPT_SS_ACCESS_TOKEN.value: struct.pack("<I", len(encoded)) + encoded}


def _money(value) -> Decimal:
    if value is None:
        return Decimal(0)
    if not isinstance(value, (Decimal, int, str)) or isinstance(value, bool):
        raise ValueError("Expected an exact SQL decimal value.")
    result = Decimal(value)
    if not result.is_finite():
        raise ValueError("Expected a finite SQL decimal value.")
    return result


def _integer(value) -> int:
    if value is None:
        return 0
    if type(value) is not int or not -(2 ** 31) <= value < 2 ** 31:
        raise ValueError("Expected a SQL int value.")
    return value


def _string(value, *, nullable: bool = False) -> str | None:
    if value is None and nullable:
        return None
    if not isinstance(value, str):
        raise ValueError("Expected catalogue text.")
    return value


def _release(row) -> ResolverRelease:
    if len(row) != 4 or any(value is None for value in row):
        raise ValueError("No published resolver catalogue.")
    release = ResolverRelease(*row)
    release.validate()
    return release


class FabricStatementQuery:
    def __init__(self, settings: Settings, token_provider: TokenProvider):
        self.settings, self.token_provider = settings, token_provider

    async def _read(self, operation):
        if not self.settings.sql_endpoint.strip() or not self.settings.database.strip():
            raise ValueError("Finance warehouse is not configured.")
        if not 1 <= self.settings.sql_timeout_seconds <= 300:
            raise ValueError("SQL timeout must be between 1 and 300 seconds.")
        token = await self.token_provider.get_token((SQL_SCOPE,))
        attributes = token_attributes(token)
        cancelled = threading.Event()

        def run():
            from mssql_python import connect, DatabaseError
            if cancelled.is_set():
                raise InterruptedError()
            # Keyword values are escaped/validated by the driver's connection-string builder.
            connection = connect(
                Server=self.settings.sql_endpoint, Database=self.settings.database,
                Encrypt="yes", TrustServerCertificate="no", autocommit=True,
                attrs_before=attributes, timeout=self.settings.sql_timeout_seconds,
            )
            try:
                connection.timeout = self.settings.sql_timeout_seconds
                if cancelled.is_set():
                    raise InterruptedError()
                return operation(connection, cancelled)
            except DatabaseError as exc:
                diagnostic = str(getattr(exc, "ddbc_error", ""))
                if re.search(r"\b51000\b", diagnostic) and "Resolver catalogue changed." in diagnostic:
                    raise CatalogChangedException() from None
                if "HYT00" in diagnostic or "HYT01" in diagnostic:
                    raise TimeoutError("Finance SQL timed out.") from None
                raise
            finally:
                connection.close()

        try:
            return await asyncio.to_thread(run)
        except asyncio.CancelledError:
            # The synchronous driver has no public cursor cancellation API. The current
            # read is bounded by Connection.timeout; no subsequent read may start.
            cancelled.set()
            raise

    @staticmethod
    def _rows(connection, sql: str, parameters: tuple = (), *, limit: int, cancelled: threading.Event):
        cursor = connection.cursor()
        try:
            if cancelled.is_set():
                raise InterruptedError()
            cursor.execute(sql, parameters, use_prepare=False)
            rows = []
            while True:
                if cancelled.is_set():
                    raise InterruptedError()
                row = cursor.fetchone()
                if row is None:
                    return rows
                if len(rows) >= limit:
                    raise ValueError("Finance SQL returned more rows than permitted.")
                rows.append(row)
        finally:
            cursor.close()

    async def get_release(self) -> ResolverRelease:
        def read(connection, cancelled):
            rows = self._rows(connection, RELEASE_SQL, limit=1, cancelled=cancelled)
            if len(rows) != 1:
                raise ValueError("Resolver release must contain exactly one version.")
            return _release(rows[0])
        return await self._read(read)

    async def load_catalog(self) -> ResolverCatalog:
        release = await self.get_release()

        def read(connection, cancelled):
            rows = self._rows(connection, CATALOG_SQL, (release.catalog_version,), limit=20000, cancelled=cancelled)
            entities = []
            for row in rows:
                if len(row) != 12:
                    raise ValueError("Invalid catalogue row.")
                aliases = json.loads(_string(row[3]))
                if aliases is None:
                    aliases = []
                if not isinstance(aliases, list) or any(not isinstance(alias, str) for alias in aliases):
                    raise ValueError("Catalogue aliases must be an array of strings.")
                if row[11] not in (True, False, 0, 1) or row[11] is None:
                    raise ValueError("Invalid reportable flag.")
                entities.append(ResolverEntity(
                    release.catalog_version, _string(row[0]), _string(row[1]), _string(row[2]),
                    tuple(aliases), _string(row[4], nullable=True) or "",
                    _string(row[5], nullable=True), _string(row[6], nullable=True) or "",
                    *(_string(row[i], nullable=True) for i in range(7, 11)), bool(row[11]),
                ))
            if len({entity.id.lower() for entity in entities}) != len(entities):
                raise ValueError("Duplicate resolver catalogue IDs.")
            return entities

        entities = await self._read(read)
        if release != await self.get_release():
            raise CatalogChangedException()
        return ResolverCatalog(release, tuple(entities))

    async def get_statement(self, kpi: KpiDefinition, organization: OrganizationScope, period: FinancePeriod) -> StatementResult:
        release = organization.release
        if release is None or organization.catalog_version != release.catalog_version:
            raise CatalogChangedException()
        release.validate()
        if not organization.code or len(organization.code) > 200:
            raise ValueError("Invalid organization node.")
        if release != await self.get_release():
            raise CatalogChangedException()
        comparison = period.prior_year() if kpi.code == "KPI-017" else period.prior_period()

        def read(connection, cancelled):
            current = self._components(connection, organization, period, cancelled)
            prior = self._components(connection, organization, comparison, cancelled)
            logger.info("get_statement query complete. Kpi=%s Scope=%s Months=%s Rows=%s",
                        kpi.code, organization.kind, period.month_count, current.has_rows)
            return StatementResult(kpi, organization, period,
                                   KpiCalculator.compute(kpi, current), KpiCalculator.compute(kpi, prior), "USD")

        return await self._read(read)

    def _components(self, connection, organization: OrganizationScope, period: FinancePeriod, cancelled) -> FinanceComponents:
        release = organization.release
        rows = self._rows(connection, COMPONENTS_SQL, (
            period.start, period.end, organization.catalog_version, organization.code,
            release.search_index, release.embedding_deployment, release.embedding_dimensions,
        ), limit=1, cancelled=cancelled)
        if not rows:
            return FinanceComponents()
        row = rows[0]
        if len(row) != 10:
            raise ValueError("Invalid finance aggregate row.")
        return FinanceComponents(
            gross_revenue=_money(row[0]), revenue_deductions=_money(row[1]), cogs=_money(row[2]),
            operating_expenses=_money(row[3]), depreciation_amortisation=_money(row[4]),
            has_rows=_integer(row[5]) > 0, budget_net_revenue=_money(row[6]),
            closing_headcount=_integer(int(_money(row[7]))), dso_weighted=_money(row[8]),
            net_revenue_prior_year=None if row[9] is None else _money(row[9]),
        )
