// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Finance;

/// <summary>Resolved organization scope for a statement.</summary>
public sealed record OrganizationScope(
    string Kind, string Code, string Name, string? CatalogVersion = null, ResolverRelease? Release = null)
{
    public const string Region = "region";
    public const string Department = "department";
    public const string Company = "company";

    public static OrganizationScope Whole { get; } = new(Company, "ALL", "Zava (all regions)");
}

/// <summary>The data a statement is rendered from.</summary>
public sealed record StatementResult(
    KpiDefinition Kpi,
    OrganizationScope Organization,
    FinancePeriod Period,
    decimal? Value,
    decimal? PriorValue,
    string Currency);

/// <summary>
/// Supplies statement figures.
/// <para>
/// Phase 1 was a synthetic generator and phase 2 is Fabric SQL. The interface exists so that
/// swap is a substitution rather than a rewrite, and so <c>get_statement</c> keeps its fast,
/// synchronous contract in both.
/// </para>
/// </summary>
public interface IStatementQuery
{
    Task<StatementResult> GetStatementAsync(
        KpiDefinition kpi,
        OrganizationScope organization,
        FinancePeriod period,
        CancellationToken cancellationToken);
}
