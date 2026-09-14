// Copyright (c) Microsoft Corporation.

using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>
/// Current GA durable implementation. This boundary can be replaced by a MAF Durable Extension
/// adapter after the extension is GA and its OBO and persistence requirements are proven.
/// </summary>
public sealed class DurableTaskOrchestratorTurnScheduler(
    DurableTaskClient client,
    ILogger<DurableTaskOrchestratorTurnScheduler> logger)
    : IOrchestratorTurnScheduler
{
    public async Task<string> ScheduleAsync(
        string sessionKey,
        string turnId,
        string conversationRecordId,
        string channelId,
        CancellationToken cancellationToken)
    {
        OrchestratorTurnRequest request =
            CreateRequest(sessionKey, turnId, conversationRecordId, channelId);

        string instanceId = InstanceIdFor(sessionKey, turnId);

        // The same inbound activity can reach us more than once — observed in production as two
        // POSTs about 11 seconds apart for a single Teams message, each of which previously
        // started its own orchestration. That ran the routing model twice and, worse, would call
        // the subagent twice: a duplicate question injected into Copilot Studio's own
        // conversation state. A deterministic instance id makes the second delivery collide with
        // the first instead of forking, which is the standard Durable Task dedupe.
        OrchestrationMetadata? existing =
            await client.GetInstanceAsync(instanceId, cancellation: cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Orchestrator turn {InstanceId} already scheduled ({Status}); ignoring duplicate "
                + "delivery.",
                instanceId,
                existing.RuntimeStatus);

            return instanceId;
        }

        try
        {
            return await client.ScheduleNewOrchestrationInstanceAsync(
                TurnOrchestrator.Name,
                request,
                new StartOrchestrationOptions { InstanceId = instanceId },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Lost the race with a concurrent duplicate. The backend rejecting the second create
            // is the guard working, so the turn already in flight is returned rather than
            // failing the delivery the user is waiting on.
            OrchestrationMetadata? raced =
                await client.GetInstanceAsync(instanceId, cancellation: cancellationToken);

            if (raced is null)
            {
                throw;
            }

            logger.LogInformation(
                ex,
                "Orchestrator turn {InstanceId} was created concurrently; reusing it.",
                instanceId);

            return instanceId;
        }
    }

    /// <summary>
    /// Deterministic in the caller's session and the inbound activity, so the same message
    /// always maps to the same orchestration. Both inputs are already hashed — Base32 and hex
    /// respectively — so the id is safe to use verbatim and leaks no conversation identifiers
    /// into the scheduler dashboard.
    /// </summary>
    internal static string InstanceIdFor(string sessionKey, string turnId)
        => $"turn-{sessionKey}-{turnId}";

    internal static OrchestratorTurnRequest CreateRequest(
        string sessionKey,
        string turnId,
        string conversationRecordId,
        string channelId)
        => new()
        {
            SessionKey = sessionKey,
            TurnId = turnId,
            ConversationRecordId = conversationRecordId,
            ChannelId = channelId,
            IdempotencyKey = $"{sessionKey}:{turnId}"
        };
}
