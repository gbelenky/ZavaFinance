// Copyright (c) Microsoft Corporation.

using System.Reflection;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Structural checks over the golden set itself. These need no model and always run, so a
/// malformed case is caught at build time rather than surfacing as a confusing eval failure.
/// </summary>
public sealed class GoldenSetIntegrityTests
{
    private static readonly string[] KnownTools =
    [
        OrchestratorRoute.KpiInfoTool,
        OrchestratorRoute.StatementTool,
        OrchestratorRoute.ExploreFinanceTool,
        OrchestratorRoute.NoTool
    ];

    [Fact]
    public void EveryCaseTargetsAKnownRoute()
    {
        foreach (RoutingCase testCase in RoutingGoldenSet.Cases)
        {
            Assert.Contains(testCase.ExpectedTool, KnownTools);
        }
    }

    [Fact]
    public void CaseIdsAreUnique()
    {
        string[] duplicates = RoutingGoldenSet.Cases
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void UtterancesAreUniqueWithinTheirPriorContext()
    {
        // The same wording may legitimately appear twice with different history, because the
        // history is what changes the correct route. Only an exact duplicate of both is a bug.
        string[] duplicates = RoutingGoldenSet.Cases
            .GroupBy(
                c => string.Join("\u001f", [.. c.PriorTurns, c.Utterance]),
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryRouteIsCovered()
    {
        foreach (string tool in KnownTools)
        {
            Assert.NotEmpty(RoutingGoldenSet.For(tool));
        }
    }

    [Fact]
    public void StickyRoutingIsExercisedInBothDirections()
    {
        RoutingCase[] multiTurn = [.. RoutingGoldenSet.Cases.Where(c => c.PriorTurns.Count > 0)];

        // Inheriting context and correctly dropping it are different failure modes; a golden set
        // that only covers one of them passes while the other silently regresses.
        Assert.Contains(multiTurn, c => c.KpiMayBeInherited);
        Assert.Contains(multiTurn, c => !c.KpiMayBeInherited);
    }

    [Fact]
    public void EveryCaseExplainsWhyItExists()
    {
        foreach (RoutingCase testCase in RoutingGoldenSet.Cases)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(testCase.Rationale),
                $"Case '{testCase.Id}' has no rationale.");
        }
    }

    [Fact]
    public void ArgumentExpectationsMatchTheTargetRoute()
    {
        foreach (RoutingCase testCase in RoutingGoldenSet.Cases)
        {
            if (testCase.ExpectedTool == OrchestratorRoute.NoTool)
            {
                Assert.Null(testCase.KpiContains);
                Assert.Null(testCase.OrgContains);
                Assert.Empty(testCase.DateRangeContains);
            }

            if (testCase.ExpectedTool == OrchestratorRoute.KpiInfoTool)
            {
                // get_kpi_info takes only a KPI name; asserting an org would be meaningless.
                Assert.Null(testCase.OrgContains);
                Assert.Empty(testCase.DateRangeContains);
            }

            if (testCase.ExpectedTool == OrchestratorRoute.ExploreFinanceTool)
            {
                // explore_finance takes only a free-text question.
                Assert.Null(testCase.KpiContains);
                Assert.Null(testCase.OrgContains);
                Assert.Empty(testCase.DateRangeContains);
            }
        }
    }

    [Fact]
    public void RouteConstantsMatchTheToolMethodNamesTheModelIsToldAbout()
    {
        // The prompt names the tools as strings. If a constant is renamed without updating the
        // prompt, routing silently degrades to 'none' for every turn.
        string prompt = OrchestratorAgent.SystemInstructions;

        Assert.Contains(OrchestratorRoute.KpiInfoTool, prompt, StringComparison.Ordinal);
        Assert.Contains(OrchestratorRoute.StatementTool, prompt, StringComparison.Ordinal);
        Assert.Contains(OrchestratorRoute.NoTool, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolMethodsCarryDescriptionsForEveryRoutedArgument()
    {
        // The [Description] attributes are the source of truth for how the model tells the
        // tools apart, so this walks whatever the catalog discovered rather than a hard-coded
        // list that would silently stop covering a newly added tool.
        Assert.NotEmpty(OrchestratorToolCatalog.Tools);

        foreach (OrchestratorToolDescriptor tool in OrchestratorToolCatalog.Tools)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' has no description.");

            foreach (OrchestratorToolParameter parameter in tool.Parameters)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(parameter.Description),
                    $"Parameter '{parameter.Name}' of '{tool.Name}' has no description.");
            }
        }
    }
}
