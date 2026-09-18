using System.Text.Json.Serialization;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Finance;

namespace ZavaFinance.Core.Agent;

public sealed record StatementArguments(
    string? Kpi, string Org, string DateRange,
    string? SelectedKpiId = null, string? SelectedOrganizationId = null, DateOnly? AsOfDate = null);

public sealed record PendingClarification(
    string RequestId, string Field, string CatalogVersion, StatementArguments Arguments,
    IReadOnlyList<string> CandidateIds, DateTimeOffset ExpiresAtUtc, string OwnerSessionKey,
    ResolverRelease? Release = null, IReadOnlyList<string>? CandidateLabelHashes = null,
    IReadOnlyList<IReadOnlyList<string>>? CandidateTermHashes = null);

/// <summary>Hosted-agent state, isolated by the validated caller and conversation.</summary>
public sealed class OrchestratorSessionState
{
    // Native calls have content-free result markers; finance answers and tokens are never saved.
    public string? AgentSessionJson { get; set; }
    public int AgentSessionVersion { get; set; }
    public string? CopilotStudioConversationId { get; set; }
    public string? LastKpiName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingClarification? PendingClarification { get; set; }
    [JsonIgnore]
    public ClarificationPrompt? ReplyClarification { get; set; }
}

public interface IAgentSessionStore
{
    Task<OrchestratorSessionState> LoadAsync(string sessionKey, CancellationToken cancellationToken);

    Task SaveAsync(
        string sessionKey, OrchestratorSessionState state, CancellationToken cancellationToken);
}
