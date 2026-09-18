using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ZavaFinance.Core.Agent;

internal sealed class FinanceHistory(int limit) : ChatHistoryProvider
{
    private readonly InMemoryChatHistoryProvider _memory = new();
    public override IReadOnlyList<string> StateKeys => _memory.StateKeys;
    internal IEnumerable<ChatMessage> GetMessages(AgentSession session) => _memory.GetMessages(session);
    private void SetMessages(AgentSession session, List<ChatMessage> messages) => _memory.SetMessages(session, messages);
    internal const string WithheldResult =
        "Application handling requested. No execution result is supplied to the model.";
    private AgentSession? _session;

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context.Session);
        _session = context.Session;
        List<ChatMessage> prior = Trim(Sanitize(GetMessages(context.Session)), limit - 1);
        SetMessages(context.Session, [.. prior, .. context.RequestMessages
            .Where(message => message.Role == ChatRole.User)
            .Select(message => new ChatMessage(ChatRole.User, message.Text))]);
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(prior);
    }

    internal void RecordDecision(ChatResponse response)
    {
        FunctionCallContent? call = FinanceToolCatalog.Validate(response.Messages);
        List<ChatMessage> messages = GetMessages(_session!).ToList();
        if (call is null)
            messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
        else
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(call.CallId, call.Name, call.Arguments)]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, WithheldResult)]));
        }
        SetMessages(_session!, Trim(messages, limit));
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static List<ChatMessage> Sanitize(IEnumerable<ChatMessage> stored)
    {
        var safe = new List<ChatMessage>();
        bool hasUser = false;
        foreach (ChatMessage message in stored)
        {
            if (message.Role == ChatRole.User)
            {
                safe.Add(new(ChatRole.User, message.Text));
                hasUser = true;
            }
            else if (hasUser && message.Role == ChatRole.Assistant)
            {
                FunctionCallContent? call;
                try { call = FinanceToolCatalog.Validate([message]); }
                catch (ToolSelectionException) { continue; }
                if (call is null)
                    safe.Add(new(ChatRole.Assistant, message.Text));
                else
                {
                    safe.Add(new(ChatRole.Assistant,
                        [new FunctionCallContent(call.CallId, call.Name, call.Arguments)]));
                    safe.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, WithheldResult)]));
                }
            }
        }
        return safe;
    }

    private static List<ChatMessage> Trim(List<ChatMessage> messages, int limit)
    {
        int start = Math.Max(0, messages.Count - limit);
        while (start < messages.Count && messages[start].Role != ChatRole.User) start++;
        return messages.GetRange(start, messages.Count - start);
    }
}
