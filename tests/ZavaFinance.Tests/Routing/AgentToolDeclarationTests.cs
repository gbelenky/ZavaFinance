// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Invariants over the agent's native tool declarations. These run without a model and are what
/// make the <c>[OrchestratorTool]</c> / <c>[Description]</c> annotations load-bearing rather than
/// decorative: native function schemas are generated from them, so a drifting annotation now breaks
/// the build instead of silently changing nothing.
/// </summary>
public sealed class AgentToolDeclarationTests
{
    private readonly ITestOutputHelper _output;

    public AgentToolDeclarationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DiscoversOnlyAnnotatedPublicInstanceMethodsOnSpecifiedClasses()
    {
        IReadOnlyList<AIFunctionDeclaration> tools =
            OrchestratorAgent.DiscoverToolDeclarations(typeof(SampleTools));

        Assert.Equal(["a_tool", "z_tool"], tools.Select(tool => tool.Name));
        Assert.All(tools, tool => Assert.False(tool is AIFunction));
    }

    [Fact]
    public void DiscoveredDeclarationsUseAnnotationsAndMethodSignatures()
    {
        AIFunctionDeclaration tool = OrchestratorAgent.DiscoverToolDeclarations(typeof(SampleTools))
            .Single(tool => tool.Name == "z_tool");

        Assert.Equal("An annotated tool.", tool.Description);
        JsonElement parameter = tool.JsonSchema.GetProperty("properties").GetProperty("question");
        Assert.Equal("The user's question.", parameter.GetProperty("description").GetString());
        Assert.Equal("string", parameter.GetProperty("type").GetString());
        Assert.Contains("question", tool.JsonSchema.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public void AgentDeclaresExactlyTheExecutableTools()
    {
        string[] names = [.. OrchestratorAgent.ToolDeclarations.Select(t => t.Name).Order(StringComparer.Ordinal)];

        // 'none' is not a tool: it has no method to execute and no arguments to extract.
        Assert.Equal(
            [
                FinanceToolNames.ExploreFinanceTool,
                FinanceToolNames.KpiInfoTool,
                FinanceToolNames.StatementTool
            ],
            names);
    }

    [Fact]
    public void EachFunctionHasOnlyItsOwnArguments()
    {
        var expected = new Dictionary<string, string[]>
        {
            [FinanceToolNames.KpiInfoTool] = ["kpi"],
            [FinanceToolNames.StatementTool] = ["dateRange", "kpi", "org"],
            [FinanceToolNames.ExploreFinanceTool] = ["question"]
        };

        foreach (AIFunctionDeclaration tool in OrchestratorAgent.ToolDeclarations)
        {
            Assert.Equal(expected[tool.Name], tool.JsonSchema.GetProperty("properties")
                .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.False(tool is AIFunction, "The model must only receive non-executable declarations.");
        }
    }

    [Fact]
    public void NativeSchemasCarryEveryArgumentDescription()
    {
        foreach (AIFunctionDeclaration tool in OrchestratorAgent.ToolDeclarations)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            _output.WriteLine(tool.JsonSchema.ToString());

            foreach (JsonProperty parameter in tool.JsonSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    parameter.Value.GetProperty("description").GetString()));
            }
        }
    }

    [Fact]
    public void DescriptionsAreNotDuplicatedInTheSystemPrompt()
    {
        string prompt = OrchestratorAgent.SystemInstructions;
        _output.WriteLine(prompt);

        foreach (AIFunctionDeclaration tool in OrchestratorAgent.ToolDeclarations)
        {
            Assert.DoesNotContain(tool.Description, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeConstraintsReachTheModel()
    {
        // Each tool's description says what it must NOT be used for, and those clauses are what
        // separate "explain this KPI" from "give me its figures" from "analyse this".
        // Asserted as an invariant rather than fixed strings so the wording stays tunable — but
        // a tool that loses its negative constraint entirely still fails here.
        foreach (AIFunctionDeclaration tool in OrchestratorAgent.ToolDeclarations)
        {
            Assert.True(
                tool.Description.Contains("do NOT", StringComparison.OrdinalIgnoreCase),
                $"Tool '{tool.Name}' has no negative constraint in its description.");

        }
    }

    [Fact]
    public void NoRouteStillReachesTheModel()
    {
        // 'none' is declared in the base prompt rather than as a tool, so it needs its own
        // guard against being dropped during a prompt edit.
        Assert.Contains(
            FinanceToolNames.NoTool, OrchestratorAgent.SystemInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolNamesAreStableRouteConstants()
    {
        foreach (AIFunctionDeclaration tool in OrchestratorAgent.ToolDeclarations)
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", tool.Name);
        }
    }

    [Fact]
    public void StatementArgumentsCanBeOmittedForClarificationOrStickyKpi()
    {
        AIFunctionDeclaration statement = OrchestratorAgent.ToolDeclarations.Single(
            tool => tool.Name == FinanceToolNames.StatementTool);
        Assert.True(!statement.JsonSchema.TryGetProperty("required", out JsonElement required)
            || required.GetArrayLength() == 0);
    }

    private sealed class SampleTools
    {
        public SampleTools() => throw new InvalidOperationException("Discovery must not construct tools.");

        [OrchestratorTool("z_tool")]
        [Description("An annotated tool.")]
        public Task<string> AskAsync(
            [Description("The user's question.")] string question,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Discovery must not execute tools.");

        [OrchestratorTool("a_tool")]
        [Description("Another annotated tool.")]
        public string Answer() => throw new InvalidOperationException("Discovery must not execute tools.");

        public string Helper() => throw new InvalidOperationException("Unannotated helper.");

        [OrchestratorTool("private_tool")]
        [Description("Not public.")]
        private string PrivateHelper() => throw new InvalidOperationException("Private helper.");

        [OrchestratorTool("static_tool")]
        [Description("Not an instance method.")]
        public static string StaticHelper() => throw new InvalidOperationException("Static helper.");
    }
}
