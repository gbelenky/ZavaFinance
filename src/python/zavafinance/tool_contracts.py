"""Typed MAF tool inputs, also used to validate schema-v1 stored calls."""

from pydantic import BaseModel, ConfigDict, Field


class ToolInput(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class KpiInfoInput(ToolInput):
    """Look up the official definition, meaning, formula or business description of a
    KPI from the KPIpedia knowledge base. Use this only when the user asks what a KPI
    is, what it means, how it is defined or how it is calculated. Do NOT use this to
    retrieve figures or numbers, and do NOT use it merely because the user named a KPI
    without an organization or period."""

    kpi: str = Field(description="The KPI name to explain, e.g. 'Net Revenues'.")


class StatementInput(ToolInput):
    """Return the actual figure for one specific KPI, organization and period from the Zava
    finance lakehouse. Use this when the user asks for figures, numbers, a report or a
    statement, including retrieval phrasing such as 'show me', 'give me' or 'how much'
    applied to a KPI. Asking how one KPI is doing in one organization and period is
    also a figure lookup, unless the user requests an explanation, trend or comparison.
    A missing organization or date range does not disqualify this tool: it asks for
    whatever it still needs. Do NOT use this to explain what a KPI means, and do NOT
    use it for open-ended analysis such as 'why did margin fall' or questions that
    rank or compare many things at once.

    Date-resolution rules for dateRange:
    - Explicit dates take precedence over conversation context and defaults.
    - For a quarter, half-year or month without a year, use the year of the
      most recent relevant, unambiguous period in the conversation. If no year is
      established, use the current calendar year supplied below. For example,
      'Q3' after 'Q4 2025' means 'Q3 2025'; without context it means Q3 of the
      supplied current year. Do not ask for a year solely because it was omitted.
    - On short follow-ups, retain the KPI, organization and period the user has
      not changed. An organization-only reply to a missing-organization question
      must keep the previously supplied period.
    - Normalize unambiguous date wording to a supported expression such as
      'Q3 2026', 'H1 2026', 'November 2025', 'January to March 2026' or '2026'.
      'The quarter before that' refers to the established period, including year
      rollover; for example, before Q1 2026 is Q4 2025.
    - Keep supported relative expressions such as 'this month', 'last month',
      'this quarter', 'last quarter', 'this year', 'last year' and 'YTD' unchanged.
      The host resolves them against the supplied UTC reference date, not an
      earlier conversation date. 'This year' and 'YTD' are year-to-date at the
      warehouse's monthly granularity.
    - Do not infer fiscal-year boundaries, data availability or a latest closed
      period. Do not drop unknown date qualifiers or widen the requested range.
      If the expression remains ambiguous or unsupported, preserve it in full
      so the host can request clarification. Use an empty string only when no
      period was provided or can be inherited."""

    kpi: str | None = Field(default=None, description=(
        "The user's KPI term verbatim; do not canonicalize, expand, or guess an ID. "
        "Omit to reuse the KPI most recently explained."))
    org: str = Field(default="", description=(
        "The user's organization term verbatim, retaining every scope qualifier. "
        "Do not replace a local unit with a parent, region, or company."))
    dateRange: str = Field(default="", description=(
        "The complete period resolved using this tool's date-resolution rules, e.g. "
        "'Q3 2025' for 'Q3' after a 2025 period, or the supplied current year when "
        "no year is established. Preserve supported relative expressions and any "
        "unresolved date qualifiers; do not invent dates or broaden the range."))


class AnalysisInput(ToolInput):
    """Answer an open-ended analytical question about Zava finance data that a single
    KPI-organization-period lookup cannot express: explaining why something moved,
    ranking or comparing many organizations or periods at once, finding drivers,
    trends or outliers. Use this when the question needs analysis rather than one
    figure. Do not infer a request for trends or comparisons from a single KPI's status.
    Do NOT use this for a single specific figure, and do NOT use it to explain what a KPI means."""

    question: str = Field(description=(
        "The user's analytical question, in full, as a natural-language sentence. "
        "Preserve its scope; do not add comparisons or subquestions."))


TOOL_INPUTS = {
    "get_kpi_info": KpiInfoInput,
    "get_statement": StatementInput,
    "explore_finance": AnalysisInput,
}
