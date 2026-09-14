// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

/// <summary>
/// The aggregation rules, which are where a finance agent is most likely to be quietly wrong.
/// <para>
/// These are pure-function tests over summed components, so they need no database — which is
/// what makes them runnable everywhere, including where the SQL analytics endpoint is only
/// reachable over TDS from inside the network.
/// </para>
/// </summary>
public sealed class KpiCalculatorTests
{
    private static FinanceComponents Components(
        decimal grossRevenue = 1000m,
        decimal deductions = 100m,
        decimal cogs = 400m,
        decimal opex = 200m,
        decimal da = 50m,
        int headcount = 10,
        decimal budget = 800m,
        decimal? priorYearNetRevenue = 750m,
        decimal dsoWeighted = 0m) =>
        new()
        {
            HasRows = true,
            GrossRevenue = grossRevenue,
            RevenueDeductions = deductions,
            Cogs = cogs,
            OperatingExpenses = opex,
            DepreciationAmortisation = da,
            ClosingHeadcount = headcount,
            BudgetNetRevenue = budget,
            NetRevenuePriorYear = priorYearNetRevenue,
            DsoWeighted = dsoWeighted
        };

    private static KpiDefinition Kpi(string code) => KpiCatalog.All.Single(k => k.Code == code);

    [Fact]
    public void DerivedAdditiveKpisFollowTheStatedFormulas()
    {
        FinanceComponents c = Components();

        Assert.Equal(900m, c.NetRevenue);        // 1000 - 100
        Assert.Equal(500m, c.GrossProfit);       // 900 - 400
        Assert.Equal(300m, c.Ebitda);            // 500 - 200
        Assert.Equal(250m, c.OperatingIncome);   // 300 - 50
    }

    [Theory]
    [InlineData("KPI-006", 55.56)]   // gross profit 500 / net revenue 900
    [InlineData("KPI-009", 33.33)]   // ebitda 300 / 900
    [InlineData("KPI-012", 27.78)]   // operating income 250 / 900
    public void RatioKpisAreRecomputedFromSummedComponents(string code, double expected)
    {
        decimal? value = KpiCalculator.Compute(Kpi(code), Components());

        Assert.Equal((decimal)expected, value!.Value, 2);
    }

    [Fact]
    public void SummingMonthlyRatiosWouldBeWrongSoComponentsAreSummedInstead()
    {
        // Two months with very different revenue. Averaging the monthly margins is wrong; the
        // correct aggregate margin is implicitly revenue-weighted because the components sum.
        FinanceComponents january = Components(grossRevenue: 100m, deductions: 0m, cogs: 20m);
        FinanceComponents december = Components(grossRevenue: 1000m, deductions: 0m, cogs: 700m);

        decimal januaryMargin = KpiCalculator.Compute(Kpi("KPI-006"), january)!.Value;
        decimal decemberMargin = KpiCalculator.Compute(Kpi("KPI-006"), december)!.Value;
        decimal naiveAverage = (januaryMargin + decemberMargin) / 2m;

        FinanceComponents combined = Components(grossRevenue: 1100m, deductions: 0m, cogs: 720m);
        decimal correct = KpiCalculator.Compute(Kpi("KPI-006"), combined)!.Value;

        Assert.Equal(80m, januaryMargin);
        Assert.Equal(30m, decemberMargin);
        Assert.Equal(55m, naiveAverage);
        Assert.Equal(34.55m, correct, 2);

        // Twenty points apart on this small example; the real dataset diverges much further.
        // This is why a ratio is never summed or averaged.
        Assert.True(Math.Abs(naiveAverage - correct) > 20m);
    }

    [Fact]
    public void HeadcountUsesTheClosingMonthRatherThanASumAcrossMonths()
    {
        decimal? value = KpiCalculator.Compute(Kpi("KPI-013"), Components(headcount: 42));

        Assert.Equal(42m, value);
    }

    [Fact]
    public void RevenuePerFteDividesByClosingHeadcount()
    {
        decimal? value = KpiCalculator.Compute(Kpi("KPI-014"), Components(headcount: 9));

        Assert.Equal(100m, value);   // net revenue 900 / 9
    }

    [Fact]
    public void BudgetVarianceIsRecomputedFromSummedActualAndBudget()
    {
        decimal? value = KpiCalculator.Compute(Kpi("KPI-016"), Components(budget: 800m));

        Assert.Equal(12.5m, value);  // (900 - 800) / 800
    }

    [Fact]
    public void YearOnYearGrowthUsesThePriorYearTotal()
    {
        decimal? value = KpiCalculator.Compute(Kpi("KPI-017"), Components(priorYearNetRevenue: 750m));

        Assert.Equal(20m, value);    // (900 - 750) / 750
    }

    [Fact]
    public void YearOnYearGrowthIsUnavailableWithoutAPriorYear()
    {
        // Unavailable rather than zero growth, which would be a wrong number.
        decimal? value = KpiCalculator.Compute(Kpi("KPI-017"), Components(priorYearNetRevenue: null));

        Assert.Null(value);
    }

    [Fact]
    public void DaysSalesOutstandingIsRevenueWeightedNotAveraged()
    {
        FinanceComponents c = Components(dsoWeighted: 900m * 45m);

        Assert.Equal(45m, KpiCalculator.Compute(Kpi("KPI-015"), c));
    }

    [Fact]
    public void ZeroDenominatorsReportUnavailableRatherThanZero()
    {
        FinanceComponents noRevenue = Components(grossRevenue: 0m, deductions: 0m);

        Assert.Null(KpiCalculator.Compute(Kpi("KPI-006"), noRevenue));
        Assert.Null(KpiCalculator.Compute(Kpi("KPI-014"), Components(headcount: 0)));
        Assert.Null(KpiCalculator.Compute(Kpi("KPI-016"), Components(budget: 0m)));
    }

    [Fact]
    public void MissingRowsReportUnavailableForEveryKpi()
    {
        var empty = new FinanceComponents { HasRows = false };

        foreach (KpiDefinition kpi in KpiCatalog.All)
        {
            Assert.Null(KpiCalculator.Compute(kpi, empty));
        }
    }

    [Fact]
    public void EveryCatalogKpiIsComputable()
    {
        // Guards against adding a KPI to the catalogue without teaching the calculator how to
        // aggregate it, which would silently report "no data".
        foreach (KpiDefinition kpi in KpiCatalog.All)
        {
            Assert.NotNull(KpiCalculator.Compute(kpi, Components()));
        }
    }
}
