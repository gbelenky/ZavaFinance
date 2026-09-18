// Copyright (c) Microsoft Corporation.

using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
                ModelDeployment = FoundryTestEnvironment.ModelDeployment!,
                ReasoningEnabled = FoundryTestEnvironment.ReasoningEnabled
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
            await OrchestratorAgent.SelectToolAsync(agent, priorTurn, session, cts.Token);
        }

        AgentResponse response = await OrchestratorAgent.SelectToolAsync(
            agent, testCase.Utterance, session, cts.Token);
        FunctionCallContent? call = response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>().SingleOrDefault();

        _output.WriteLine($"case      : {testCase.Id}");
        _output.WriteLine($"utterance : {testCase.Utterance}");
        _output.WriteLine($"routed to : {call?.Name ?? FinanceToolNames.NoTool}");
        _output.WriteLine($"arguments : {JsonSerializer.Serialize(call?.Arguments)}");

        AssertRoute(testCase, call, response.Text);
    }

    private static void AssertRoute(RoutingCase testCase, FunctionCallContent? call, string message)
    {
        string context = Context(testCase, call);

        Assert.True(
            string.Equals(testCase.ExpectedTool, call?.Name ?? FinanceToolNames.NoTool,
                StringComparison.OrdinalIgnoreCase),
            $"Wrong tool selected.{context}");

        switch (testCase.ExpectedTool)
        {
            case FinanceToolNames.KpiInfoTool:
                AssertContains(testCase.KpiContains, Argument(call, "kpi"), "kpi", context);
                break;

            case FinanceToolNames.StatementTool:
                AssertStatementArguments(testCase, call, context);
                break;

            case FinanceToolNames.NoTool:
                Assert.False(
                    string.IsNullOrWhiteSpace(message),
                    $"The 'none' route must carry a message for the user.{context}");
                break;

            case FinanceToolNames.ExploreFinanceTool:
                // The analytical agent is a natural-language endpoint, so the routed question
                // must be a real sentence rather than a bare noun.
                Assert.False(
                    string.IsNullOrWhiteSpace(Argument(call, "question")),
                    $"explore_finance must carry the user's question.{context}");
                break;
        }
    }

    private static void AssertStatementArguments(
        RoutingCase testCase, FunctionCallContent? call, string context)
    {
        if (testCase.KpiContains is not null)
        {
            // An inherited KPI may legitimately be omitted: get_statement falls back to the
            // session's most recently explained KPI. Only a *wrong* KPI is a failure.
            bool omitted = string.IsNullOrWhiteSpace(Argument(call, "kpi"));

            if (!testCase.KpiMayBeInherited || !omitted)
            {
                AssertContains(testCase.KpiContains, Argument(call, "kpi"), "kpi", context);
            }
        }

        AssertContains(testCase.OrgContains, Argument(call, "org"), "org", context);

        foreach (string fragment in testCase.DateRangeContains)
        {
            AssertContains(fragment, Argument(call, "dateRange"), "dateRange", context);
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

    private static string? Argument(FunctionCallContent? call, string name) =>
        call?.Arguments?.TryGetValue(name, out object? value) == true
            ? value?.ToString()
            : null;

    private static string Context(RoutingCase testCase, FunctionCallContent? call) =>
        $"""


        case      : {testCase.Id}
        utterance : {testCase.Utterance}
        history   : {(testCase.PriorTurns.Count == 0
            ? "<none>"
            : string.Join(" | ", testCase.PriorTurns))}
        expected  : {testCase.ExpectedTool}
        actual    : {call?.Name ?? FinanceToolNames.NoTool} {JsonSerializer.Serialize(call?.Arguments)}
        why       : {testCase.Rationale}
        """;
}
