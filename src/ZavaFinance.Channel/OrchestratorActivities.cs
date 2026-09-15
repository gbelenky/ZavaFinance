// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>
/// Executes at least once. The channel delivery record suppresses completed replays and caches
/// answers, but an external send and the Delivered write cannot be committed atomically.
/// </summary>
public sealed class OrchestratorActivities
{
    public const string ProcessTurnName = "ProcessOrchestratorTurn";

    private readonly OrchestratorChannel _channel;
    private readonly IChannelAdapter _adapter;
    private readonly ChannelSessionStore _store;
    private readonly ILogger<OrchestratorActivities> _logger;

    public OrchestratorActivities(
        OrchestratorChannel channel,
        IChannelAdapter adapter,
        ChannelSessionStore store,
        ILogger<OrchestratorActivities> logger)
    {
        _channel = channel;
        _adapter = adapter;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Resumes the user's conversation, which yields a real turn context and therefore access to
    /// the caller's Teams SSO token, calls the hosted agent, and sends the answer.
    /// </summary>
    [Function(ProcessTurnName)]
    public async Task<string> RunAsync(
        [ActivityTrigger] OrchestratorTurnRequest request,
        FunctionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;

        try
        {
            TurnDeliveryRecord? turn = await _store.ReadTurnAsync(
                request.SessionKey, request.TurnId, cancellationToken);
            if (turn?.Status == TurnDeliveryStatus.Delivered)
            {
                return "skipped";
            }

            await ContinueConversationAsync(request, cancellationToken);
            turn = await _store.ReadTurnAsync(request.SessionKey, request.TurnId, cancellationToken);
            if (turn?.Status != TurnDeliveryStatus.Delivered)
            {
                // Auto-sign-in may return without running the callback. That is not delivery.
                throw new InvalidOperationException("The continued turn did not deliver its answer.");
            }

            return "sent";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Channel turn cancelled.", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel turn processing or delivery failed.");
            // Durable failure payloads must not contain downstream bodies, questions or tokens.
            throw new InvalidOperationException("Channel turn failed. See the channel's restricted logs.");
        }
    }

    /// <summary>
    /// Resumes the stored conversation.
    /// <para>
    /// The continuation activity must be built <b>from the stored conversation reference</b>.
    /// A bare event activity carries no <c>ConversationAccount</c>, and the adapter rejects it
    /// before the handler ever runs — which the user experiences as an acknowledgement
    /// followed by silence.
    /// </para>
    /// </summary>
    private async Task ContinueConversationAsync(
        OrchestratorTurnRequest request,
        CancellationToken cancellationToken)
    {
        Conversation? conversation = await _channel.Proactive.GetConversationWithThrowAsync(
            request.ConversationRecordId, cancellationToken);

        ConversationReference reference = conversation?.Reference
            ?? throw new InvalidOperationException(
                $"Stored conversation '{request.ConversationRecordId}' has no reference, so "
                + "the turn cannot be resumed.");

        IActivity continuation = reference.GetContinuationActivity();

        // Keep the SDK continuation activity and attach only the identifier payload.
        continuation.Value = request;

        await _channel.Proactive.ContinueConversationAsync(
            _adapter,
            conversation,
            (turnContext, turnState, ct) =>
                _channel.ContinueTurnAsync(turnContext, turnState, ct),
            autoSignInHandlers: ["mcs"],
            continuationActivity: continuation,
            cancellationToken: cancellationToken);
    }
}
