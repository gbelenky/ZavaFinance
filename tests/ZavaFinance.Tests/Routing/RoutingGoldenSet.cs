// Copyright (c) Microsoft Corporation.

using ZavaFinance.Core.Agent;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// One utterance and the route it must produce.
/// <para>
/// Assertions are exact/contains matches, never an LLM judge: a routing regression has to fail
/// deterministically and cheaply. Judged metrics are reserved for text the orchestrator itself
/// synthesizes, of which there is almost none.
/// </para>
/// </summary>
public sealed record RoutingCase
{
    public required string Id { get; init; }

    /// <summary>The final user turn, the one the assertions apply to.</summary>
    public required string Utterance { get; init; }

    public required string ExpectedTool { get; init; }

    /// <summary>
    /// Turns replayed through the same session before <see cref="Utterance"/>. This is how
    /// sticky-routing behaviour is exercised: the follow-up must inherit context from these.
    /// </summary>
    public IReadOnlyList<string> PriorTurns { get; init; } = [];

    /// <summary>Case-insensitive substring the extracted KPI must contain.</summary>
    public string? KpiContains { get; init; }

    /// <summary>Case-insensitive substring the extracted organization must contain.</summary>
    public string? OrgContains { get; init; }

    /// <summary>Case-insensitive substrings the extracted date range must all contain.</summary>
    public IReadOnlyList<string> DateRangeContains { get; init; } = [];

    /// <summary>
    /// When true the KPI argument may be omitted, because the session already established it
    /// and <c>get_statement</c> falls back to the session's latest KPI.
    /// </summary>
    public bool KpiMayBeInherited { get; init; }

    /// <summary>Why this case exists. Shown on failure.</summary>
    public required string Rationale { get; init; }

    public override string ToString() => Id;
}

