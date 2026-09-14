// Copyright (c) Microsoft Corporation.

using System.Text.Json;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Core.Agent;

/// <summary>
/// The Microsoft Agent Framework routing agent.
/// <para>
/// The model <b>routes</b>; it never writes the answer. It selects one tool and extracts that
/// tool's arguments, and the selected tool's output is returned verbatim through
/// <see cref="ToolPassthrough"/>. Registering real functions on <c>ChatOptions.Tools</c> would let
/// the framework invoke them and feed their output back through the model, which measurably
/// destroys sourced answers — a 2,827-character cited reply came back as 1,119 characters with the
/// citation dropped, while the model was instructed to return it verbatim.
/// </para>
/// <para>
/// The host supplies an <see cref="IDownstreamTokenProvider"/>, so the same routing and the same
/// tools run unchanged whether the caller arrived through the Teams / Microsoft 365 Copilot
/// channel or through the Foundry hosted agent.
/// </para>
/// </summary>
public sealed class OrchestratorAgent
{
    public const string AgentName = "zavafinance";

    private static readonly string SystemPrompt =
        $"""
        You are an orchestrator. Your only job is to select exactly one route and extract its
        arguments. Return the requested structured response and no prose outside it.

        Available tools:
        {OrchestratorToolCatalog.BuildPromptSection()}

        - Select none only when no tool applies. Set message to a brief description of
          what you can help with.

        When a request is genuinely borderline, prefer the cheaper tool that can ask for what
        it is missing. get_statement returns instantly and requests any absent organization or
        period; get_kpi_info spends roughly a minute in a subagent before answering. Choosing
        get_kpi_info wrongly therefore costs the user a long wait *and* answers a question they
        did not ask, while choosing get_statement wrongly costs one quick clarifying question.
        A KPI named with a retrieval verb — "show me", "give me", "how much", "what were" —
        and no question about its meaning is a request for figures, even with no organization
        or period present.

        You can see the earlier turns of this conversation. Use them **only** to resolve what
        the user is referring to — a follow-up such as "and for the Nordics?" or "what about
        last quarter?" inherits the KPI, organization and period already established.

        Never answer a KPI or statement question yourself. Never invent missing arguments.
        The host executes the selected tool after your routing turn and returns the tool
        result verbatim.
        """;

    private readonly ICopilotStudioClientFactory _clientFactory;
    private readonly OrchestratorSessionStore _sessionStore;
    private readonly IStatementQueryFactory _statementQueryFactory;
    private readonly IFabricDataAgentClientFactory _dataAgentFactory;
    private readonly FabricOptions _fabricOptions;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorAgent> _logger;

    public OrchestratorAgent(
        ICopilotStudioClientFactory clientFactory,
        OrchestratorSessionStore sessionStore,
        IStatementQueryFactory statementQueryFactory,
        IFabricDataAgentClientFactory dataAgentFactory,
        FabricOptions fabricOptions,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _sessionStore = sessionStore;
        _statementQueryFactory = statementQueryFactory;
        _dataAgentFactory = dataAgentFactory;
        _fabricOptions = fabricOptions;
        _options = options;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OrchestratorAgent>();
    }

    public static string SystemInstructions => SystemPrompt;

