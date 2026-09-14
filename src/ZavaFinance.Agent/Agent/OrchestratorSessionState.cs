// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.Storage;

namespace ZavaFinance.Core.Agent;

/// <summary>
/// Per-user, per-conversation state. Persisted under the derived session key, never under the
/// raw conversation id, so two participants of the same Teams group chat never share it.
/// </summary>
public sealed class OrchestratorSessionState : IStoreItem
{
    /// <summary>
    /// The serialized routing conversation.
    /// <para>
    /// This is the one material state change from the durable-entity design: the routing history
    /// lives here, under the caller's own session key in private-endpoint storage, rather than in
    /// the Durable Task hub. The hub is a different access-control boundary — its dashboard
    /// exposes orchestration payloads — so keeping conversation data out of it removes a class of
    /// data-classification problem rather than mitigating one.
    /// </para>
    /// <para>
    /// Tool results and access tokens never enter this history. A previous sourced answer must not
    /// be able to stand in for a fresh tool call.
    /// </para>
    /// </summary>
    public string? AgentSessionJson { get; set; }

    /// <summary>The subagent's own conversation handle. Opaque to us.</summary>
    public string? CopilotStudioConversationId { get; set; }

    /// <summary>
    /// The hosted agent's platform conversation id.
    /// <para>
    /// The Responses endpoint rejects an arbitrary identifier — a session key sent as
    /// <c>conversation.id</c> fails with <c>Malformed identifier</c> — so the conversation is
    /// created through the platform and its <c>conv_…</c> id is stored here, per session key.
    /// Reusing it is what makes the hosted agent's own state sticky across turns: without it
    /// every turn would open a new conversation and lose the latest KPI and the Copilot Studio
    /// handle.
    /// </para>
    /// <para>
    /// It is not an authorization token. The hosted agent still derives its own session key from
    /// the validated assertion, so a caller who guessed this value would still not reach another
    /// user's state.
    /// </para>
    /// </summary>
    public string? HostedAgentConversationId { get; set; }

    /// <summary>
    /// No longer used. The Fabric data agent is reached over MCP, whose calls are
    /// self-contained, so there is no thread handle to persist. Retained so an existing stored
    /// session still deserializes rather than resetting every user's conversation.
    /// </summary>
    [Obsolete("MCP calls are stateless; kept only for backward-compatible deserialization.")]
    public string? FabricThreadId { get; set; }

    /// <summary>Latest KPI successfully resolved by <c>get_kpi_info</c>.</summary>
    public string? LastKpiName { get; set; }

    /// <summary>Tool last used, surfaced to the model as a sticky-routing bias.</summary>
    public string? LastSubagent { get; set; }

    public string? ETag { get; set; }
}

/// <summary>
/// The question for a turn that has been acknowledged but not yet answered.
/// <para>
/// Message content is deliberately kept out of the Durable Task hub: orchestration inputs and
/// outputs are visible in the scheduler dashboard, which is a different access-control
/// boundary from the private-endpoint storage account.
/// </para>
/// </summary>
public sealed class PendingTurn : IStoreItem
{
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// Completed Orchestrator/tool result cached before delivery. A retried durable activity can
    /// reuse it instead of calling Copilot Studio a second time.
    /// </summary>
    public string? Answer { get; set; }

    public string? ETag { get; set; }
}

/// <summary>
/// Loads and saves session state. Store keys are derived inside this type from the session
/// key — callers cannot supply one, so there is no path that addresses another user's state.
/// </summary>
public sealed class OrchestratorSessionStore
{
    private readonly IStorage _storage;

    public OrchestratorSessionStore(IStorage storage) => _storage = storage;

    private static string KeyFor(string sessionKey) => $"orchestrator/{sessionKey}";

    private static string PendingKeyFor(string sessionKey, string turnId)
        => $"pending/{sessionKey}/{turnId}";

    public async Task<OrchestratorSessionState> LoadAsync(
        string sessionKey, CancellationToken cancellationToken)
    {
        string key = KeyFor(sessionKey);

        IDictionary<string, object> items =
            await _storage.ReadAsync([key], cancellationToken);

        return items.TryGetValue(key, out object? value)
            && value is OrchestratorSessionState state
                ? state
                : new OrchestratorSessionState();
    }

    public Task SaveAsync(
        string sessionKey,
        OrchestratorSessionState state,
        CancellationToken cancellationToken)
    {
        // Last writer wins. The state is only written after the user has been answered, and
        // failing the write would discard the conversation history rather than protect it.
        state.ETag = "*";

        var changes = new Dictionary<string, object>
        {
            [KeyFor(sessionKey)] = state
        };

        return _storage.WriteAsync(changes, cancellationToken);
    }

    public Task SavePendingTurnAsync(
        string sessionKey,
        string turnId,
        string question,
        CancellationToken cancellationToken)
    {
        var changes = new Dictionary<string, object>
        {
            [PendingKeyFor(sessionKey, turnId)] = new PendingTurn
            {
                Question = question,
                ETag = "*"
            }
        };

        return _storage.WriteAsync(changes, cancellationToken);
    }

    public async Task<PendingTurn?> ReadPendingTurnAsync(
        string sessionKey, string turnId, CancellationToken cancellationToken)
    {
        string key = PendingKeyFor(sessionKey, turnId);

        IDictionary<string, object> items =
            await _storage.ReadAsync([key], cancellationToken);

        return items.TryGetValue(key, out object? value) && value is PendingTurn pending
            ? pending
            : null;
    }

    public Task SavePendingTurnAnswerAsync(
        string sessionKey,
        string turnId,
        PendingTurn pendingTurn,
        string answer,
        CancellationToken cancellationToken)
    {
        pendingTurn.Answer = answer;
        pendingTurn.ETag = "*";

        var changes = new Dictionary<string, object>
        {
            [PendingKeyFor(sessionKey, turnId)] = pendingTurn
        };

        return _storage.WriteAsync(changes, cancellationToken);
    }

    public Task DeletePendingTurnAsync(
        string sessionKey, string turnId, CancellationToken cancellationToken)
        => _storage.DeleteAsync([PendingKeyFor(sessionKey, turnId)], cancellationToken);

    /// <summary>
    /// Drops the caller's conversation: agent history, the Copilot Studio conversation handle,
    /// and the sticky KPI.
    /// <para>
    /// The subagent handle matters operationally. Republishing a Copilot Studio agent — for
    /// example after changing its knowledge sources — does not affect conversations already in
    /// flight, so a stored handle can keep answering from the previous version. Resetting
    /// forces the next turn to call <c>StartConversationAsync</c> again.
    /// </para>
    /// </summary>
    public Task ResetAsync(string sessionKey, CancellationToken cancellationToken)
        => _storage.DeleteAsync([KeyFor(sessionKey)], cancellationToken);
}
