import asyncio
import inspect
import struct
import threading
import unittest
from dataclasses import replace
from datetime import date
from decimal import Decimal as D
from unittest.mock import patch

import mssql_python
from mssql_python.auth import AADAuth

from zavafinance.config import Settings
from zavafinance.finance import (
    CatalogChangedException, FabricStatementQuery, FinancePeriodParser,
    KpiCatalog, OrganizationScope, ResolverRelease,
)
from zavafinance.finance.sql import CATALOG_SQL, COMPONENTS_SQL, RELEASE_SQL, SQL_SCOPE, token_attributes

RELEASE = ResolverRelease("v1", "resolver-v1", "embedding-model", 1536)
RELEASE_ROW = ("v1", "resolver-v1", "embedding-model", 1536)
CATALOG_ROW = ("KPI-003", "kpi", "Net Revenue", '["net sales"]', "Definition", None, "Net Revenue", "KPI-003", None, None, None, True)
COMPONENT_ROW = (D(1000), D(100), D(400), D(200), D(50), 10, D(800), 10, D(40500), D(750))


class Tokens:
    def __init__(self):
        self.scopes = []

    async def get_token(self, scopes):
        self.scopes.append(scopes)
        return f"delegated-caller-token-{len(self.scopes)}"


class Cursor:
    def __init__(self, connection, rows):
        self.connection, self.rows, self.closed = connection, iter(rows), False
        self.description = [("value",)]

    def execute(self, sql, parameters, *, use_prepare):
        self.connection.executions.append((sql, parameters, use_prepare))
        if parameters and not use_prepare:
            raise RuntimeError("Cannot execute unprepared statement")
        if self.connection.failure:
            raise self.connection.failure
        return self

    def fetchone(self):
        if self.description is None:
            raise RuntimeError("No active result set")
        return next(self.rows, None)

    def close(self):
        self.closed = True


class Connection:
    def __init__(self, *resultsets, failure=None):
        self.resultsets = list(resultsets)
        self.executions, self.cursors = [], []
        self.closed, self.timeout, self.failure = False, None, failure

    def cursor(self):
        cursor = Cursor(self, self.resultsets.pop(0))
        self.cursors.append(cursor)
        return cursor

    def close(self):
        self.closed = True


class SqlContractTests(unittest.TestCase):
    def test_installed_driver_token_and_parameters_contract(self):
        self.assertEqual("1.15.0", mssql_python.__version__)
        signature = inspect.signature(mssql_python.connect)
        self.assertIn("attrs_before", signature.parameters)
        self.assertIn("timeout", signature.parameters)
        self.assertIn("use_prepare", inspect.signature(mssql_python.Cursor.execute).parameters)
        self.assertTrue(hasattr(mssql_python.Connection, "timeout"))
        attributes = token_attributes("synthetic-unit-test-token")
        packed = attributes[mssql_python.ConstantsDDBC.SQL_COPT_SS_ACCESS_TOKEN.value]
        self.assertEqual(AADAuth.get_token_struct("synthetic-unit-test-token"), packed)
        self.assertEqual(len(packed) - 4, struct.unpack("<I", packed[:4])[0])
        self.assertEqual("synthetic-unit-test-token", packed[4:].decode("utf-16-le"))
        self.assertNotEqual(attributes, token_attributes("other-caller"))
        for invalid in ("", " ", None):
            with self.assertRaises(ValueError):
                token_attributes(invalid)

    def test_every_fact_aggregate_has_deduplicating_versioned_scope_membership(self):
        self.assertEqual(5, COMPONENTS_SQL.count("EXISTS (SELECT 1 FROM dbo.resolver_scope s"))
        self.assertEqual(5, COMPONENTS_SQL.count("s.catalog_version = @version AND s.node_id = @node"))
        self.assertEqual(7, COMPONENTS_SQL.count("?"))
        for forbidden in ("@all", "@kind", "@code", "JOIN dbo.resolver_scope", "LIKE"):
            self.assertNotIn(forbidden, COMPONENTS_SQL)
        for binding in ("COUNT(*) FROM dbo.resolver_release", "search_index = @searchIndex",
                        "embedding_deployment = @embeddingDeployment", "embedding_dimensions = @embeddingDimensions"):
            self.assertIn(binding, COMPONENTS_SQL)

    def test_catalog_visibility_requires_caller_facts_not_public_dimensions(self):
        for required in ("INNER JOIN fact_finance_monthly f", "s.node_id = c.entity_id",
                         "s.catalog_version = c.catalog_version",
                         "f.region_code = s.region_code AND f.department_code = s.department_code"):
            self.assertIn(required, CATALOG_SQL)
        for forbidden in ("TOP", "LIKE", "dim_region", "dim_department"):
            self.assertNotIn(forbidden, CATALOG_SQL)

    def test_period_and_ratio_sql_semantics(self):
        for required in ("DECLARE @closing date = DATEADD(month, -1, @end)",
                         "h.month_start = @closing", "f.month_start >= @start AND f.month_start < @end",
                         "k.month_start >= DATEADD(year, -1, @start)",
                         "SUM(d.kpi_value * ISNULL(r.net_revenue, 0))"):
            self.assertIn(required, COMPONENTS_SQL)


class SqlTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tokens = Tokens()
        self.settings = Settings(sql_endpoint="fabric.example.test", database="finance;safe", sql_timeout_seconds=17)
        self.query = FabricStatementQuery(self.settings, self.tokens)
        self.period = FinancePeriodParser.parse("Q3 2026", date(2026, 9, 10))
        self.organization = OrganizationScope("org", "EMEA", "EMEA", "v1", RELEASE)

    async def test_batch_skips_non_row_results_before_reading_aggregates(self):
        class BatchCursor(Cursor):
            def __init__(self, connection, rows):
                super().__init__(connection, rows)
                self.description, self.advances = None, 0

            def nextset(self):
                self.advances += 1
                if self.advances == 2:
                    self.description = [("value",)]
                return True

        connection = Connection()
        cursor = BatchCursor(connection, [COMPONENT_ROW])
        with patch.object(connection, "cursor", return_value=cursor):
            rows = self.query._rows(connection, COMPONENTS_SQL, ("bound",),
                                    limit=1, cancelled=threading.Event())
        self.assertEqual([COMPONENT_ROW], rows)
        self.assertEqual(2, cursor.advances)
        self.assertTrue(cursor.closed)

    async def test_batch_without_row_result_fails_explicitly_and_closes_cursor(self):
        connection = Connection()
        cursor = Cursor(connection, [])
        cursor.description = None
        with patch.object(connection, "cursor", return_value=cursor), \
                patch.object(cursor, "nextset", return_value=False, create=True):
            with self.assertRaisesRegex(ValueError, "did not return a row result"):
                self.query._rows(connection, COMPONENTS_SQL, ("bound",),
                                 limit=1, cancelled=threading.Event())
        self.assertTrue(cursor.closed)

    async def test_batch_cancellation_prevents_advancing_to_another_result(self):
        cancelled = threading.Event()

        class CancelledCursor(Cursor):
            def execute(self, *args, **kwargs):
                super().execute(*args, **kwargs)
                self.description = None
                cancelled.set()
                return self

        connection = Connection()
        cursor = CancelledCursor(connection, [])
        with patch.object(connection, "cursor", return_value=cursor), \
                patch.object(cursor, "nextset", create=True) as advance:
            with self.assertRaises(InterruptedError):
                self.query._rows(connection, COMPONENTS_SQL, ("bound",),
                                 limit=1, cancelled=cancelled)
        advance.assert_not_called()
        self.assertTrue(cursor.closed)

    async def test_catalog_load_binds_release_and_user_token_on_every_connection(self):
        connections = [Connection([RELEASE_ROW]), Connection([CATALOG_ROW]), Connection([RELEASE_ROW])]
        with patch("mssql_python.connect", side_effect=connections) as connect:
            snapshot = await self.query.load_catalog()
        self.assertEqual(RELEASE, snapshot.release)
        self.assertEqual(("net sales",), snapshot.entities[0].aliases)
        self.assertEqual([(SQL_SCOPE,)] * 3, self.tokens.scopes)
        self.assertEqual(("v1",), connections[1].executions[0][1])
        self.assertTrue(connections[1].executions[0][2])
        for index, call in enumerate(connect.call_args_list, 1):
            self.assertEqual("finance;safe", call.kwargs["Database"])
            self.assertEqual("yes", call.kwargs["Encrypt"])
            self.assertEqual("no", call.kwargs["TrustServerCertificate"])
            self.assertEqual(17, call.kwargs["timeout"])
            self.assertEqual(token_attributes(f"delegated-caller-token-{index}"), call.kwargs["attrs_before"])
            self.assertNotIn("token_provider", call.kwargs)
            self.assertNotIn("Authentication", call.kwargs)
        self.assertTrue(all(connection.closed and connection.timeout == 17 for connection in connections))
        self.assertTrue(all(cursor.closed for connection in connections for cursor in connection.cursors))

    async def test_missing_multiple_or_invalid_releases_fail_closed(self):
        for rows in ([], [RELEASE_ROW, RELEASE_ROW], [("v1", "Bad Index", "model", 2)],
                     [(None, "resolver-v1", "model", 2)]):
            connection = Connection(rows)
            with patch("mssql_python.connect", return_value=connection):
                with self.assertRaises(ValueError):
                    await self.query.get_release()
            self.assertTrue(connection.closed)

    async def test_catalog_duplicate_ids_aliases_and_size_fail_closed(self):
        duplicate = ("kpi-003", *CATALOG_ROW[1:])
        invalid_aliases = (*CATALOG_ROW[:3], '{"a":"b"}', *CATALOG_ROW[4:])
        invalid_alias_items = (*CATALOG_ROW[:3], '[42]', *CATALOG_ROW[4:])
        for rows in ([CATALOG_ROW, duplicate], [invalid_aliases], [invalid_alias_items],
                     [CATALOG_ROW] * 20001):
            with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), Connection(rows)]):
                with self.assertRaises(ValueError):
                    await self.query.load_catalog()

    async def test_catalog_release_mutation_is_rejected(self):
        with patch("mssql_python.connect", side_effect=[
            Connection([RELEASE_ROW]), Connection([CATALOG_ROW]),
            Connection([("v1", "resolver-new", "embedding-model", 1536)]),
        ]):
            with self.assertRaises(CatalogChangedException):
                await self.query.load_catalog()

    async def test_current_previous_and_all_inputs_are_bound_parameters(self):
        scope = replace(self.organization, code="x'; DROP TABLE facts;--")
        connections = [Connection([RELEASE_ROW]), Connection([COMPONENT_ROW], [COMPONENT_ROW])]
        with patch("mssql_python.connect", side_effect=connections):
            result = await self.query.get_statement(KpiCatalog.by_code("KPI-006"), scope, self.period)
        self.assertEqual(D("55.56"), result.value)
        self.assertEqual(D("55.56"), result.prior_value)
        for sql, params, prepare in connections[1].executions:
            self.assertEqual(COMPONENTS_SQL, sql)
            self.assertNotIn(scope.code, sql)
            self.assertEqual(scope.code, params[3])
            self.assertEqual(("v1", scope.code, "resolver-v1", "embedding-model", 1536), params[2:])
            self.assertTrue(prepare)
        self.assertEqual((date(2026, 7, 1), date(2026, 10, 1)), connections[1].executions[0][1][:2])
        self.assertEqual((date(2026, 4, 1), date(2026, 7, 1)), connections[1].executions[1][1][:2])
        self.assertTrue(all(connection.closed for connection in connections))

    async def test_yoy_comparison_uses_same_period_previous_year(self):
        connection = Connection([COMPONENT_ROW], [COMPONENT_ROW])
        with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), connection]):
            result = await self.query.get_statement(KpiCatalog.by_code("KPI-017"), self.organization, self.period)
        self.assertEqual(D("20"), result.value)
        self.assertEqual((date(2025, 7, 1), date(2025, 10, 1)), connection.executions[1][1][:2])

    async def test_no_rows_and_nulls_are_safe(self):
        for row in ((), ((None, None, None, None, None, 0, None, None, None, None),)):
            connection = Connection(row, row)
            with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), connection]):
                result = await self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period)
            self.assertIsNone(result.value)

    async def test_fabric_float_aggregates_are_converted_before_decimal_calculation(self):
        row = (1000.1, 100.02, 400.10000000000002, 200.19999999999999,
               50.01, 10, 800.1000000000001, 10, 40500.123, 750.0199999999999)
        with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), Connection([row], [row])]):
            result = await self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period)
        self.assertIsInstance(result.value, D)
        self.assertEqual(D("900.08"), result.value)

    async def test_non_finite_or_non_numeric_aggregates_are_rejected(self):
        for invalid in (float("nan"), float("inf"), float("-inf"), D("NaN"), D("Infinity"), True, object()):
            with self.subTest(value=invalid):
                row = (invalid, *COMPONENT_ROW[1:])
                with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), Connection([row])]):
                    with self.assertRaises(ValueError):
                        await self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period)

    async def test_unbound_scope_never_opens_connection(self):
        with patch("mssql_python.connect") as connect:
            with self.assertRaises(CatalogChangedException):
                await self.query.get_statement(KpiCatalog.by_code("KPI-003"), replace(self.organization, release=None), self.period)
        connect.assert_not_called()
        self.assertFalse(self.tokens.scopes)

    async def test_changed_binding_or_sql_guard_error_never_returns_figures(self):
        with patch("mssql_python.connect", return_value=Connection([("v2", *RELEASE_ROW[1:])])):
            with self.assertRaises(CatalogChangedException):
                await self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period)
        failure = mssql_python.DatabaseError("SQL failed", "(51000) Resolver catalogue changed.")
        connection = Connection([], failure=failure)
        with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), connection]):
            with self.assertRaises(CatalogChangedException):
                await self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period)
        self.assertTrue(connection.closed)
        self.assertTrue(connection.cursors[0].closed)

    async def test_cancellation_before_worker_starts_never_opens_connection(self):
        class CancelledTokens:
            async def get_token(self, scopes):
                raise asyncio.CancelledError()
        with patch("mssql_python.connect") as connect:
            with self.assertRaises(asyncio.CancelledError):
                await FabricStatementQuery(self.settings, CancelledTokens()).get_release()
        connect.assert_not_called()

    async def test_cancellation_during_read_closes_cursor_and_prevents_prior_read(self):
        started, release, closed = threading.Event(), threading.Event(), threading.Event()

        class BlockingCursor(Cursor):
            def execute(self, *args, **kwargs):
                super().execute(*args, **kwargs)
                started.set()
                release.wait(5)
                return self

        class BlockingConnection(Connection):
            def cursor(self):
                cursor = BlockingCursor(self, self.resultsets.pop(0))
                self.cursors.append(cursor)
                return cursor

            def close(self):
                super().close()
                closed.set()

        connection = BlockingConnection([COMPONENT_ROW], [COMPONENT_ROW])
        with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), connection]):
            task = asyncio.create_task(self.query.get_statement(KpiCatalog.by_code("KPI-003"), self.organization, self.period))
            try:
                self.assertTrue(await asyncio.to_thread(started.wait, 5))
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
            finally:
                release.set()
            self.assertTrue(await asyncio.to_thread(closed.wait, 5))
        self.assertEqual(1, len(connection.executions))
        self.assertEqual(1, len(connection.resultsets))
        self.assertTrue(connection.cursors[0].closed)

    async def test_decimal_headcount_cast_matches_dotnet_and_overflow_fails(self):
        for headcount in (D("10.5"), "10.5", 10.5):
            row = (*COMPONENT_ROW[:7], headcount, *COMPONENT_ROW[8:])
            with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), Connection([row], [row])]):
                result = await self.query.get_statement(KpiCatalog.by_code("KPI-014"), self.organization, self.period)
            self.assertEqual(D(90), result.value)
        for headcount in (float("nan"), float("inf"), True, D(2 ** 31)):
            row = (*COMPONENT_ROW[:7], headcount, *COMPONENT_ROW[8:])
            with patch("mssql_python.connect", side_effect=[Connection([RELEASE_ROW]), Connection([row])]):
                with self.assertRaises(ValueError):
                    await self.query.get_statement(KpiCatalog.by_code("KPI-014"), self.organization, self.period)

    async def test_query_timeout_maps_to_safe_timeout_type(self):
        failure = mssql_python.OperationalError("SQL failed", "[HYT00] Query timeout expired")
        connection = Connection([], failure=failure)
        with patch("mssql_python.connect", return_value=connection):
            with self.assertRaises(TimeoutError):
                await self.query.get_release()
        self.assertTrue(connection.closed)
