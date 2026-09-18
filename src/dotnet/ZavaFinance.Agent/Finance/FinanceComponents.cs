// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Finance;

/// <summary>
/// The additive building blocks for one organization and period, already summed.
/// <para>
/// Only additive quantities are ever summed. Every ratio KPI is derived from these totals, so
/// it is impossible for a ratio to be summed or averaged by accident — the shape of this type
/// is the guardrail. Naive aggregation of monthly departmental margins is wrong by over 100
/// percentage points on this dataset.
/// </para>
/// </summary>
public sealed record FinanceComponents
{
    public decimal GrossRevenue { get; init; }

    public decimal RevenueDeductions { get; init; }

    public decimal Cogs { get; init; }

    public decimal OperatingExpenses { get; init; }

    public decimal DepreciationAmortisation { get; init; }

    public decimal BudgetNetRevenue { get; init; }

    /// <summary>Closing-month FTE. Headcount never sums across months.</summary>
    public int ClosingHeadcount { get; init; }

    /// <summary>Net revenue for the same period one year earlier, for YoY growth.</summary>
    public decimal? NetRevenuePriorYear { get; init; }

    /// <summary>
    /// Revenue-weighted numerator for DSO. Receivables are not stored, so a revenue-weighted
    /// average of the monthly values is the defensible aggregation; a plain average would
    /// over-weight small months.
    /// </summary>
    public decimal DsoWeighted { get; init; }

    public bool HasRows { get; init; }

    public decimal NetRevenue => GrossRevenue - RevenueDeductions;

    public decimal GrossProfit => NetRevenue - Cogs;

    public decimal Ebitda => GrossProfit - OperatingExpenses;

    public decimal OperatingIncome => Ebitda - DepreciationAmortisation;
}

/// <summary>
/// Derives a KPI value from summed components.
/// <para>
/// This is the single place the aggregation rules live. It is a pure function, so the rules are
/// unit-testable without a database — which matters because the SQL analytics endpoint is only
/// reachable over TDS from inside the network.
/// </para>
/// </summary>
public static class KpiCalculator
{
    /// <summary>
    /// Returns the KPI value, or <c>null</c> when the denominator is zero or the underlying
    /// data is missing. A null is reported to the user as "not available", never as zero: a
    /// fabricated zero is a wrong number.
    /// </summary>
    public static decimal? Compute(KpiDefinition kpi, FinanceComponents c)
    {
        if (!c.HasRows)
        {
            return null;
        }

        return kpi.Code switch
        {
            "KPI-001" => c.GrossRevenue,
            "KPI-002" => c.RevenueDeductions,
            "KPI-003" => c.NetRevenue,
            "KPI-004" => c.Cogs,
            "KPI-005" => c.GrossProfit,
            "KPI-006" => Percent(c.GrossProfit, c.NetRevenue),
            "KPI-007" => c.OperatingExpenses,
            "KPI-008" => c.Ebitda,
            "KPI-009" => Percent(c.Ebitda, c.NetRevenue),
            "KPI-010" => c.DepreciationAmortisation,
            "KPI-011" => c.OperatingIncome,
            "KPI-012" => Percent(c.OperatingIncome, c.NetRevenue),
            "KPI-013" => c.ClosingHeadcount,
            "KPI-014" => Divide(c.NetRevenue, c.ClosingHeadcount),
            "KPI-015" => Divide(c.DsoWeighted, c.NetRevenue),
            "KPI-016" => Percent(c.NetRevenue - c.BudgetNetRevenue, c.BudgetNetRevenue),
            "KPI-017" => c.NetRevenuePriorYear is null
                ? null
                : Percent(c.NetRevenue - c.NetRevenuePriorYear.Value, c.NetRevenuePriorYear.Value),
            "KPI-018" => c.BudgetNetRevenue,
            _ => null
        };
    }

    private static decimal? Percent(decimal numerator, decimal denominator) =>
        denominator == 0 ? null : Math.Round(numerator / denominator * 100m, 2);

    private static decimal? Divide(decimal numerator, decimal denominator) =>
        denominator == 0 ? null : Math.Round(numerator / denominator, 2);
}