/// <summary>
/// The routing golden set.
/// <para>
/// An orchestrator is judged on routing, not on answer quality — answer quality belongs to the
/// Copilot Studio agent. These cases cover, in the priority order the design calls for:
/// tool selection, argument extraction, and sticky-routing correctness.
/// </para>
/// </summary>
public static class RoutingGoldenSet
{
    public static IReadOnlyList<RoutingCase> Cases { get; } =
    [
        // -------------------------------------------------------------------
        // 1. Tool selection - definition intent must reach get_kpi_info.
        // -------------------------------------------------------------------
        new()
        {
            Id = "kpi-what-is",
            Utterance = "What is allocated income?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "allocated income",
            Rationale = "Plain definition question is the canonical get_kpi_info trigger."
        },
        new()
        {
            Id = "kpi-how-calculated",
            Utterance = "How is EBIT calculated?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "ebit",
            Rationale = "'How is X calculated' is a definition request, not a request for figures."
        },
        new()
        {
            Id = "kpi-explain",
            Utterance = "Explain net revenues to me.",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "net revenue",
            Rationale = "'Explain' is definitional even without the word 'what'."
        },
        new()
        {
            Id = "kpi-what-does-mean",
            Utterance = "What does gross margin mean?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "gross margin",
            Rationale = "Meaning question, no organization or period present."
        },
        new()
        {
            Id = "kpi-tell-me-about",
            Utterance = "Tell me about operating margin.",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "operating margin",
            Rationale =
                "Ambiguous phrasing with no org or period. The tool description's negative "
                + "constraint ('do NOT use get_statement to explain a KPI') is what separates these."
        },

        // -------------------------------------------------------------------
        // 2. Tool selection - figures intent must reach get_statement,
        //    plus argument extraction.
        // -------------------------------------------------------------------
        new()
        {
            Id = "stmt-full-args",
            Utterance = "Show me net revenues for Nordics in Q3 2026.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "net revenue",
            OrgContains = "nordic",
            DateRangeContains = ["Q3", "2026"],
            Rationale = "All three arguments present and must be extracted verbatim-ish."
        },
        new()
        {
            Id = "stmt-named-entity-org",
            Utterance = "Give me the EBIT statement for Zava GmbH for January to March 2026.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "ebit",
            OrgContains = "zava",
            DateRangeContains = ["January", "2026"],
            Rationale = "Organization is a company name and the period is spelled out, not a quarter."
        },
        new()
        {
            Id = "stmt-figures-wording",
            Utterance = "What were the figures for gross margin in EMEA last quarter?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "gross margin",
            OrgContains = "emea",
            Rationale =
                "Starts with 'what' like a definition question, but 'figures' makes it a statement. "
                + "This is the case most likely to mis-route if descriptions are weak."
        },
        new()
        {
            Id = "stmt-report-wording",
            Utterance = "I need a report on headcount for APAC for FY2026.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "headcount",
            OrgContains = "apac",
            DateRangeContains = ["2026"],
            Rationale = "'Report' is a statement trigger."
        },
        new()
        {
            Id = "stmt-terse",
            Utterance = "Numbers for operating margin, Germany, Q1 2026.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "operating margin",
            OrgContains = "germany",
            DateRangeContains = ["Q1", "2026"],
            Rationale = "Telegraphic phrasing with no verb still has to parse into three arguments."
        },

        // -------------------------------------------------------------------
        // 3. Neither tool applies.
        // -------------------------------------------------------------------
        new()
        {
            Id = "none-greeting",
            Utterance = "Hello there!",
            ExpectedTool = OrchestratorRoute.NoTool,
            Rationale = "A greeting must not invoke a subagent; Copilot Studio calls cost ~51 s."
        },
        new()
        {
            Id = "none-capability",
            Utterance = "What can you do?",
            ExpectedTool = OrchestratorRoute.NoTool,
            Rationale = "Capability question is answered by the orchestrator itself."
        },
        new()
        {
            Id = "none-off-topic",
            Utterance = "What's the weather in Stockholm tomorrow?",
            ExpectedTool = OrchestratorRoute.NoTool,
            Rationale = "Off-topic must degrade gracefully rather than guess a KPI."
        },
        new()
        {
            Id = "none-thanks",
            Utterance = "Thanks, that's helpful.",
            ExpectedTool = OrchestratorRoute.NoTool,
            Rationale = "Acknowledgement must not re-trigger the previous tool."
        },

        // -------------------------------------------------------------------
        // 4. Sticky routing - follow-ups inherit context, topic changes drop it.
        // -------------------------------------------------------------------
        new()
        {
            Id = "sticky-followup-inherits-kpi",
            PriorTurns = ["What is allocated income?"],
            Utterance = "And for the Nordics in Q3 2026?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "allocated income",
            KpiMayBeInherited = true,
            OrgContains = "nordic",
            DateRangeContains = ["Q3", "2026"],
            Rationale =
                "A bare follow-up naming only org and period must become get_statement for the "
                + "KPI already established. Either the model restates the KPI or omits it and the "
                + "tool falls back to session state; both are correct."
        },
        new()
        {
            Id = "sticky-followup-inherits-period",
            PriorTurns =
            [
                "What is gross margin?",
                "Show me gross margin for EMEA in Q1 2026."
            ],
            Utterance = "What about APAC?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            OrgContains = "apac",
            KpiMayBeInherited = true,
            Rationale = "Only the organization changes; KPI and period carry over."
        },
        new()
        {
            Id = "sticky-topic-change-drops-kpi",
            PriorTurns = ["What is EBIT?"],
            Utterance = "Actually, what is gross margin?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "gross margin",
            Rationale =
                "A clear topic change must stop inheriting. Returning 'EBIT' here is the "
                + "sticky-routing failure mode."
        },
        new()
        {
            Id = "sticky-definition-after-statement",
            PriorTurns = ["Show me net revenues for Nordics in Q3 2026."],
            Utterance = "How is that actually defined?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "net revenue",
            Rationale =
                "Switching from figures back to definition for the same KPI: the tool changes "
                + "but the KPI is inherited."
        },

        // -------------------------------------------------------------------
        // 5. Adversarial - phrasing that points one way and intent the other.
        //    These are the cases the tools' negative constraints exist for
        //    ("do NOT use this to retrieve figures" / "...to explain a KPI").
        // -------------------------------------------------------------------
        new()
        {
            Id = "adversarial-what-is-but-wants-figures",
            Utterance = "What is the net revenue for Nordics in Q3 2026?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "net revenue",
            OrgContains = "nordic",
            DateRangeContains = ["Q3", "2026"],
            Rationale =
                "Opens with 'What is', the definition trigger, but names an organization and a "
                + "period, so it is unambiguously a request for figures."
        },
        new()
        {
            Id = "adversarial-how-much-wants-figures",
            Utterance = "How much was EBIT for Zava GmbH in Q2 2026?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "ebit",
            OrgContains = "zava",
            DateRangeContains = ["Q2", "2026"],
            Rationale =
                "'How much' neighbours 'how is it calculated', which routes the other way. "
                + "Quantity versus method is the distinction under test."
        },
        new()
        {
            Id = "adversarial-comparison-is-definitional",
            Utterance = "Is allocated income the same thing as net revenue?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            Rationale =
                "A comparison between two KPIs is still a definition question with no "
                + "organization or period, so it must not become a statement request."
        },
        new()
        {
            Id = "adversarial-knowledge-base-phrasing",
            Utterance = "What does KPIpedia say about operating margin?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "operating margin",
            Rationale =
                "Names the knowledge base explicitly; must route to the subagent that owns it "
                + "rather than being treated as a request for numbers."
        },
        new()
        {
            Id = "adversarial-figures-without-org-or-period",
            Utterance = "How much did we spend on headcount?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "headcount",
            Rationale =
                "A request for figures with no organization or period. The route is still "
                + "get_statement; the prompt forbids inventing the missing arguments, so absent "
                + "org and dateRange are correct rather than a failure."
        },

        // -------------------------------------------------------------------
        // 6. Hard - deliberately at the edge of the decision boundary. These
        //    exist to stop the golden set sitting at 100%: a set that never
        //    fails can catch a regression but can never demonstrate an
        //    improvement, so it cannot justify a change to the routing contract.
        // -------------------------------------------------------------------
        new()
        {
            Id = "hard-figures-with-no-org-or-period",
            Utterance = "Show me EBIT.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "ebit",
            Rationale =
                "'Show me' is a figures request even stripped of every other argument. The "
                + "missing org and period must stay missing rather than be invented."
        },
        new()
        {
            Id = "hard-definition-despite-org-present",
            Utterance = "In the Nordics, how do we define allocated income?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "allocated income",
            Rationale =
                "An organization is present, which is the strongest single signal for "
                + "get_statement, but 'how do we define' is definitional. Naming a region does "
                + "not turn a definition question into a request for figures."
        },
        new()
        {
            Id = "hard-negated-definition-decoy",
            Utterance = "I don't need the definition, just the Q3 2026 numbers for EBIT in EMEA.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "ebit",
            OrgContains = "emea",
            DateRangeContains = ["Q3", "2026"],
            Rationale =
                "The word 'definition' appears but is negated. Keyword-shaped routing fails "
                + "here; the model has to read the negation."
        },
        new()
        {
            Id = "hard-give-me-the-definition",
            Utterance = "Give me the definition of operating margin.",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "operating margin",
            Rationale =
                "'Give me' is a statement trigger and 'definition' is the real intent. The "
                + "verb must lose to the object."
        },
        new()
        {
            Id = "hard-implicit-performance-question",
            Utterance = "How are we doing on headcount in APAC this year?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "headcount",
            OrgContains = "apac",
            Rationale =
                "Asks for figures without using any of the trigger words in the tool "
                + "description - no 'show', 'report', 'numbers' or 'figures'."
        },
        new()
        {
            Id = "hard-period-only-followup",
            PriorTurns = ["What is EBIT?"],
            Utterance = "Q3 2026?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "ebit",
            KpiMayBeInherited = true,
            DateRangeContains = ["Q3", "2026"],
            Rationale =
                "A two-word follow-up carrying only a period. Switching tool on this little "
                + "signal is the hardest inheritance case in the set."
        },
        new()
        {
            Id = "hard-org-only-drift-keeps-period",
            PriorTurns = ["Show me gross margin for EMEA in Q1 2026."],
            Utterance = "And Q2?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "gross margin",
            KpiMayBeInherited = true,
            OrgContains = "emea",
            DateRangeContains = ["Q2"],
            Rationale =
                "Only the period changes. KPI and organization must both survive while "
                + "dateRange is replaced - partial inheritance, not all-or-nothing."
        },
        new()
        {
            Id = "hard-latest-kpi-wins-over-first",
            PriorTurns =
            [
                "What is EBIT?",
                "Show me EBIT for APAC in Q1 2026.",
                "What is gross margin?"
            ],
            Utterance = "Now show me that for the Nordics in Q2 2026.",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "gross margin",
            KpiMayBeInherited = true,
            OrgContains = "nordic",
            DateRangeContains = ["Q2", "2026"],
            Rationale =
                "'That' refers to the most recently discussed KPI, not the first one in the "
                + "conversation. Returning EBIT here is the classic stale-context failure."
        },
        new()
        {
            Id = "hard-real-question-after-greeting",
            PriorTurns = ["Hello there!"],
            Utterance = "What is EBIT?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "ebit",
            Rationale =
                "A 'none' turn must not poison the session. The first real question after "
                + "small talk has to route normally."
        },

        // -------------------------------------------------------------------
        // 7. explore_finance - open-ended analysis. This tool overlaps
        //    get_statement's surface (both are about figures), so these cases
        //    guard the boundary between "one number" and "analysis".
        // -------------------------------------------------------------------
        new()
        {
            Id = "explore-why-question",
            Utterance = "Why did APAC margin fall in Q3 2026, and which departments drove it?",
            ExpectedTool = OrchestratorRoute.ExploreFinanceTool,
            Rationale =
                "Causal analysis across departments cannot be expressed as one "
                + "KPI-organization-period lookup."
        },
        new()
        {
            Id = "explore-ranking",
            Utterance = "Which department had the highest revenue per FTE in 2026?",
            ExpectedTool = OrchestratorRoute.ExploreFinanceTool,
            Rationale =
                "Ranking across all departments needs analysis; get_statement returns a single "
                + "figure for one named organization."
        },
        new()
        {
            Id = "explore-multi-region-comparison",
            Utterance = "Compare gross margin across all four regions for Q4 2026.",
            ExpectedTool = OrchestratorRoute.ExploreFinanceTool,
            Rationale = "Comparison across many organizations at once, not a single lookup."
        },
        new()
        {
            Id = "explore-trend",
            Utterance = "What trends do you see in our operating expenses over the last two years?",
            ExpectedTool = OrchestratorRoute.ExploreFinanceTool,
            Rationale = "Open-ended trend interpretation rather than a specific figure."
        },
        new()
        {
            Id = "explore-boundary-single-figure-stays-statement",
            Utterance = "What was EMEA net revenue in November 2025?",
            ExpectedTool = OrchestratorRoute.StatementTool,
            KpiContains = "net revenue",
            OrgContains = "emea",
            DateRangeContains = ["November", "2025"],
            Rationale =
                "One KPI, one organization, one period. Routing this to the slow analytical "
                + "agent would be a needless minute of latency for a sub-second query."
        },
        new()
        {
            Id = "explore-boundary-definition-stays-kpi-info",
            Utterance = "Why do we even track days sales outstanding?",
            ExpectedTool = OrchestratorRoute.KpiInfoTool,
            KpiContains = "days sales outstanding",
            Rationale =
                "Begins with 'why', the analysis trigger, but asks about the meaning of a KPI "
                + "rather than about the data."
        }
    ];

    /// <summary>Cases whose expected route is the given tool.</summary>
    public static IEnumerable<RoutingCase> For(string tool) =>
        Cases.Where(c => c.ExpectedTool == tool);
}
