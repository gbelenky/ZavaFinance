// Copyright (c) Microsoft Corporation.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Finance;

/// <summary>
/// Queries the Fabric lakehouse SQL analytics endpoint as the signed-in user.
/// <para>
/// Every statement is a parameterised query executed with a **delegated** token, so row-level
/// security and workspace permissions apply to the caller. An app-only token would query as the
/// service principal, RLS would not filter per user, and every caller would see the same data —
/// which would defeat the entire per-user isolation model.
/// </para>
/// <para>
/// The SQL only ever sums additive components. Ratios are recomputed from those sums by
/// <see cref="KpiCalculator"/>, so a summed or averaged ratio is not expressible here.
/// </para>
/// </summary>
public sealed class FabricStatementQuery : IStatementQuery, IResolverCatalog
{
    private readonly FabricOptions _options;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly ILogger _logger;

    public FabricStatementQuery(
        FabricOptions options,
        Func<CancellationToken, Task<string>> tokenProvider,
        ILogger logger)
    {
        _options = options;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<ResolverRelease> GetReleaseAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT catalog_version, search_index, embedding_deployment, embedding_dimensions FROM dbo.resolver_release;",
            connection)
        {
            CommandTimeout = (int)_options.SqlTimeout.TotalSeconds
        };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || Enumerable.Range(0, 4).Any(reader.IsDBNull))
            throw new InvalidDataException("No published resolver catalogue.");
        var release = new ResolverRelease(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetInt32(3));
        release.Validate();
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Resolver release must contain exactly one version.");
        return release;
    }

    public async Task<ResolverCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        ResolverRelease release = await GetReleaseAsync(cancellationToken);
        string version = release.CatalogVersion;
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(CatalogSql, connection)
        {
            CommandTimeout = (int)_options.SqlTimeout.TotalSeconds
        };
        command.Parameters.Add("@version", SqlDbType.NVarChar, 64).Value = version;
        var entities = new List<ResolverEntity>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (entities.Count >= 20000)
                throw new InvalidDataException("Resolver catalogue exceeds the supported size.");
            entities.Add(new ResolverEntity(version, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
                Optional(reader, 4) ?? "", Optional(reader, 5), Optional(reader, 6) ?? "",
                Optional(reader, 7), Optional(reader, 8), Optional(reader, 9), Optional(reader, 10),
                reader.GetBoolean(11)));
        }
        if (entities.Select(entity => entity.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entities.Count)
            throw new InvalidDataException("Duplicate resolver catalogue IDs.");
        if (release != await GetReleaseAsync(cancellationToken))
            throw new CatalogChangedException();
        return new ResolverCatalog(release, entities);
    }

    internal const string CatalogSql =
        """
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
        """;

    private static string? Optional(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    public async Task<StatementResult> GetStatementAsync(
        KpiDefinition kpi,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken)
    {
        if (organization.Release is null || organization.CatalogVersion != organization.Release.CatalogVersion
            || organization.Release != await GetReleaseAsync(cancellationToken))
            throw new CatalogChangedException();
        await using SqlConnection connection = await OpenAsync(cancellationToken);

        FinanceComponents current =
            await LoadComponentsAsync(connection, organization, period, cancellationToken);

        // The comparison is the prior year for a growth KPI and the preceding period otherwise,
        // which is what "versus prior" means for each.
        FinancePeriod comparison = kpi.Code == "KPI-017"
            ? period.PriorYear()
            : period.PriorPeriod();

        FinanceComponents prior =
            await LoadComponentsAsync(connection, organization, comparison, cancellationToken);

        _logger.LogInformation(
            "get_statement query complete. Kpi={Kpi} Scope={Scope} Months={Months} Rows={Rows}",
            kpi.Code,
            organization.Kind,
            period.MonthCount,
            current.HasRows);

        return new StatementResult(
            kpi,
            organization,
            period,
            KpiCalculator.Compute(kpi, current),
            KpiCalculator.Compute(kpi, prior),
            "USD");
    }

    // Amounts are positive magnitudes; ratios are recomputed outside the query.
    internal const string ComponentsSql =
            """
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
            -- Headcount is semi-additive: summed across organizations, never across months.
            headcount AS (
                SELECT SUM(h.headcount_fte) AS closing_headcount
                FROM fact_headcount_monthly h
                WHERE h.month_start = @closing
                  AND EXISTS (SELECT 1 FROM dbo.resolver_scope s
                      WHERE s.catalog_version = @version AND s.node_id = @node
                        AND s.region_code = h.region_code AND s.department_code = h.department_code)
            ),
            -- Receivables are not stored, so DSO aggregates as a revenue-weighted average.
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
            CROSS JOIN sply s
            """;

    private async Task<FinanceComponents> LoadComponentsAsync(
        SqlConnection connection,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(ComponentsSql, connection)
        {
            CommandTimeout = (int)_options.SqlTimeout.TotalSeconds
        };

        command.Parameters.Add("@start", SqlDbType.Date).Value = period.Start.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@end", SqlDbType.Date).Value = period.End.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@version", SqlDbType.NVarChar, 64).Value = organization.CatalogVersion;
        command.Parameters.Add("@node", SqlDbType.NVarChar, 200).Value = organization.Code;
        command.Parameters.Add("@searchIndex", SqlDbType.NVarChar, 128).Value = organization.Release!.SearchIndex;
        command.Parameters.Add("@embeddingDeployment", SqlDbType.NVarChar, 64).Value = organization.Release.EmbeddingDeployment;
        command.Parameters.Add("@embeddingDimensions", SqlDbType.Int).Value = organization.Release.EmbeddingDimensions;

        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 51000)
        {
            throw new CatalogChangedException();
        }
        await using (reader)
        {
            if (!await reader.ReadAsync(cancellationToken))
                return new FinanceComponents { HasRows = false };

            return new FinanceComponents
            {
                GrossRevenue = Money(reader, 0),
                RevenueDeductions = Money(reader, 1),
                Cogs = Money(reader, 2),
                OperatingExpenses = Money(reader, 3),
                DepreciationAmortisation = Money(reader, 4),
                HasRows = !reader.IsDBNull(5) && reader.GetInt32(5) > 0,
                BudgetNetRevenue = Money(reader, 6),
                ClosingHeadcount = reader.IsDBNull(7) ? 0 : (int)Convert.ToDecimal(reader.GetValue(7)),
                DsoWeighted = Money(reader, 8),
                NetRevenuePriorYear = reader.IsDBNull(9) ? null : Money(reader, 9)
            };
        }
    }

    private static decimal Money(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(
            $"Server={_options.SqlEndpoint};Database={_options.Database};"
            + "Encrypt=True;TrustServerCertificate=False;")
        {
            // Acquired late and never cached by us, so the SDK can refresh transparently.
            AccessToken = await _tokenProvider(cancellationToken)
        };

        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}
