// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

public sealed class KpiCatalogTests
{
    [Theory]
    [InlineData("EBIT", "KPI-011")]
    [InlineData("ebit", "KPI-011")]
    [InlineData("Operating Income (EBIT)", "KPI-011")]
    [InlineData("allocated income", "KPI-011")]
    [InlineData("net revenue", "KPI-003")]
    [InlineData("net revenues", "KPI-003")]
    [InlineData("Net Revenue", "KPI-003")]
    [InlineData("gross margin", "KPI-006")]
    [InlineData("Gross Margin %", "KPI-006")]
    [InlineData("operating margin", "KPI-012")]
    [InlineData("EBITDA", "KPI-008")]
    [InlineData("ebitda margin", "KPI-009")]
    [InlineData("headcount", "KPI-013")]
    [InlineData("FTE", "KPI-013")]
    [InlineData("revenue per FTE", "KPI-014")]
    [InlineData("DSO", "KPI-015")]
    [InlineData("days sales outstanding", "KPI-015")]
    [InlineData("COGS", "KPI-004")]
    [InlineData("cost of goods sold", "KPI-004")]
    [InlineData("opex", "KPI-007")]
    [InlineData("KPI-003", "KPI-003")]
    public void ResolvesThePhrasingsTheRouterProduces(string text, string expected)
    {
        KpiDefinition? kpi = KpiCatalog.Resolve(text);

        Assert.NotNull(kpi);
        Assert.Equal(expected, kpi.Code);
    }

    [Fact]
    public void GrossMarginIsNotCapturedByGrossRevenue()
    {
        // Substring matching alone would let "gross margin" resolve to "Gross Revenue"; the
        // longest-name rule is what prevents a silently wrong KPI.
        Assert.Equal("KPI-006", KpiCatalog.Resolve("gross margin")!.Code);
        Assert.Equal("KPI-001", KpiCatalog.Resolve("gross revenue")!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the vibes")]
    public void ReturnsNullForUnknownText(string? text)
    {
        Assert.Null(KpiCatalog.Resolve(text));
    }

    [Fact]
    public void CodesAreUnique()
    {
        Assert.Equal(
            KpiCatalog.All.Count,
            KpiCatalog.All.Select(k => k.Code).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RatioKpisAreNotMarkedAdditive()
    {
        // The classification drives the aggregation, so a mislabelled ratio would be summed.
        foreach (string code in new[] { "KPI-006", "KPI-009", "KPI-012", "KPI-014", "KPI-015", "KPI-016", "KPI-017" })
        {
            Assert.Equal(
                KpiAggregation.Ratio,
                KpiCatalog.All.Single(k => k.Code == code).Aggregation);
        }
    }

    [Fact]
    public void HeadcountIsSemiAdditive()
    {
        Assert.Equal(
            KpiAggregation.SemiAdditive,
            KpiCatalog.All.Single(k => k.Code == "KPI-013").Aggregation);
    }

    [Fact]
    public void PercentKpisAreClassifiedAsPercentUnits()
    {
        // Drives "percentage points" rather than "percent change of a percent" in the output.
        foreach (string code in new[] { "KPI-006", "KPI-009", "KPI-012", "KPI-016", "KPI-017" })
        {
            Assert.Equal(KpiUnit.Percent, KpiCatalog.All.Single(k => k.Code == code).Unit);
        }
    }
}
