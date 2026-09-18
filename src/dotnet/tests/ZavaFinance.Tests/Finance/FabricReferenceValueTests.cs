// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

/// <summary>
/// Pins the aggregation arithmetic against real Zava figures.
/// <para>
/// The component sums below were computed directly from the parquet tables that were written
/// to the lakehouse (see <c>tools/reference_values.py</c>), so these are the numbers the SQL
/// must reproduce. They exist because the SQL analytics endpoint is TDS-only and outbound 1433
/// is blocked on the development network: the query cannot be executed locally, but the
/// arithmetic applied to its results can still be gated in CI.
/// </para>
/// <para>
/// This is the "assert exact values" half of the phase 2 requirement. The remaining half —
/// that the SQL returns these component sums — has to be verified from a host with 1433 egress.
/// </para>
/// </summary>
public sealed class FabricReferenceValueTests
{
    private static KpiDefinition Kpi(string code) => KpiCatalog.All.Single(k => k.Code == code);

    /// <summary>EMEA, November 2025.</summary>
    private static FinanceComponents EmeaNovember2025 => new()
    {
        HasRows = true,
        GrossRevenue = 93_701_276.73m,
        RevenueDeductions = 8_061_714.36m,
        Cogs = 49_228_405.15m,
        OperatingExpenses = 58_917_933.94m,
        DepreciationAmortisation = 5_071_299.87m,
        BudgetNetRevenue = 81_837_576.77m,
        ClosingHeadcount = 3258
    };

    /// <summary>EMEA, Q4 2025 — three months aggregated.</summary>
    private static FinanceComponents EmeaQ42025 => new()
    {
        HasRows = true,
        GrossRevenue = 271_042_492.51m,
        RevenueDeductions = 23_404_710.85m,
        Cogs = 141_976_212.38m,
        OperatingExpenses = 176_468_910.16m,
        DepreciationAmortisation = 15_213_992.99m,
        BudgetNetRevenue = 238_146_669.84m,
        ClosingHeadcount = 3247
    };

    /// <summary>Whole company, calendar 2025 — twelve months, all regions.</summary>
    private static FinanceComponents All2025 => new()
    {
        HasRows = true,
        GrossRevenue = 3_867_705_621.71m,
        RevenueDeductions = 328_817_052.89m,
        Cogs = 2_036_324_109.80m,
        OperatingExpenses = 2_378_696_906.37m,
        DepreciationAmortisation = 203_889_124.44m,
        BudgetNetRevenue = 3_486_153_099.26m,
        ClosingHeadcount = 11144
    };

    [Fact]
    public void EmeaNovember2025MatchesTheLakehouse()
    {
        FinanceComponents c = EmeaNovember2025;

        Assert.Equal(85_639_562.37m, c.NetRevenue);
        Assert.Equal(36_411_157.22m, c.GrossProfit);
        Assert.Equal(-22_506_776.72m, c.Ebitda);
        Assert.Equal(-27_578_076.59m, c.OperatingIncome);

        Assert.Equal(42.52m, KpiCalculator.Compute(Kpi("KPI-006"), c));   // gross margin %
        Assert.Equal(-32.20m, KpiCalculator.Compute(Kpi("KPI-012"), c));  // operating margin %
        Assert.Equal(26_285.93m, KpiCalculator.Compute(Kpi("KPI-014"), c)); // revenue per FTE
        Assert.Equal(3258m, KpiCalculator.Compute(Kpi("KPI-013"), c));    // headcount
    }

    [Fact]
    public void EmeaQ42025MatchesTheLakehouse()
    {
        FinanceComponents c = EmeaQ42025;

        Assert.Equal(247_637_781.66m, c.NetRevenue);
        Assert.Equal(42.67m, KpiCalculator.Compute(Kpi("KPI-006"), c));
        Assert.Equal(-34.74m, KpiCalculator.Compute(Kpi("KPI-012"), c));

        // Closing month, not the sum of three months. Summing would give roughly 9,750.
        Assert.Equal(3247m, KpiCalculator.Compute(Kpi("KPI-013"), c));
        Assert.Equal(76_266.64m, KpiCalculator.Compute(Kpi("KPI-014"), c));
    }

    [Fact]
    public void WholeCompany2025MatchesTheLakehouse()
    {
        FinanceComponents c = All2025;

        Assert.Equal(3_538_888_568.82m, c.NetRevenue);
        Assert.Equal(42.46m, KpiCalculator.Compute(Kpi("KPI-006"), c));
        Assert.Equal(-30.52m, KpiCalculator.Compute(Kpi("KPI-012"), c));
        Assert.Equal(11_144m, KpiCalculator.Compute(Kpi("KPI-013"), c));
        Assert.Equal(317_559.99m, KpiCalculator.Compute(Kpi("KPI-014"), c));
    }

    [Fact]
    public void BudgetVarianceMatchesTheLakehouse()
    {
        // (85,639,562.37 - 81,837,576.77) / 81,837,576.77
        Assert.Equal(4.65m, KpiCalculator.Compute(Kpi("KPI-016"), EmeaNovember2025));
    }

    [Fact]
    public void QuarterlyMarginIsNotTheAverageOfItsMonths()
    {
        // EMEA Q4 2025 monthly gross margins were 43.21, 42.52 and 42.31, averaging 42.68,
        // while the correct component-derived figure is 42.67. The gap is small *here* only
        // because the three months are of similar size; it widens sharply across organizations
        // of different scale, and for per-FTE measures. The rule holds regardless: the value is
        // derived from summed components rather than from other periods' ratios.
        decimal correct = KpiCalculator.Compute(Kpi("KPI-006"), EmeaQ42025)!.Value;
        decimal naiveAverage = Math.Round((43.21m + 42.52m + 42.31m) / 3m, 2);

        Assert.Equal(42.67m, correct);
        Assert.Equal(42.68m, naiveAverage);
        Assert.NotEqual(naiveAverage, correct);
    }

    [Fact]
    public void RevenuePerFteIsNotAdditiveAcrossPeriods()
    {
        // A quarter's revenue per FTE (76,266.64) is roughly three times a single month's
        // (26,285.93) because revenue sums while headcount does not. Treating the measure as
        // additive, or reusing a monthly figure for a quarter, is wrong by a factor of three.
        decimal month = KpiCalculator.Compute(Kpi("KPI-014"), EmeaNovember2025)!.Value;
        decimal quarter = KpiCalculator.Compute(Kpi("KPI-014"), EmeaQ42025)!.Value;

        Assert.True(quarter > month * 2.5m);
    }
}
