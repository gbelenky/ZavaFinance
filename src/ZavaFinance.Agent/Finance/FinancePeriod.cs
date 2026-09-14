// Copyright (c) Microsoft Corporation.

using System.Globalization;
using System.Text.RegularExpressions;

namespace ZavaFinance.Core.Finance;

/// <summary>
/// A resolved reporting period as a half-open month range, <c>[Start, End)</c>.
/// </summary>
public sealed record FinancePeriod(DateOnly Start, DateOnly End, string Label)
{
    /// <summary>Inclusive first month of the period.</summary>
    public DateOnly FirstMonth => Start;

    /// <summary>
    /// Inclusive last month. Semi-additive KPIs such as headcount take this month rather than a
    /// sum across the range.
    /// </summary>
    public DateOnly ClosingMonth => End.AddMonths(-1);

    public int MonthCount =>
        ((End.Year - Start.Year) * 12) + End.Month - Start.Month;

    /// <summary>The same period one year earlier, for year-on-year comparisons.</summary>
    public FinancePeriod PriorYear() =>
        new(Start.AddYears(-1), End.AddYears(-1), $"{Label} (prior year)");

    /// <summary>
    /// The immediately preceding period of equal length, for sequential comparisons.
    /// </summary>
    public FinancePeriod PriorPeriod() =>
        new(Start.AddMonths(-MonthCount), Start, $"{Label} (prior period)");
}

/// <summary>
/// Parses the free-text date range the router extracted.
/// <para>
/// The router is told never to invent a missing argument, so an unparseable or absent range is
/// reported back to the user rather than silently defaulting to a period they did not ask for.
/// A statement for the wrong period is worse than a clarifying question.
/// </para>
/// </summary>
public static class FinancePeriodParser
{
    private static readonly string[] MonthNames =
    [
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december"
    ];

    /// <param name="today">
    /// Supplied by the caller rather than read from the clock so that relative phrases stay
    /// deterministic under durable replay.
    /// </param>
    public static FinancePeriod? Parse(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string value = text.Trim().ToLowerInvariant();

        return ParseRelative(value, today)
            ?? ParseQuarter(value)
            ?? ParseMonthRange(value)
            ?? ParseSingleMonth(value)
            ?? ParseHalf(value)
            ?? ParseYear(value);
    }

    private static FinancePeriod? ParseRelative(string value, DateOnly today)
    {
        DateOnly thisMonth = new(today.Year, today.Month, 1);

        switch (value)
        {
            case "this month":
            case "current month":
                return Month(thisMonth);

            case "last month":
            case "previous month":
                return Month(thisMonth.AddMonths(-1));

            case "this quarter":
            case "current quarter":
                return QuarterOf(thisMonth);

            case "last quarter":
            case "previous quarter":
                return QuarterOf(thisMonth.AddMonths(-3));

            case "this year":
            case "current year":
            case "ytd":
            case "year to date":
                return new FinancePeriod(
                    new DateOnly(today.Year, 1, 1),
                    thisMonth.AddMonths(1),
                    $"YTD {today.Year}");

            case "last year":
            case "previous year":
                return Year(today.Year - 1);

            default:
                return null;
        }
    }

    private static FinancePeriod? ParseQuarter(string value)
    {
        Match m = Regex.Match(
            value,
            @"\bq([1-4])\s*(?:of\s*)?(?:fy)?\s*(\d{4})\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        if (!m.Success)
        {
            m = Regex.Match(
                value,
                @"\b(\d{4})\s*q([1-4])\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(1));

            if (!m.Success)
            {
                return null;
            }

            int flippedYear = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int flippedQuarter = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

            return Quarter(flippedYear, flippedQuarter);
        }

        int quarter = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        return Quarter(year, quarter);
    }

    private static FinancePeriod? ParseHalf(string value)
    {
        Match m = Regex.Match(
            value, @"\bh([12])\s*(?:fy)?\s*(\d{4})\b", RegexOptions.None, TimeSpan.FromSeconds(1));

        if (!m.Success)
        {
            return null;
        }

        int half = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int startMonth = half == 1 ? 1 : 7;

        return new FinancePeriod(
            new DateOnly(year, startMonth, 1),
            new DateOnly(year, startMonth, 1).AddMonths(6),
            $"H{half} {year}");
    }

    private static FinancePeriod? ParseMonthRange(string value)
    {
        Match m = Regex.Match(
            value,
            @"\b([a-z]+)\s*(\d{4})?\s*(?:to|through|thru|until|-|–|—)\s*([a-z]+)\s*(\d{4})\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        if (!m.Success)
        {
            return null;
        }

        int fromMonth = MonthIndex(m.Groups[1].Value);
        int toMonth = MonthIndex(m.Groups[3].Value);

        if (fromMonth == 0 || toMonth == 0)
        {
            return null;
        }

        int toYear = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        int fromYear = m.Groups[2].Success
            ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
            : toYear;

        var start = new DateOnly(fromYear, fromMonth, 1);
        var end = new DateOnly(toYear, toMonth, 1).AddMonths(1);

        return end <= start
            ? null
            : new FinancePeriod(start, end, $"{Title(m.Groups[1].Value)} to {Title(m.Groups[3].Value)} {toYear}");
    }

    private static FinancePeriod? ParseSingleMonth(string value)
    {
        Match m = Regex.Match(
            value, @"\b([a-z]+)\s+(\d{4})\b", RegexOptions.None, TimeSpan.FromSeconds(1));

        if (!m.Success)
        {
            return null;
        }

        int month = MonthIndex(m.Groups[1].Value);

        if (month == 0)
        {
            return null;
        }

        int year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        return Month(new DateOnly(year, month, 1));
    }

    private static FinancePeriod? ParseYear(string value)
    {
        Match m = Regex.Match(
            value, @"\b(?:fy)?\s*(\d{4})\b", RegexOptions.None, TimeSpan.FromSeconds(1));

        return m.Success
            ? Year(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static FinancePeriod Quarter(int year, int quarter) =>
        new(
            new DateOnly(year, ((quarter - 1) * 3) + 1, 1),
            new DateOnly(year, ((quarter - 1) * 3) + 1, 1).AddMonths(3),
            $"Q{quarter} {year}");

    private static FinancePeriod QuarterOf(DateOnly month) =>
        Quarter(month.Year, ((month.Month - 1) / 3) + 1);

    private static FinancePeriod Month(DateOnly month) =>
        new(month, month.AddMonths(1), month.ToString("MMMM yyyy", CultureInfo.InvariantCulture));

    private static FinancePeriod Year(int year) =>
        new(new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1), year.ToString(CultureInfo.InvariantCulture));

    private static int MonthIndex(string name)
    {
        for (int i = 0; i < MonthNames.Length; i++)
        {
            if (MonthNames[i].StartsWith(name, StringComparison.Ordinal) && name.Length >= 3)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static string Title(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
