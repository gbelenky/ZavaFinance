// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Finance;

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Statement tool. Backed by a parameterised query against the Fabric lakehouse SQL analytics
/// endpoint, executed as the signed-in user.
/// </summary>
public sealed class StatementTool
{
    private readonly IStatementQuery? _query;
    private readonly OrchestratorSessionState _state;
    private readonly DateOnly _today;
    private readonly ILogger _logger;

    public StatementTool(
        IStatementQuery? query,
        OrchestratorSessionState state,
        DateOnly today,
        ILogger logger)
    {
        _query = query;
        _state = state;
        _today = today;
        _logger = logger;
    }

    [OrchestratorTool(FinanceToolNames.StatementTool)]
    [Description(
        "Return the actual figure for one specific KPI, organization and period from the Zava "
        + "finance lakehouse. Use this when the user asks for figures, numbers, a report or a "
        + "statement, including retrieval phrasing such as 'show me', 'give me' or 'how much' "
        + "applied to a KPI. Asking how one KPI is doing in one organization and period is "
        + "also a figure lookup, unless the user requests an explanation, trend or comparison. "
        + "A missing organization or date range does not disqualify this "
        + "tool: it asks for whatever it still needs. Do NOT use this to explain what a KPI "
        + "means, and do NOT use it for open-ended analysis such as 'why did margin fall' or "
        + "questions that rank or compare many things at once.")]
    public async Task<string> GetStatementAsync(
        [Description("KPI name. Omit to reuse the KPI most recently explained.")]
        string? kpi = null,
        [Description("Organization or business unit, e.g. 'Nordics', 'EMEA' or 'Marketing'.")]
        string org = "",
        [Description("Date range, e.g. 'Q3 2026', 'November 2025' or 'January to March 2026'.")]
        string dateRange = "",
        CancellationToken cancellationToken = default)
    {
        // Falls back to the session's most recent KPI so follow-ups like "now show me the
        // numbers for Nordics" work without restating it.
        string? effectiveKpiText = !string.IsNullOrWhiteSpace(kpi) ? kpi : _state.LastKpiName;

        KpiDefinition? definition = KpiCatalog.Resolve(effectiveKpiText);

        if (definition is null)
        {
            return string.IsNullOrWhiteSpace(effectiveKpiText)
                ? "Which KPI would you like a statement for?"
                : $"I do not recognise '{effectiveKpiText}' as a KPI I can report on.";
        }

        if (string.IsNullOrWhiteSpace(org))
        {
            return $"Which organization should I report {definition.Name} for? "
                + "You can name a region such as EMEA, a department, or the whole company.";
        }

        // Never default a missing period: a statement for a period the user did not ask for is
        // worse than one more question.
        FinancePeriod? period = FinancePeriodParser.Parse(dateRange, _today);

        if (period is null)
        {
            return string.IsNullOrWhiteSpace(dateRange)
                ? $"Which period should I report {definition.Name} for, for example 'Q3 2026'?"
                : $"I could not interpret '{dateRange}' as a period. Try 'Q3 2026', "
                  + "'November 2025' or 'January to March 2026'.";
        }

        if (_query is null)
        {
            _logger.LogWarning("get_statement requested but Fabric SQL is not configured.");
            return "The finance warehouse is not configured in this environment.";
        }

        try
        {
            OrganizationScope? scope =
                await _query.ResolveOrganizationAsync(org, cancellationToken);

            if (scope is null)
            {
                return $"I could not find an organization called '{org}'. Zava reports by region "
                    + "(AMER, EMEA, APAC, LATAM) and by department.";
            }

            StatementResult result =
                await _query.GetStatementAsync(definition, scope, period, cancellationToken);

            _state.LastKpiName = definition.Name;

            // Figures must reach the user exactly as computed. A model paraphrase of a
            // statement is a wrong number waiting to happen.
            return Render(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("get_statement timed out.");

            return $"The finance warehouse did not respond in time for {definition.Name}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "get_statement failed.");

            return $"I could not retrieve {definition.Name} from the finance warehouse.";
        }
    }

    internal static string Render(StatementResult result)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"**{result.Kpi.Name} — {result.Organization.Name} — {result.Period.Label}**");
        builder.AppendLine();

        if (result.Value is null)
        {
            // Reported as unavailable rather than as zero: a fabricated zero is a wrong number.
            builder.Append("No data is available for that combination.");

            return builder.ToString().TrimEnd();
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- {result.Period.Label}: {Format(result.Value.Value, result.Kpi, result.Currency)}");

        if (result.PriorValue is not null)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Prior period: {Format(result.PriorValue.Value, result.Kpi, result.Currency)}");

            string? change = Change(result.Value.Value, result.PriorValue.Value, result.Kpi);

            if (change is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Change: {change}");
            }
        }

        // Attribution comes from the one shared definition, so all three tools name their
        // source the same way. The formula is included here because a computed figure is only
        // checkable if the arithmetic behind it is stated.
        return SourceFooter.Append(
            builder.ToString().TrimEnd(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SourceFooter.Lakehouse}. {result.Kpi.Name} = {result.Kpi.Formula}"));
    }

    private static string Format(decimal value, KpiDefinition kpi, string currency) =>
        kpi.Unit switch
        {
            KpiUnit.Percent => string.Create(CultureInfo.InvariantCulture, $"{value:N2}%"),
            KpiUnit.Count => string.Create(CultureInfo.InvariantCulture, $"{value:N0} FTE"),
            KpiUnit.Days => string.Create(CultureInfo.InvariantCulture, $"{value:N1} days"),
            _ => FormatCurrency(value, currency)
        };

    private static string FormatCurrency(decimal value, string currency) =>
        Math.Abs(value) >= 1_000_000m
            ? string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000m:N1} M {currency}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:N0} {currency}");

    private static string? Change(decimal current, decimal prior, KpiDefinition kpi)
    {
        // A percentage KPI moves in percentage *points*. Expressing that as a percent change of
        // a percent is a category error that reads as a far larger move than occurred.
        if (kpi.Unit == KpiUnit.Percent)
        {
            decimal points = Math.Round(current - prior, 2);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Math.Abs(points):N2} pp {(points >= 0 ? "up" : "down")} versus prior period");
        }

        if (prior == 0)
        {
            return null;
        }

        decimal change = Math.Round((current - prior) / Math.Abs(prior) * 100m, 1);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(change):N1}% {(change >= 0 ? "up" : "down")} versus prior period");
    }
}
