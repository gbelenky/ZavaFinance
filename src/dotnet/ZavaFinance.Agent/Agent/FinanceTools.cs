using System.ComponentModel;
using Microsoft.Extensions.AI;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Core.Agent;

internal sealed class FinanceTools(
    Func<KpiInfoTool> definition, Func<StatementTool> statement, Func<ExploreFinanceTool> analysis,
    OrchestratorSessionState state, DateOnly today)
{
    internal FinanceReply? Reply { get; private set; }
    internal Exception? Failure { get; private set; }
    internal IReadOnlyList<AIFunction> Functions() => FinanceToolCatalog.Bind(this, today);
    internal StatementTool Statement() => statement();

    [OrchestratorTool(FinanceToolNames.KpiInfoTool)]
    [Description("Look up the official definition, meaning, formula or business description of a "
        + "KPI from the KPIpedia knowledge base. Use this only when the user asks what a KPI "
        + "is, what it means, how it is defined or how it is calculated. Do NOT use this to "
        + "retrieve figures or numbers, and do NOT use it merely because the user named a KPI "
        + "without an organization or period.")]
    public Task<string> GetKpiInfoAsync(
        [Description("The KPI name to explain, e.g. 'Net Revenues'.")] string kpi,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(() => definition().GetKpiInfoAsync(kpi, cancellationToken));

    [OrchestratorTool(FinanceToolNames.StatementTool)]
    [Description("Return the actual figure for one specific KPI, organization and period from the Zava "
        + "finance lakehouse. Use this when the user asks for figures, numbers, a report or a "
        + "statement, including retrieval phrasing such as 'show me', 'give me' or 'how much' "
        + "applied to a KPI. Asking how one KPI is doing in one organization and period is "
        + "also a figure lookup, unless the user requests an explanation, trend or comparison. "
        + "A missing organization or date range does not disqualify this tool: it asks for "
        + "whatever it still needs. Do NOT use this to explain what a KPI means, and do NOT "
        + "use it for open-ended analysis such as 'why did margin fall' or questions that "
        + "rank or compare many things at once.\n\n"
        + "Date-resolution rules for dateRange:\n"
        + "- Explicit dates take precedence over conversation context and defaults.\n"
        + "- For a quarter, half-year or month without a year, use the year of the most recent "
        + "relevant, unambiguous period in the conversation. If no year is established, use "
        + "the current calendar year supplied below. 'Q3' after 'Q4 2025' means 'Q3 2025'; "
        + "without context it means Q3 of the supplied current year. Do not ask for an omitted year.\n"
        + "- On short follow-ups, retain the KPI, organization and period the user has not changed. "
        + "An organization-only reply to a missing-organization question keeps the supplied period.\n"
        + "- Normalize unambiguous date wording to 'Q3 2026', 'H1 2026', 'November 2025', "
        + "'January to March 2026' or '2026'. 'The quarter before that' refers to the established "
        + "period, including year rollover: before Q1 2026 is Q4 2025.\n"
        + "- Keep 'this month', 'last month', 'this quarter', 'last quarter', 'this year', "
        + "'last year' and 'YTD' unchanged. The host resolves them against the supplied UTC "
        + "reference date, not an earlier conversation date. 'This year' and 'YTD' are "
        + "year-to-date at the warehouse's monthly granularity.\n"
        + "- Do not infer fiscal-year boundaries, data availability or a latest closed period. "
        + "Do not drop unknown date qualifiers or widen the range. Preserve ambiguous or "
        + "unsupported expressions in full for clarification. Use an empty string only when "
        + "no period was provided or can be inherited.")]
    public Task<string> GetStatementAsync(
        [Description("The user's KPI term verbatim; do not canonicalize, expand, or guess an ID. Omit to reuse the KPI most recently explained.")]
        string? kpi = null,
        [Description("The user's organization term verbatim, retaining every scope qualifier. Do not replace a local unit with a parent, region, or company.")]
        string org = "",
        [Description("The complete period resolved using the date-resolution rules. Inherit an established year or use the supplied current year; retain relative expressions and unresolved date qualifiers.")]
        string dateRange = "",
        CancellationToken cancellationToken = default) =>
        CompleteAsync(() => statement().GetStatementAsync(kpi, org, dateRange, cancellationToken));

    [OrchestratorTool(FinanceToolNames.ExploreFinanceTool)]
    [Description("Answer an open-ended analytical question about Zava finance data that a single "
        + "KPI-organization-period lookup cannot express: explaining why something moved, "
        + "ranking or comparing many organizations or periods at once, finding drivers, "
        + "trends or outliers. Use this when the question needs analysis rather than one "
        + "figure. Do not infer trends or comparisons from a single KPI's status. "
        + "Do NOT use this for a single specific figure, and do NOT use it to explain what a KPI means.")]
    public Task<string> ExploreFinanceAsync(
        [Description("The user's analytical question, in full, as a natural-language sentence. Preserve its scope; do not add comparisons or subquestions.")]
        string question, CancellationToken cancellationToken = default) =>
        CompleteAsync(() => analysis().ExploreFinanceAsync(question, cancellationToken));

    private async Task<string> CompleteAsync(Func<Task<string>> operation)
    {
        try
        {
            string answer = await operation();
            if (answer is null) throw new InvalidOperationException("A finance tool must return text.");
            Reply = new FinanceReply(answer, state.ReplyClarification);
            // Neither SDK function telemetry nor model history receives permissioned results.
            return FinanceHistory.WithheldResult;
        }
        catch (OperationCanceledException ex)
        {
            Failure = ex;
            throw;
        }
        catch (Exception ex)
        {
            Failure = new InvalidOperationException($"Finance tool failed. ErrorType={ex.GetType().Name}");
            throw Failure;
        }
    }
}
