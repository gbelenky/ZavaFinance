// Copyright (c) Microsoft Corporation.

using System.Data;
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
public sealed class FabricStatementQuery : IStatementQuery
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

    public async Task<OrganizationScope?> ResolveOrganizationAsync(
        string org, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(org))
        {
            return null;
        }

        if (IsWholeCompany(org))
        {
            return OrganizationScope.Whole;
        }

        const string Sql =
            """
            SELECT TOP (1) kind, code, name FROM (
                SELECT 'region' AS kind, region_code AS code, region_name AS name,
                       CASE WHEN LOWER(region_code) = @needle THEN 0
                            WHEN LOWER(region_name) = @needle THEN 1
                            ELSE 2 END AS rank
                FROM dim_region
                WHERE LOWER(region_code) = @needle
                   OR LOWER(region_name) = @needle
                   OR LOWER(region_name) LIKE @like
                UNION ALL
                SELECT 'department', department_code, department_name,
                       CASE WHEN LOWER(department_code) = @needle THEN 0
                            WHEN LOWER(department_name) = @needle THEN 1
                            ELSE 3 END
                FROM dim_department
                WHERE LOWER(department_code) = @needle
                   OR LOWER(department_name) = @needle
                   OR LOWER(department_name) LIKE @like
            ) matches
            ORDER BY rank, LEN(name)
            """;

        string needle = org.Trim().ToLowerInvariant();

        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Sql, connection)
        {
            CommandTimeout = (int)_options.SqlTimeout.TotalSeconds
        };

        command.Parameters.Add("@needle", SqlDbType.NVarChar, 200).Value = needle;
        command.Parameters.Add("@like", SqlDbType.NVarChar, 210).Value = $"%{needle}%";

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new OrganizationScope(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<StatementResult> GetStatementAsync(
        KpiDefinition kpi,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken)
    {
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

    private async Task<FinanceComponents> LoadComponentsAsync(
        SqlConnection connection,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken)
    {
        // Amounts are stored as positive magnitudes per component, so the KPI semantics are
        // applied when the components are combined, never in the SUM itself.
        const string Sql =
            """
            DECLARE @closing date = DATEADD(month, -1, @end);

            WITH scoped AS (
                SELECT f.kpi_component, f.amount_usd
                FROM fact_finance_monthly f
                WHERE f.month_start >= @start AND f.month_start < @end
                  AND (@all = 1
                       OR (@kind = 'region' AND f.region_code = @code)
                       OR (@kind = 'department' AND f.department_code = @code))
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
                  AND (@all = 1
                       OR (@kind = 'region' AND k.region_code = @code)
                       OR (@kind = 'department' AND k.department_code = @code))
            ),
            -- Headcount is semi-additive: summed across organizations, never across months.
            headcount AS (
                SELECT SUM(h.headcount_fte) AS closing_headcount
                FROM fact_headcount_monthly h
                WHERE h.month_start = @closing
                  AND (@all = 1
                       OR (@kind = 'region' AND h.region_code = @code)
                       OR (@kind = 'department' AND h.department_code = @code))
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
                  AND (@all = 1
                       OR (@kind = 'region' AND d.region_code = @code)
                       OR (@kind = 'department' AND d.department_code = @code))
            ),
            sply AS (
                SELECT SUM(k.kpi_value) AS net_revenue_sply
                FROM fact_kpi_monthly k
                WHERE k.kpi_code = 'KPI-003'
                  AND k.month_start >= DATEADD(year, -1, @start)
                  AND k.month_start < DATEADD(year, -1, @end)
                  AND (@all = 1
                       OR (@kind = 'region' AND k.region_code = @code)
                       OR (@kind = 'department' AND k.department_code = @code))
            )
            SELECT c.gross_revenue, c.revenue_deductions, c.cogs, c.opex, c.da, c.row_count,
                   b.budget_net_revenue, h.closing_headcount, d.dso_weighted, s.net_revenue_sply
            FROM components c
            CROSS JOIN budget b
            CROSS JOIN headcount h
            CROSS JOIN dso d
            CROSS JOIN sply s
            """;

        await using var command = new SqlCommand(Sql, connection)
        {
            CommandTimeout = (int)_options.SqlTimeout.TotalSeconds
        };

        bool all = organization.Kind == OrganizationScope.Company;

        command.Parameters.Add("@start", SqlDbType.Date).Value = period.Start.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@end", SqlDbType.Date).Value = period.End.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@all", SqlDbType.Bit).Value = all;
        command.Parameters.Add("@kind", SqlDbType.NVarChar, 20).Value = organization.Kind;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = organization.Code;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new FinanceComponents { HasRows = false };
        }

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

    private static decimal Money(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));

    private static bool IsWholeCompany(string org)
    {
        string value = org.Trim().ToLowerInvariant();

        return value is "zava" or "all" or "group" or "company" or "total"
            or "worldwide" or "global" or "all regions" or "whole company";
    }

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