    /// <summary>
    /// Builds the routing agent. Both hosts and the routing eval construct it through this one
    /// factory, so all three exercise a single definition — a golden-set run that measured a
    /// differently configured agent would not gate anything.
    /// </summary>
    public static AIAgent CreateRoutingAgent(
        AIProjectClient projectClient, FoundryOptions foundryOptions) =>
        projectClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new ChatOptions
            {
                ModelId = foundryOptions.ModelDeployment,
                Instructions = SystemInstructions,

                // Routing is a classification, not a generation. Sampling was observed to flip
                // borderline utterances between tools across identical runs, which means the same
                // question can cost a ~51 s subagent call on one turn and not the next. It also
                // makes the golden set a coin flip instead of a gate.
                Temperature = 0
            }
        });

    public async Task<string> RunAsync(
        AIAgent routingAgent,
        IDownstreamTokenProvider tokenProvider,
        string sessionKey,
        string question,
        Func<string?, CancellationToken, Task> onRouteSelected,
        CancellationToken cancellationToken)
    {
        OrchestratorSessionState state =
            await _sessionStore.LoadAsync(sessionKey, cancellationToken);

        AgentSession session =
            await LoadSessionAsync(routingAgent, state, cancellationToken);

        try
        {
            AgentResponse<OrchestratorRoute> response =
                await routingAgent.RunAsync<OrchestratorRoute>(
                    question, session, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Router selected {Tool}.", response.Result.Tool);

            await onRouteSelected(response.Result.Tool, cancellationToken);

            return await ExecuteRouteAsync(
                response.Result, tokenProvider, state, cancellationToken);
        }
        finally
        {
            // The serialized session carries the routing conversation only. Tool results and
            // access tokens never enter it.
            await PersistSessionAsync(routingAgent, session, state, cancellationToken);

            try
            {
                await _sessionStore.SaveAsync(sessionKey, state, cancellationToken);
            }
            catch (Exception ex)
            {
                // Never let a state write destroy an answer the user has already waited for.
                // This ran in a finally block, so a throw here replaced a completed subagent
                // reply with a generic failure — observed once as a 1,517-character sourced
                // answer discarded because a store write rejected its payload.
                //
                // The cost of swallowing it is a lost sticky KPI and a new Copilot Studio
                // conversation on the next turn. The cost of rethrowing is a minute of the
                // user's time and a subagent call that has to be made again.
                _logger.LogError(
                    ex, "Could not persist session state. The answer is delivered regardless.");
            }
        }
    }

    private async Task<string> ExecuteRouteAsync(
        OrchestratorRoute route,
        IDownstreamTokenProvider tokenProvider,
        OrchestratorSessionState state,
        CancellationToken cancellationToken)
    {
        var passthrough = new ToolPassthrough();
        var kpiTool = new KpiInfoTool(
            _clientFactory,
            tokenProvider,
            state,
            passthrough,
            _options,
            _loggerFactory.CreateLogger<KpiInfoTool>());

        // Constructed per call because every Fabric surface needs the caller's own delegated
        // token. An app-only token would bypass row-level security and show every user the
        // same data.
        var statementTool = new StatementTool(
            _statementQueryFactory.Create(tokenProvider),
            state,
            passthrough,
            DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime),
            _loggerFactory.CreateLogger<StatementTool>());

        var exploreTool = new ExploreFinanceTool(
            _dataAgentFactory.Create(tokenProvider),
            state,
            passthrough,
            _fabricOptions,
            _loggerFactory.CreateLogger<ExploreFinanceTool>());

        switch (route.Tool?.Trim().ToLowerInvariant())
        {
            case OrchestratorRoute.KpiInfoTool:
                return await kpiTool.GetKpiInfoAsync(
                    route.Kpi ?? string.Empty, cancellationToken);

            case OrchestratorRoute.StatementTool:
                return await statementTool.GetStatementAsync(
                    route.Kpi,
                    route.Org ?? string.Empty,
                    route.DateRange ?? string.Empty,
                    cancellationToken);

            case OrchestratorRoute.ExploreFinanceTool:
                return await exploreTool.ExploreFinanceAsync(
                    route.Question ?? string.Empty, cancellationToken);

            case OrchestratorRoute.NoTool:
                return string.IsNullOrWhiteSpace(route.Message)
                    ? "I can explain a KPI, report a figure for an organization and period, or "
                      + "analyse a finance question."
                    : route.Message;

            default:
                _logger.LogError(
                    "Router returned unsupported route {Tool}.", route.Tool);
                return "I could not determine which data source should answer that request.";
        }
    }

    private async Task<AgentSession> LoadSessionAsync(
        AIAgent agent, OrchestratorSessionState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.AgentSessionJson))
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(state.AgentSessionJson);

            return await agent.DeserializeSessionAsync(
                document.RootElement, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A stored session that a newer agent shape can no longer read must not brick the
            // conversation. Start a fresh one and keep answering.
            _logger.LogWarning(
                ex, "Stored agent session could not be restored. Starting a new session.");

            state.AgentSessionJson = null;

            return await agent.CreateSessionAsync(cancellationToken);
        }
    }

    private async Task PersistSessionAsync(
        AIAgent agent,
        AgentSession session,
        OrchestratorSessionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement serialized = await agent.SerializeSessionAsync(
                session, cancellationToken: cancellationToken);

            state.AgentSessionJson = serialized.GetRawText();

            _logger.LogInformation(
                "Persisted agent session. Bytes={Bytes}", state.AgentSessionJson.Length);
        }
        catch (Exception ex)
        {
            // Losing history is recoverable; failing the turn after the user has already been
            // acknowledged is not.
            _logger.LogError(ex, "Could not serialize the agent session.");
        }
    }
}
