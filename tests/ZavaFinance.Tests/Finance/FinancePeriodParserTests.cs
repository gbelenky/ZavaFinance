// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests.Finance;

public sealed class FinancePeriodParserTests
{
    // Fixed so relative phrases are deterministic, exactly as the durable path requires.
    private static readonly DateOnly Today = new(2026, 9, 10);

    private static FinancePeriod Parse(string text)
    {
        FinancePeriod? period = FinancePeriodParser.Parse(text, Today);

        Assert.NotNull(period);

        return period;
    }

    [Theory]
    [InlineData("Q3 2026", 2026, 7, 2026, 10)]
    [InlineData("q1 2025", 2025, 1, 2025, 4)]
    [InlineData("2026 Q4", 2026, 10, 2027, 1)]
    [InlineData("Q2 FY2026", 2026, 4, 2026, 7)]
    public void ParsesQuarters(string text, int sy, int sm, int ey, int em)
    {
        FinancePeriod period = Parse(text);

        Assert.Equal(new DateOnly(sy, sm, 1), period.Start);
        Assert.Equal(new DateOnly(ey, em, 1), period.End);
        Assert.Equal(3, period.MonthCount);
    }

    [Theory]
    [InlineData("November 2025", 2025, 11)]
    [InlineData("nov 2025", 2025, 11)]
    [InlineData("January 2026", 2026, 1)]
    public void ParsesSingleMonths(string text, int year, int month)
    {
        FinancePeriod period = Parse(text);

        Assert.Equal(new DateOnly(year, month, 1), period.Start);
        Assert.Equal(1, period.MonthCount);
    }

    [Fact]
    public void ParsesExplicitMonthRanges()
    {
        FinancePeriod period = Parse("January to March 2026");

        Assert.Equal(new DateOnly(2026, 1, 1), period.Start);
        Assert.Equal(new DateOnly(2026, 4, 1), period.End);
        Assert.Equal(3, period.MonthCount);
    }

    [Fact]
    public void ParsesCrossYearMonthRanges()
    {
        FinancePeriod period = Parse("November 2025 to February 2026");

        Assert.Equal(new DateOnly(2025, 11, 1), period.Start);
        Assert.Equal(new DateOnly(2026, 3, 1), period.End);
        Assert.Equal(4, period.MonthCount);
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("FY2026")]
    public void ParsesFullYears(string text)
    {
        FinancePeriod period = Parse(text);

        Assert.Equal(new DateOnly(2026, 1, 1), period.Start);
        Assert.Equal(12, period.MonthCount);
    }

    [Fact]
    public void ParsesHalves()
    {
        FinancePeriod period = Parse("H1 2026");

        Assert.Equal(new DateOnly(2026, 1, 1), period.Start);
        Assert.Equal(6, period.MonthCount);
    }

    [Theory]
    [InlineData("last quarter", 2026, 4, 3)]
    [InlineData("this quarter", 2026, 7, 3)]
    [InlineData("last month", 2026, 8, 1)]
    [InlineData("this month", 2026, 9, 1)]
    [InlineData("last year", 2025, 1, 12)]
    public void ParsesRelativePeriodsAgainstSuppliedToday(
        string text, int year, int month, int months)
    {
        FinancePeriod period = Parse(text);

        Assert.Equal(new DateOnly(year, month, 1), period.Start);
        Assert.Equal(months, period.MonthCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sometime soon")]
    [InlineData("the good old days")]
    public void RejectsUnparseableText(string text)
    {
        // The router is told never to invent arguments, so an unparseable period becomes a
        // clarifying question rather than a silently defaulted one.
        Assert.Null(FinancePeriodParser.Parse(text, Today));
    }

    [Fact]
    public void ClosingMonthIsTheLastMonthInTheRange()
    {
        // Semi-additive KPIs such as headcount take this month rather than a sum.
        FinancePeriod period = Parse("Q3 2026");

        Assert.Equal(new DateOnly(2026, 9, 1), period.ClosingMonth);
    }

    [Fact]
    public void PriorYearShiftsByTwelveMonths()
    {
        FinancePeriod prior = Parse("Q3 2026").PriorYear();

        Assert.Equal(new DateOnly(2025, 7, 1), prior.Start);
        Assert.Equal(new DateOnly(2025, 10, 1), prior.End);
    }

    [Fact]
    public void PriorPeriodIsTheImmediatelyPrecedingRangeOfEqualLength()
    {
        FinancePeriod prior = Parse("Q3 2026").PriorPeriod();

        Assert.Equal(new DateOnly(2026, 4, 1), prior.Start);
        Assert.Equal(new DateOnly(2026, 7, 1), prior.End);
        Assert.Equal(3, prior.MonthCount);
    }
}
