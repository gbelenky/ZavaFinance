// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Core.Agent;

/// <summary>
/// Native function selection with host-controlled execution and verbatim tool responses.
/// <para>
/// The model selects a native function and supplies its arguments. The host executes it with
/// the caller's identity and returns its output directly, without a second model generation
/// pass. Function declarations expose schemas without granting automatic invocation.
/// </para>
/// <para>
/// The hosted handler supplies a caller-bound <see cref="IDownstreamTokenProvider"/>.
/// Channel callers and direct Responses API callers use this same hosted execution path.
/// </para>
/// </summary>
public sealed class OrchestratorAgent
{
    public const string AgentName = "zavafinance";
    internal const int NativeSessionVersion = 1;
    internal const string WithheldResult =
        "Application handling requested. No execution result is supplied to the model.";

    private const string SystemPrompt =
        """
        You are Zava Finance. Use native function calling to select at most one finance tool
        and supply its arguments. Do not describe a tool call in prose or return a route object.

        When no tool applies, call none of them and respond briefly with what you can help
        with: get_kpi_info for KPI definitions, get_statement for a specific figure, or
        explore_finance for open-ended finance analysis.

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
        For an unknown business argument, omit it if optional or supply an empty string.
        Never invent values just to fill function arguments.
        For get_statement, extract the user's KPI, organization and complete date terms
        verbatim before canonicalization. Never convert a vague KPI to a particular KPI, drop
        an unknown date word, or broaden a local organization to a region or company.
        The host resolves catalogue identities and handles clarification choices.
        The host executes the selected tool and returns its result verbatim. Tool results
        are withheld from your history; result markers contain no financial information and
        are not evidence of success. Always call a tool again for a fresh finance answer.
        """;

    private readonly ICopilotStudioClientFactory _clientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IStatementQueryFactory _statementQueryFactory;
    private readonly IFabricDataAgentClientFactory _dataAgentFactory;
    private readonly FabricOptions _fabricOptions;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly IResolverSearch? _resolverSearch;
    private readonly ResolverOptions _resolverOptions;
    private readonly Dictionary<string, TurnGate> _turnLocks = new(StringComparer.Ordinal);

