// Copyright (c) Microsoft Corporation.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.DurableTask;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>
/// Activities are the only place real work happens. They execute <b>at least once</b>, so
/// every side effect here is made idempotent by a deterministic key.
/// </summary>
public sealed class OrchestratorActivities
{
    public const string ProcessTurnName = "ProcessOrchestratorTurn";

    private readonly OrchestratorChannel _channel;
    private readonly IChannelAdapter _adapter;
    private readonly IStorage _storage;
    private readonly ILogger<OrchestratorActivities> _logger;

    public OrchestratorActivities(
        OrchestratorChannel channel,
        IChannelAdapter adapter,
        IStorage storage,
        ILogger<OrchestratorActivities> logger)
    {
        _channel = channel;
        _adapter = adapter;
        _storage = storage;
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

        if (await AlreadyCompletedAsync(request.IdempotencyKey, cancellationToken))
        {
            // Replay after a recorded success: sending again would duplicate the Teams message
            // and inject a duplicate question into the subagent's own conversation state.
            _logger.LogInformation(
                "Skipping replay of already-completed turn {Key}.", request.IdempotencyKey);

            return "skipped";
        }

        await ContinueConversationAsync(request, cancellationToken);

        await MarkCompletedAsync(request.IdempotencyKey, cancellationToken);

        return "sent";
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

        // Only the payload is attached. The activity type and name are left as the SDK
        // produced them, so the [ContinueConversation] route still matches.
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

    private async Task<bool> AlreadyCompletedAsync(string key, CancellationToken ct)
    {
        IDictionary<string, object> items =
            await _storage.ReadAsync([IdempotencyKeyFor(key)], ct);

        return items.Count > 0;
    }

    private Task MarkCompletedAsync(string key, CancellationToken ct)
    {
        var changes = new Dictionary<string, object>
        {
            [IdempotencyKeyFor(key)] = new TurnCompletionRecord()
        };

        return _storage.WriteAsync(changes, ct);
    }

    /// <summary>
    /// Hashed because the raw key contains the session key, and store keys are not a place
    /// to put identity-derived values in the clear.
    /// </summary>
    private static string IdempotencyKeyFor(string key)
        => "turn-complete-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed class TurnCompletionRecord : IStoreItem
    {
        public string? ETag { get; set; }
    }
}
