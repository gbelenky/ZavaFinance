namespace ZavaFinance.Core.Agent;

/// <summary>Hosted-agent state, isolated by the validated caller and conversation.</summary>
public sealed class OrchestratorSessionState
{
    // Native calls have content-free result markers; finance answers and tokens are never saved.
    public string? AgentSessionJson { get; set; }
    public int AgentSessionVersion { get; set; }
    public string? CopilotStudioConversationId { get; set; }
    public string? LastKpiName { get; set; }
}

public interface IAgentSessionStore
{
    Task<OrchestratorSessionState> LoadAsync(string sessionKey, CancellationToken cancellationToken);

    Task SaveAsync(
        string sessionKey, OrchestratorSessionState state, CancellationToken cancellationToken);
}