    private sealed class TurnGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    public OrchestratorAgent(
        ICopilotStudioClientFactory clientFactory,
        IAgentSessionStore sessionStore,
        IStatementQueryFactory statementQueryFactory,
        IFabricDataAgentClientFactory dataAgentFactory,
        FabricOptions fabricOptions,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IResolverSearch? resolverSearch = null,
        ResolverOptions? resolverOptions = null)
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
        _resolverSearch = resolverSearch;
        _resolverOptions = resolverOptions ?? new ResolverOptions();
    }

    public static string SystemInstructions => SystemPrompt;

    internal static IReadOnlyList<AIFunctionDeclaration> ToolDeclarations { get; } =
        DiscoverToolDeclarations(typeof(KpiInfoTool), typeof(StatementTool), typeof(ExploreFinanceTool));

    // Only annotated methods on these explicitly supplied classes become model-visible tools.
    internal static IReadOnlyList<AIFunctionDeclaration> DiscoverToolDeclarations(params Type[] toolTypes) =>
        Array.AsReadOnly(DiscoverToolMethods(toolTypes)
            .Select(DeclareTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray());

    private static IEnumerable<MethodInfo> DiscoverToolMethods(params Type[] toolTypes) =>
        toolTypes.SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(OrchestratorToolAttribute), inherit: false));

    /// <summary>
    /// Builds the same routing agent for hosted execution and the routing evaluation.
    /// </summary>
    public static AIAgent CreateRoutingAgent(
        AIProjectClient projectClient, FoundryOptions foundryOptions) =>
        projectClient.AsAIAgent(CreateAgentOptions(foundryOptions));

    internal static AIAgent CreateRoutingAgent(
        IChatClient chatClient, FoundryOptions foundryOptions) =>
        new ChatClientAgent(chatClient, CreateAgentOptions(foundryOptions));

    private static ChatClientAgentOptions CreateAgentOptions(FoundryOptions foundryOptions)
    {
        ChatOptions chatOptions = ModelRequestOptions.Create(
            foundryOptions.ModelDeployment, foundryOptions.ReasoningEnabled);
        chatOptions.Instructions = SystemInstructions;
        chatOptions.Tools = [.. ToolDeclarations];
        chatOptions.ToolMode = ChatToolMode.Auto;
        chatOptions.AllowMultipleToolCalls = false;

        return new()
        {
            Name = AgentName,
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = chatOptions
        };
    }

    private static AIFunctionDeclaration DeclareTool(MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<OrchestratorToolAttribute>()
            ?? throw new InvalidOperationException($"Tool method '{method.Name}' has no tool name.");
        string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"Tool '{attribute.Name}' has no description.");
        }

        foreach (ParameterInfo parameter in method.GetParameters()
                     .Where(parameter => parameter.ParameterType != typeof(CancellationToken)))
        {
            if (string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            {
                throw new InvalidOperationException(
                    $"Parameter '{parameter.Name}' of tool '{attribute.Name}' has no description.");
            }
        }

        // Schemas only: no tool instance or delegated credentials are bound to the model.
        return AIFunctionFactory.CreateDeclaration(
            attribute.Name, description, AIJsonUtilities.CreateFunctionJsonSchema(method));
    }

    public async Task<string> RunAsync(
        AIAgent routingAgent,
        IDownstreamTokenProvider tokenProvider,
        string sessionKey,
        string question,
        CancellationToken cancellationToken)
        => (await RunReplyAsync(routingAgent, tokenProvider, sessionKey, question,
            submission: null, cancellationToken)).Text;

    public async Task<FinanceReply> RunReplyAsync(
        AIAgent routingAgent, IDownstreamTokenProvider tokenProvider,
        string sessionKey, string question, ClarificationSubmission? submission,
        CancellationToken cancellationToken)
    {
        TurnGate gate;
        lock (_turnLocks)
        {
            if (!_turnLocks.TryGetValue(sessionKey, out gate!))
                _turnLocks.Add(sessionKey, gate = new TurnGate());
            gate.Users++;
        }
        bool entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            return await RunReplyCoreAsync(routingAgent, tokenProvider, sessionKey, question,
                submission, cancellationToken);
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (_turnLocks)
            {
                if (--gate.Users == 0)
                {
                    _turnLocks.Remove(sessionKey);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task<FinanceReply> RunReplyCoreAsync(
        AIAgent routingAgent, IDownstreamTokenProvider tokenProvider,
        string sessionKey, string question, ClarificationSubmission? submission,
        CancellationToken cancellationToken)
    {
        OrchestratorSessionState state =
            await _sessionStore.LoadAsync(sessionKey, cancellationToken);
        bool reset = submission is null && ClarificationSelection.IsReset(question);
        if (reset) state = new OrchestratorSessionState();

        AgentSession session =
            await LoadSessionAsync(routingAgent, state, cancellationToken);

        try
        {
            if (reset) return new FinanceReply("The conversation has been reset. What would you like to ask?");
            bool textSelection = submission is null
                && ClarificationSelection.TryRead(question, state.PendingClarification, out submission);
            if (submission is not null || textSelection)
            {
                if (state.PendingClarification is null)
                    return new FinanceReply("There is no valid pending choice for that selection. Please ask the statement again.");
                if (submission is null)
                    return new FinanceReply("That selection does not uniquely identify a pending option. Please reply with its option number.");
                string text = await CreateStatementTool(tokenProvider, state, sessionKey)
                    .ContinueAsync(submission, cancellationToken);
                return new FinanceReply(text, state.ReplyClarification);
            }
            // A new request supersedes old cards; the next selection cannot resume old arguments.
            state.PendingClarification = null;
            state.ReplyClarification = null;
            AgentResponse response = await SelectToolAsync(
                routingAgent, question, session, cancellationToken, _options.MaxHistoryMessages);
            FunctionCallContent? call = response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>().SingleOrDefault();
            string toolName = call?.Name ?? FinanceToolNames.NoTool;

            _logger.LogInformation(
                "Model selected {Tool}.", toolName);

            cancellationToken.ThrowIfCancellationRequested();
            string answer = call is not null
                ? await InvokeToolAsync(call, CreateToolFactories(tokenProvider, state, sessionKey), cancellationToken)
                : response.Text;
            return new FinanceReply(answer, state.ReplyClarification);
        }
        catch (ToolSelectionException ex)
        {
            _logger.LogWarning(ex, "Rejected invalid native function selection.");
            return new FinanceReply("I could not select a valid finance tool for that request. Please try asking "
                + "one KPI definition, statement, or finance analysis question at a time.");
        }
        finally
        {
            // Complete local persistence even when tool execution was cancelled.
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await PersistSessionAsync(routingAgent, session, state, persistenceTimeout.Token);

            try
            {
                await _sessionStore.SaveAsync(sessionKey, state, persistenceTimeout.Token);
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

    internal static async Task<AgentResponse> SelectToolAsync(
        AIAgent agent, string question, AgentSession session, CancellationToken cancellationToken,
        int maxHistoryMessages = OrchestratorOptions.DefaultMaxHistoryMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHistoryMessages, 3);
        if (agent is not ChatClientAgent { ChatHistoryProvider: InMemoryChatHistoryProvider history })
        {
            throw new InvalidOperationException("Native tool selection requires local chat history.");
        }

        List<ChatMessage> messages = TrimHistory(history.GetMessages(session).ToList(), maxHistoryMessages - 1);
        history.SetMessages(session, messages.ToList());
        messages.Add(new ChatMessage(ChatRole.User, question));
        try
        {
            AgentResponse response = await agent.RunAsync(
                question, session, cancellationToken: cancellationToken);
            FunctionCallContent? call = ValidateToolCall(response);

            if (call is not null)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, [call]));
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, WithheldResult)]));
            }
            else
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            }

            return response;
        }
        finally
        {
            // Replace the SDK's automatic response history with validated calls and paired,
            // content-free markers. Rejected calls and incidental model prose are not replayed.
            // This is a local update, not a second model request or a claim of tool success.
            history.SetMessages(session, TrimHistory(messages, maxHistoryMessages));
        }
    }

    private static List<ChatMessage> TrimHistory(List<ChatMessage> messages, int limit)
    {
        int start = Math.Max(0, messages.Count - limit);
        // Drop whole turns, never leave a native call without its matching result.
        while (start < messages.Count && messages[start].Role != ChatRole.User)
        {
            start++;
        }
        return messages.GetRange(start, messages.Count - start);
    }

    internal static FunctionCallContent? ValidateToolCall(AgentResponse response)
    {
        FunctionCallContent[] calls = [.. response.Messages
            .SelectMany(message => message.Contents).OfType<FunctionCallContent>()];

        if (calls.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                throw new ToolSelectionException("The model returned neither a function call nor a response.");
            }

            return null;
        }

        if (calls.Length != 1)
        {
            throw new ToolSelectionException("Only one finance function may be called per turn.");
        }

        FunctionCallContent call = calls[0];
        AIFunctionDeclaration tool = ToolDeclarations
            .SingleOrDefault(tool => string.Equals(tool.Name, call.Name, StringComparison.Ordinal))
            ?? throw new ToolSelectionException("The model selected an unknown function.");

        if (call.InformationalOnly || call.Exception is not null || string.IsNullOrWhiteSpace(call.CallId))
        {
            throw new ToolSelectionException("The model returned a malformed function call.");
        }

        JsonElement properties = tool.JsonSchema.GetProperty("properties");
        if (call.Arguments is not null)
        {
            foreach ((string name, object? value) in call.Arguments)
            {
                if (!properties.TryGetProperty(name, out JsonElement schema))
                {
                    throw new ToolSelectionException("The function call contains an unknown argument.");
                }

                bool isNull = value is null || value is JsonElement { ValueKind: JsonValueKind.Null };
                JsonElement type = schema.GetProperty("type");
                bool allowsNull = type.ValueKind == JsonValueKind.Array
                    && type.EnumerateArray().Any(item => item.GetString() == "null");

                if (isNull ? !allowsNull : value is not string
                    && value is not JsonElement { ValueKind: JsonValueKind.String })
                {
                    throw new ToolSelectionException("A function argument has an invalid type.");
                }
            }
        }

        if (tool.JsonSchema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement parameter in required.EnumerateArray())
            {
                if (call.Arguments is null || !call.Arguments.ContainsKey(parameter.GetString()!))
                {
                    throw new ToolSelectionException("The function call is missing a required argument.");
                }
            }
        }

        return call;
    }

    private Dictionary<Type, Func<object>> CreateToolFactories(
        IDownstreamTokenProvider tokenProvider,
        OrchestratorSessionState state,
        string sessionKey)
    {
        // Factories are local to this turn; only the selected tool and its client are created.
        return new()
        {
            [typeof(KpiInfoTool)] = () => new KpiInfoTool(
                _clientFactory, tokenProvider, state, _options,
                _loggerFactory.CreateLogger<KpiInfoTool>()),
            [typeof(StatementTool)] = () => CreateStatementTool(tokenProvider, state, sessionKey),
            [typeof(ExploreFinanceTool)] = () => new ExploreFinanceTool(
                _dataAgentFactory.Create(tokenProvider), _fabricOptions,
                _loggerFactory.CreateLogger<ExploreFinanceTool>())
        };
    }

    private StatementTool CreateStatementTool(
        IDownstreamTokenProvider tokenProvider, OrchestratorSessionState state, string sessionKey) =>
        new(_statementQueryFactory.Create(tokenProvider), state,
            DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime),
            _loggerFactory.CreateLogger<StatementTool>(), _resolverSearch, sessionKey,
            _timeProvider, _resolverOptions);

    internal static async Task<string> InvokeToolAsync(
        FunctionCallContent call,
        IReadOnlyDictionary<Type, Func<object>> toolFactories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MethodInfo method = DiscoverToolMethods([.. toolFactories.Keys])
            .SingleOrDefault(method => method.GetCustomAttribute<OrchestratorToolAttribute>()!.Name == call.Name)
            ?? throw new ToolSelectionException("The selected function has no registered implementation.");

        object target = toolFactories[method.DeclaringType!]();
        AIFunction function = AIFunctionFactory.Create(method, target, new AIFunctionFactoryOptions
        {
            Name = call.Name,
            // Return the method's string itself, not the SDK's default JSON-serialized result.
            MarshalResult = (result, _, _) => ValueTask.FromResult(result)
        });
        object? result = await function.InvokeAsync(
            new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()), cancellationToken);

        return result is string answer
            ? answer
            : throw new InvalidOperationException($"Tool '{call.Name}' did not return a text response.");
    }

    private async Task<AgentSession> LoadSessionAsync(
        AIAgent agent, OrchestratorSessionState state, CancellationToken cancellationToken)
    {
        if (state.AgentSessionVersion != NativeSessionVersion)
        {
            // Old structured routing may hold a server conversation handle. Start native
            // history locally, but retain the caller's sticky KPI and subagent conversation.
            if (!string.IsNullOrWhiteSpace(state.AgentSessionJson))
            {
                _logger.LogInformation("Starting local native history for an older session.");
            }
            state.AgentSessionJson = null;
            state.AgentSessionVersion = NativeSessionVersion;
        }

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

internal sealed class ToolSelectionException(string message) : Exception(message);
