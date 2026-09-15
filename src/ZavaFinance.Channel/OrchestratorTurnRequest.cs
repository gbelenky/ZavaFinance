// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Channel;

/// <summary>
/// Orchestration payload. Carries identifiers only — never an access token, never message
/// content. The session key is already a salted hash, so it is safe to persist in the task
/// hub; the user's question stays out of the hub entirely and is read from the session store
/// by <see cref="OrchestratorChannel.ContinueTurnAsync"/>.
/// </summary>
public sealed class OrchestratorTurnRequest
{
    public required string SessionKey { get; init; }

    /// <summary>
    /// Identifies the pending turn record holding the question text. Deterministic for the
    /// inbound activity, so a replay resolves the same record.
    /// </summary>
    public required string TurnId { get; init; }

    /// <summary>Handle returned by <c>Proactive.StoreConversationAsync</c>.</summary>
    public required string ConversationRecordId { get; init; }

    public required string ChannelId { get; init; }

    /// <summary>
    /// Retained for compatibility with in-flight Durable Task payloads. Delivery state now
    /// uses SessionKey and TurnId directly, including migration of the old completion key.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}
