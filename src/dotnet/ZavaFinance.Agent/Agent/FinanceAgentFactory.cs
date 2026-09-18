using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Configuration;

namespace ZavaFinance.Core.Agent;

public sealed class FinanceAgentFactory(IChatClient client, FoundryOptions options)
{
    public const string SystemInstructions =
        """
        You are Zava Finance. Use native function calling to select at most one finance tool
        and supply its arguments. Do not describe a tool call in prose or return a route object.

        When no tool applies, call none of them and respond briefly with what you can help
        with: get_kpi_info for KPI definitions, get_statement for a specific figure, or
        explore_finance for open-ended finance analysis.

        When a request is genuinely borderline, prefer the cheaper tool that can ask for what
        it is missing. get_statement returns instantly and requests any absent organization or
        period; get_kpi_info spends roughly a minute in a subagent before answering. Choosing
        get_kpi_info wrongly therefore costs the user a long wait and answers a question they
        did not ask, while choosing get_statement wrongly costs one quick clarifying question.
        A KPI named with a retrieval verb — "show me", "give me", "how much", "what were" —
        and no question about its meaning is a request for figures, even with no organization
        or period present.

        You can see the earlier turns of this conversation. Use them only to resolve what
        the user is referring to — a follow-up such as "and for the Nordics?" or "what about
        last quarter?" inherits the KPI, organization and period already established.

        Never answer a KPI or statement question yourself. Never invent missing arguments.
        For an unknown business argument, omit it if optional or supply an empty string.
        Never invent values just to fill function arguments.
        For get_statement, preserve the user's KPI and organization terms verbatim before
        canonicalization; resolve dates using that tool's date-resolution rules.
        Never convert a vague KPI to a particular KPI, drop an unknown date word, or broaden
        a local organization to a region or company. The host resolves catalogue identities
        and handles clarification choices.
        The selected tool's result is returned verbatim. Tool results are withheld from your
        history; result markers contain no financial information and are not evidence of
        success. Always call a tool again for a fresh finance answer.
        """;

    internal AIAgent Create(IReadOnlyList<AIFunction> tools, int historyLimit)
    {
        var history = new FinanceHistory(historyLimit);
        ChatOptions chatOptions = ModelRequestOptions.Create(options.ModelDeployment, options.ReasoningEnabled);
        chatOptions.Instructions = SystemInstructions;
        chatOptions.Tools = [.. tools];
        chatOptions.ToolMode = ChatToolMode.Auto;
        chatOptions.AllowMultipleToolCalls = false;
        var agent = new ChatClientAgent(new SingleModelTurn(client, history), new ChatClientAgentOptions
        {
            Name = "zavafinance",
            ChatHistoryProvider = history,
            ChatOptions = chatOptions
        });
        return agent.AsBuilder().Use(StopAfterToolAsync).Build();
    }

    private static async ValueTask<object?> StopAfterToolAsync(
        AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.FunctionCount != 1 || context.FunctionCallIndex != 0 || context.Iteration != 0)
            throw new ToolSelectionException("Only one finance tool is allowed per turn.");
        context.Terminate = true;
        return await next(context, cancellationToken);
    }

    private sealed class SingleModelTurn(IChatClient inner, FinanceHistory history) : DelegatingChatClient(inner)
    {
        private int _requests;
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) != 1)
                throw new InvalidOperationException("A finance turn permits only one outer model request.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            ChatResponse response = await base.GetResponseAsync(messages, options, timeout.Token);
            // Validate the entire selection before MAF can execute any function.
            history.RecordDecision(response);
            return response;
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Finance turns use non-streaming model requests.");
    }
}
