using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Core.Agent;

/// <summary>Caller-bound conversation commands and MAF finance-agent composition.</summary>
public sealed class OrchestratorAgent(
    FinanceAgentFactory agents,
    ICopilotStudioClientFactory definitions,
    IAgentSessionStore sessions,
    IStatementQueryFactory statements,
    IFabricDataAgentClientFactory analysis,
    FabricOptions fabricOptions,
    OrchestratorOptions options,
    TimeProvider time,
    ILoggerFactory loggers,
    IResolverSearch? search = null,
    ResolverOptions? resolverOptions = null)
{
    internal const int NativeSessionVersion = 2;
    private readonly ILogger _logger = loggers.CreateLogger<OrchestratorAgent>();
    private readonly ConversationTurnGate _turns = new();

    public async Task<string> RunAsync(
        IDownstreamTokenProvider tokenProvider, string sessionKey, string question,
        CancellationToken cancellationToken) =>
        (await RunReplyAsync(tokenProvider, sessionKey, question, null, cancellationToken)).Text;

    public async Task<FinanceReply> RunReplyAsync(
        IDownstreamTokenProvider tokenProvider, string sessionKey, string question,
        ClarificationSubmission? submission, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        using IDisposable gate = await _turns.EnterAsync(sessionKey, cancellationToken);
        OrchestratorSessionState state;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            state = await sessions.LoadAsync(sessionKey, timeout.Token);
        }
        bool reset = submission is null && ClarificationSelection.IsReset(question);
        if (reset) state = new();
        state.ReplyClarification = null;
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var tools = new FinanceTools(
            () => new KpiInfoTool(definitions, tokenProvider, state, options, loggers.CreateLogger<KpiInfoTool>()),
            () => new StatementTool(statements.Create(tokenProvider), state, today,
                loggers.CreateLogger<StatementTool>(), search, sessionKey, time, resolverOptions),
            () => new ExploreFinanceTool(analysis.Create(tokenProvider), fabricOptions,
                loggers.CreateLogger<ExploreFinanceTool>()),
            state, today);
        AIAgent? agent = null;
        AgentSession? session = null;
        try
        {
            if (reset) return new("The conversation has been reset. What would you like to ask?");
            bool textSelection = submission is null
                && ClarificationSelection.TryRead(question, state.PendingClarification, out submission);
            if (submission is not null || textSelection)
            {
                if (state.PendingClarification is null)
                    return new("There is no valid pending choice for that selection. Please ask the statement again.");
                if (submission is null)
                    return new("That selection does not uniquely identify a pending option. Please reply with its option number.");
                string answer = await tools.Statement().ContinueAsync(submission, cancellationToken);
                return new(answer, state.ReplyClarification);
            }
            state.PendingClarification = null;
            agent = agents.Create(tools.Functions(), options.MaxHistoryMessages);
            session = await RestoreAsync(agent, state, cancellationToken);
            AgentResponse response = await agent.RunAsync(question, session, cancellationToken: cancellationToken);
            if (tools.Failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tools.Failure).Throw();
            if (tools.Reply is null && response.Messages.SelectMany(message => message.Contents)
                .OfType<Microsoft.Extensions.AI.FunctionCallContent>().Any())
                throw new InvalidOperationException("The selected finance tool did not produce a reply.");
            return tools.Reply ?? new FinanceReply(response.Text);
        }
        catch (ToolSelectionException)
        {
            _logger.LogWarning("Rejected invalid native finance selection.");
            return new("I could not select a valid finance tool for that request. Please try asking "
                + "one KPI definition, statement, or finance analysis question at a time.");
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (agent is not null && session is not null)
                    state.AgentSessionJson = (await agent.SerializeSessionAsync(session,
                        cancellationToken: timeout.Token)).GetRawText();
                await sessions.SaveAsync(sessionKey, state, timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError("Could not persist finance session. ErrorType={ErrorType}", ex.GetType().Name);
            }
        }
    }

    private async Task<AgentSession> RestoreAsync(
        AIAgent agent, OrchestratorSessionState state, CancellationToken cancellationToken)
    {
        if (state.AgentSessionVersion != NativeSessionVersion)
        {
            state.AgentSessionJson = null;
            state.AgentSessionVersion = NativeSessionVersion;
        }
        if (!string.IsNullOrWhiteSpace(state.AgentSessionJson))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(state.AgentSessionJson);
                return await agent.DeserializeSessionAsync(json.RootElement, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning("Stored finance history could not be restored. ErrorType={ErrorType}", ex.GetType().Name);
                state.AgentSessionJson = null;
            }
        }
        return await agent.CreateSessionAsync(cancellationToken);
    }
}
