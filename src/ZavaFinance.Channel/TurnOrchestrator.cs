// Copyright (c) Microsoft Corporation.

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>
/// Coordinates one Orchestrator turn on the durable path.
/// <para>
/// Orchestrator code must be deterministic: no DateTime.Now, no Guid.NewGuid(), no I/O.
/// All real work happens in activities.
/// </para>
/// </summary>
public sealed class TurnOrchestrator
{
    public const string Name = "OrchestratorTurn";

    [Function(Name)]
    public async Task<string> RunAsync(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        OrchestratorTurnRequest request = context.GetInput<OrchestratorTurnRequest>()
            ?? throw new InvalidOperationException("Orchestrator orchestration has no request.");

        ILogger logger = context.CreateReplaySafeLogger<TurnOrchestrator>();

        logger.LogInformation(
            "Orchestrator turn started for session {SessionKey}.", request.SessionKey);

        var retry = TaskOptions.FromRetryPolicy(new RetryPolicy(
            maxNumberOfAttempts: 3,
            firstRetryInterval: TimeSpan.FromSeconds(5),
            backoffCoefficient: 2.0));

        await context.CallActivityAsync<string>(
            OrchestratorActivities.ProcessTurnName, request, retry);

        logger.LogInformation(
            "Orchestrator turn completed for session {SessionKey}.", request.SessionKey);

        return "completed";
    }
}
