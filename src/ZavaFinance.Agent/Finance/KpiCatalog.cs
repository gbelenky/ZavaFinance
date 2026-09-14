// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Finance;

/// <summary>How a KPI may be combined across rows.</summary>
public enum KpiAggregation
{
    /// <summary>Safe to sum across every dimension.</summary>
    Additive,

    /// <summary>
    /// Must be recomputed from summed components. Summing or averaging a ratio is wrong, and
    /// on this dataset naive aggregation of monthly departmental margins is off by more than
    /// 100 percentage points.
    /// </summary>
    Ratio,

    /// <summary>
    /// Sums across organizations but never across months. A period figure takes the closing
    /// month.
    /// </summary>
    SemiAdditive
}

public enum KpiUnit
{
    Currency,
    Percent,
    Count,
    Days
}

/// <summary>One KPI, mirrored from the lakehouse <c>dim_kpi</c> table and KPIpedia.</summary>
public sealed record KpiDefinition(
    string Code,
    string Name,
    KpiUnit Unit,
    KpiAggregation Aggregation,
    string Formula);

/// <summary>
/// The KPI catalogue.
/// <para>
/// Held in code rather than read from <c>dim_kpi</c> on every turn because the aggregation rule
/// for each KPI is executable logic, not data: a ratio KPI needs a specific recomputation, and
/// a row in a dimension table cannot express that. <c>dim_kpi</c> remains the source for the
/// codes themselves.
/// </para>
/// </summary>
public static class KpiCatalog
{
    public static IReadOnlyList<KpiDefinition> All { get; } =
    [
        new("KPI-001", "Gross Revenue", KpiUnit.Currency, KpiAggregation.Additive,
            "SUM(accounts 4000, 4010)"),
        new("KPI-002", "Revenue Deductions", KpiUnit.Currency, KpiAggregation.Additive,
            "SUM(accounts 4100, 4110, 4120)"),
        new("KPI-003", "Net Revenue", KpiUnit.Currency, KpiAggregation.Additive,
            "Gross Revenue - Revenue Deductions"),
        new("KPI-004", "COGS", KpiUnit.Currency, KpiAggregation.Additive,
            "SUM(accounts 5000, 5010, 5020, 5030)"),
        new("KPI-005", "Gross Profit", KpiUnit.Currency, KpiAggregation.Additive,
            "Net Revenue - COGS"),
        new("KPI-006", "Gross Margin %", KpiUnit.Percent, KpiAggregation.Ratio,
            "Gross Profit / Net Revenue"),
        new("KPI-007", "Operating Expenses", KpiUnit.Currency, KpiAggregation.Additive,
            "SUM(accounts 6000-6050)"),
        new("KPI-008", "EBITDA", KpiUnit.Currency, KpiAggregation.Additive,
            "Gross Profit - Operating Expenses"),
        new("KPI-009", "EBITDA Margin %", KpiUnit.Percent, KpiAggregation.Ratio,
            "EBITDA / Net Revenue"),
        new("KPI-010", "Depreciation & Amortisation", KpiUnit.Currency, KpiAggregation.Additive,
            "SUM(account 7000)"),
        new("KPI-011", "Operating Income (EBIT)", KpiUnit.Currency, KpiAggregation.Additive,
            "EBITDA - D&A"),
        new("KPI-012", "Operating Margin %", KpiUnit.Percent, KpiAggregation.Ratio,
            "Operating Income / Net Revenue"),
        new("KPI-013", "Headcount (FTE)", KpiUnit.Count, KpiAggregation.SemiAdditive,
            "Period-end FTE"),
        new("KPI-014", "Revenue per FTE", KpiUnit.Currency, KpiAggregation.Ratio,
            "Net Revenue / Headcount"),
        new("KPI-015", "Days Sales Outstanding", KpiUnit.Days, KpiAggregation.Ratio,
            "Revenue-weighted average of monthly DSO"),
        new("KPI-016", "Budget Variance %", KpiUnit.Percent, KpiAggregation.Ratio,
            "(Net Revenue - Budget Net Revenue) / Budget Net Revenue"),
        new("KPI-017", "Net Revenue YoY Growth %", KpiUnit.Percent, KpiAggregation.Ratio,
            "(Net Revenue - Net Revenue SPLY) / Net Revenue SPLY"),
        new("KPI-018", "Budget Net Revenue", KpiUnit.Currency, KpiAggregation.Additive,
            "Budget plan value")
    ];

    /// <summary>
    /// Phrasings the model reliably produces that are not substrings of the canonical name.
    /// </summary>
    private static readonly (string Alias, string Code)[] Aliases =
    [
        ("ebit", "KPI-011"),
        ("operating income", "KPI-011"),
        ("operating profit", "KPI-011"),
        ("allocated income", "KPI-011"),
        ("ebitda", "KPI-008"),
        ("net revenues", "KPI-003"),
        ("revenue", "KPI-003"),
        ("revenues", "KPI-003"),
        ("net sales", "KPI-003"),
        ("topline", "KPI-003"),
        ("top line", "KPI-003"),
        ("gross margin", "KPI-006"),
        ("gross margin percent", "KPI-006"),
        ("operating margin", "KPI-012"),
        ("ebitda margin", "KPI-009"),
        ("headcount", "KPI-013"),
        ("fte", "KPI-013"),
        ("employees", "KPI-013"),
        ("dso", "KPI-015"),
        ("revenue per fte", "KPI-014"),
        ("revenue per employee", "KPI-014"),
        ("budget variance", "KPI-016"),
        ("yoy growth", "KPI-017"),
        ("growth", "KPI-017"),
        ("cost of goods sold", "KPI-004"),
        ("opex", "KPI-007"),
        ("depreciation", "KPI-010"),
        ("d&a", "KPI-010")
    ];

    /// <summary>
    /// Resolves free text to a KPI. The router extracts whatever the user said, so this has to
    /// cope with "EBIT", "net revenues", "gross margin percent" and similar.
    /// </summary>
    public static KpiDefinition? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string needle = Normalize(text);

        if (needle.Length == 0)
        {
            return null;
        }

        KpiDefinition? match =
            All.FirstOrDefault(k => string.Equals(k.Code, text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(k => Normalize(k.Name) == needle);

        if (match is not null)
        {
            return match;
        }

        foreach ((string alias, string code) in Aliases)
        {
            if (needle == alias)
            {
                return All.First(k => k.Code == code);
            }
        }

        // Longest canonical name wins, so "gross margin" cannot be captured by "gross revenue".
        KpiDefinition? contained = All
            .Where(k => Normalize(k.Name).Contains(needle, StringComparison.Ordinal)
                || needle.Contains(Normalize(k.Name), StringComparison.Ordinal))
            .OrderByDescending(k => Normalize(k.Name).Length)
            .FirstOrDefault();

        if (contained is not null)
        {
            return contained;
        }

        // Finally, an alias appearing inside a longer phrase such as "ebit for the nordics".
        return Aliases
            .Where(a => needle.Contains(a.Alias, StringComparison.Ordinal))
            .OrderByDescending(a => a.Alias.Length)
            .Select(a => All.First(k => k.Code == a.Code))
            .FirstOrDefault();
    }

    internal static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '&')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }
}
