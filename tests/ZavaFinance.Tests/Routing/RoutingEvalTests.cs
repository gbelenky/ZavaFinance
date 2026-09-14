// Copyright (c) Microsoft Corporation.

using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Builds the routing agent once for the whole eval run. Constructing it per case would multiply
/// setup cost across the golden set for no benefit; the agent itself carries no per-turn state.
/// </summary>
public sealed class RoutingAgentFixture
{
    public RoutingAgentFixture()
    {
        if (!FoundryTestEnvironment.IsConfigured)
        {
            return;
        }

        var projectClient = new AIProjectClient(
            new Uri(FoundryTestEnvironment.ProjectEndpoint!),
            new DefaultAzureCredential());

        Agent = OrchestratorAgent.CreateRoutingAgent(
            projectClient,
            new FoundryOptions
            {
                ProjectEndpoint = FoundryTestEnvironment.ProjectEndpoint!,
                ModelDeployment = FoundryTestEnvironment.ModelDeployment!
            });
    }

    public AIAgent? Agent { get; }
}

/// <summary>
/// The routing eval.
/// <para>
/// An orchestrator is judged on routing; answer quality belongs to the Copilot Studio agent. These
/// assertions are exact and deterministic rather than LLM-judged, so a reworded tool description
/// that degrades tool selection fails the build instead of reaching Teams.
/// </para>
/// </summary>
public sealed class RoutingEvalTests : IClassFixture<RoutingAgentFixture>
{
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(60);

    private readonly RoutingAgentFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RoutingEvalTests(RoutingAgentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static TheoryData<string> CaseIds
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (RoutingCase testCase in RoutingGoldenSet.Cases)
            {
                data.Add(testCase.Id);
            }

            return data;
        }
    }

    [RequiresFoundryTheory]
    [MemberData(nameof(CaseIds))]
    public async Task RoutesGoldenSetCase(string caseId)
    {
        RoutingCase testCase = RoutingGoldenSet.Cases.Single(c => c.Id == caseId);
        AIAgent agent = _fixture.Agent!;

        using var cts = new CancellationTokenSource(CaseTimeout);
        AgentSession session = await agent.CreateSessionAsync(cts.Token);

        // Replay the prior turns so the model sees the same history a real follow-up would.
        foreach (string priorTurn in testCase.PriorTurns)
        {
            await agent.RunAsync<OrchestratorRoute>(
                priorTurn, session, cancellationToken: cts.Token);
        }

        AgentResponse<OrchestratorRoute> response = await agent.RunAsync<OrchestratorRoute>(
            testCase.Utterance, session, cancellationToken: cts.Token);

        OrchestratorRoute route = response.Result;

        _output.WriteLine($"case      : {testCase.Id}");
        _output.WriteLine($"utterance : {testCase.Utterance}");
        _output.WriteLine($"routed to : {route.Tool}");
        _output.WriteLine($"arguments : {JsonSerializer.Serialize(route)}");

        AssertRoute(testCase, route);
    }

    private static void AssertRoute(RoutingCase testCase, OrchestratorRoute route)
    {
        string context = Context(testCase, route);

        Assert.True(
            string.Equals(testCase.ExpectedTool, route.Tool, StringComparison.OrdinalIgnoreCase),
            $"Wrong tool selected.{context}");

        switch (testCase.ExpectedTool)
        {
            case OrchestratorRoute.KpiInfoTool:
                AssertContains(testCase.KpiContains, route.Kpi, "kpi", context);
                break;

            case OrchestratorRoute.StatementTool:
                AssertStatementArguments(testCase, route, context);
                break;

            case OrchestratorRoute.NoTool:
                Assert.False(
                    string.IsNullOrWhiteSpace(route.Message),
                    $"The 'none' route must carry a message for the user.{context}");
                break;

            case OrchestratorRoute.ExploreFinanceTool:
                // The analytical agent is a natural-language endpoint, so the routed question
                // must be a real sentence rather than a bare noun.
                Assert.False(
                    string.IsNullOrWhiteSpace(route.Question),
                    $"explore_finance must carry the user's question.{context}");
                break;
        }
    }

    private static void AssertStatementArguments(
        RoutingCase testCase, OrchestratorRoute route, string context)
    {
        if (testCase.KpiContains is not null)
        {
            // An inherited KPI may legitimately be omitted: get_statement falls back to the
            // session's most recently explained KPI. Only a *wrong* KPI is a failure.
            bool omitted = string.IsNullOrWhiteSpace(route.Kpi);

            if (!testCase.KpiMayBeInherited || !omitted)
            {
                AssertContains(testCase.KpiContains, route.Kpi, "kpi", context);
            }
        }

        AssertContains(testCase.OrgContains, route.Org, "org", context);

        foreach (string fragment in testCase.DateRangeContains)
        {
            AssertContains(fragment, route.DateRange, "dateRange", context);
        }
    }

    private static void AssertContains(
        string? expectedFragment, string? actual, string field, string context)
    {
        if (expectedFragment is null)
        {
            return;
        }

        Assert.True(
            actual is not null
            && actual.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase),
            $"Argument '{field}' should contain '{expectedFragment}' but was "
            + $"'{actual ?? "<null>"}'.{context}");
    }

    private static string Context(RoutingCase testCase, OrchestratorRoute route) =>
        $"""


        case      : {testCase.Id}
        utterance : {testCase.Utterance}
        history   : {(testCase.PriorTurns.Count == 0
            ? "<none>"
            : string.Join(" | ", testCase.PriorTurns))}
        expected  : {testCase.ExpectedTool}
        actual    : {JsonSerializer.Serialize(route)}
        why       : {testCase.Rationale}
        """;
}
