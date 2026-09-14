// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Channel;

/// <summary>
/// Schedules the durable execution of one acknowledged Orchestrator turn.
/// </summary>
/// <remarks>
/// Message content is deliberately absent from this contract. The question remains in the
/// private session store, so changing durable implementations cannot accidentally copy it
/// into orchestration history.
/// </remarks>
public interface IOrchestratorTurnScheduler
{
    Task<string> ScheduleAsync(
        string sessionKey,
        string turnId,
        string conversationRecordId,
        string channelId,
        CancellationToken cancellationToken);
}
