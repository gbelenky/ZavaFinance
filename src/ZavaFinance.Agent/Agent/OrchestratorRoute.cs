// Copyright (c) Microsoft Corporation.

using System.ComponentModel;

namespace ZavaFinance.Core.Agent;

public sealed class OrchestratorRoute
{
    public const string KpiInfoTool = "get_kpi_info";
    public const string StatementTool = "get_statement";
    public const string ExploreFinanceTool = "explore_finance";
    public const string NoTool = "none";

    [Description(
        "Tool to invoke: get_kpi_info, get_statement, explore_finance, or none.")]
    public string Tool { get; set; } = NoTool;

    /// <summary>
    /// One KPI field shared by get_kpi_info and get_statement.
    /// <para>
    /// These were once separate properties, and the model reliably put the KPI in the wrong one
    /// when routing to get_statement — the two fields described the same concept, so nothing in
    /// the schema said which to use. A flat route object cannot express per-tool argument
    /// schemas, so overlapping arguments have to be unified rather than duplicated.
    /// </para>
    /// </summary>
    [Description(
        "KPI name for get_kpi_info or get_statement. For get_statement, omit to reuse the KPI "
        + "most recently explained.")]
    public string? Kpi { get; set; }

    [Description("Organization or business unit for get_statement.")]
    public string? Org { get; set; }

    [Description("Date range for get_statement.")]
    public string? DateRange { get; set; }

    [Description("The full analytical question for explore_finance.")]
    public string? Question { get; set; }

    [Description("Brief response when no tool applies.")]
    public string? Message { get; set; }
}
