// Copyright (c) Microsoft Corporation.

using System.Reflection;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Invariants over the annotation-driven tool catalog. These run without a model and are what
/// make the <c>[OrchestratorTool]</c> / <c>[Description]</c> annotations load-bearing rather than
/// decorative: the routing prompt is generated from them, so a drifting annotation now breaks
/// the build instead of silently changing nothing.
/// </summary>
public sealed class OrchestratorToolCatalogTests
{
    private readonly ITestOutputHelper _output;

    public OrchestratorToolCatalogTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CatalogContainsExactlyTheExecutableRoutes()
    {
        string[] names = [.. OrchestratorToolCatalog.Tools.Select(t => t.Name).Order(StringComparer.Ordinal)];

        // 'none' is not a tool: it has no method to execute and no arguments to extract.
        Assert.Equal(
            [
                OrchestratorRoute.ExploreFinanceTool,
                OrchestratorRoute.KpiInfoTool,
                OrchestratorRoute.StatementTool
            ],
            names);
    }

    [Fact]
    public void EveryToolArgumentMapsToARouteProperty()
    {
        // The model returns a OrchestratorRoute, not a function call. If a tool gains a parameter
        // that the route cannot carry, the argument is silently unroutable at runtime.
        string[] routeProperties =
        [
            .. typeof(OrchestratorRoute)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
        ];

        foreach (OrchestratorToolDescriptor tool in OrchestratorToolCatalog.Tools)
        {
            foreach (OrchestratorToolParameter parameter in tool.Parameters)
            {
                Assert.True(
                    routeProperties.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase),
                    $"Tool '{tool.Name}' declares argument '{parameter.Name}', but "
                    + $"{nameof(OrchestratorRoute)} has no matching property, so the router "
                    + "cannot return it.");
            }
        }
    }

    [Fact]
    public void GeneratedPromptSectionCarriesEveryToolAndArgument()
    {
        string section = OrchestratorToolCatalog.BuildPromptSection();

        foreach (OrchestratorToolDescriptor tool in OrchestratorToolCatalog.Tools)
        {
            Assert.Contains(tool.Name, section, StringComparison.Ordinal);
            Assert.Contains(tool.Description, section, StringComparison.Ordinal);

            foreach (OrchestratorToolParameter parameter in tool.Parameters)
            {
                Assert.Contains(parameter.Name, section, StringComparison.Ordinal);
                Assert.Contains(parameter.Description, section, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SystemPromptIsGeneratedFromTheAnnotations()
    {
        // The regression this guards: the descriptions used to live only on the methods, where
        // nothing read them, while the model saw a separate hand-written restatement.
        string prompt = OrchestratorAgent.SystemInstructions;

        // Printed so a routing investigation can see exactly what the model was told.
        _output.WriteLine(prompt);

        Assert.Contains(
            OrchestratorToolCatalog.BuildPromptSection(), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeConstraintsReachTheModel()
    {
        // Each tool's description says what it must NOT be used for, and those clauses are what
        // separate "explain this KPI" from "give me its figures" from "analyse this".
        // Asserted as an invariant rather than fixed strings so the wording stays tunable — but
        // a tool that loses its negative constraint entirely still fails here.
        string prompt = OrchestratorAgent.SystemInstructions;

        foreach (OrchestratorToolDescriptor tool in OrchestratorToolCatalog.Tools)
        {
            Assert.True(
                tool.Description.Contains("do NOT", StringComparison.OrdinalIgnoreCase),
                $"Tool '{tool.Name}' has no negative constraint in its description.");

            Assert.Contains(tool.Description, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoRouteStillReachesTheModel()
    {
        // 'none' is declared in the base prompt rather than the catalog, so it needs its own
        // guard against being dropped during a prompt edit.
        Assert.Contains(
            OrchestratorRoute.NoTool, OrchestratorAgent.SystemInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolNamesAreStableRouteConstants()
    {
        foreach (OrchestratorToolDescriptor tool in OrchestratorToolCatalog.Tools)
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name);
        }
    }
}
