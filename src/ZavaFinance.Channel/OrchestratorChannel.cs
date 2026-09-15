// Copyright (c) Microsoft Corporation.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.Channel;

/// <summary>
/// The Teams / Microsoft 365 Copilot front door.
/// <para>
/// The inbound turn must finish well inside the Bot Service 10–15 second budget, and the slow
/// tools take far longer than that, so this turn only acknowledges and hands the work to a
/// durable orchestration. The answer arrives later as a proactive message.
/// </para>
/// <para>
/// This host does not route and does not call any downstream resource. It owns identity,
/// acknowledgement, and delivery, and forwards the user's assertion to the Foundry hosted agent
/// which does the rest. That split is what lets the same finance agent serve callers who never
/// come through Teams at all.
/// </para>
/// </summary>
public sealed class OrchestratorChannel : AgentApplication
{
    private readonly ICallerIdentityResolver _identityResolver;
    private readonly ISessionKeyProvider _sessionKeyProvider;
    private readonly IOrchestratorTurnScheduler _turnScheduler;
    private readonly ChannelTurnProcessor _turnProcessor;
    private readonly ChannelSessionStore _sessionStore;
    private readonly ChannelOptions _options;
    private readonly ILogger<OrchestratorChannel> _logger;

    public OrchestratorChannel(
        AgentApplicationOptions applicationOptions,
        ICallerIdentityResolver identityResolver,
        ISessionKeyProvider sessionKeyProvider,
        IOrchestratorTurnScheduler turnScheduler,
        ChannelTurnProcessor turnProcessor,
        ChannelSessionStore sessionStore,
        ChannelOptions options,
        ILogger<OrchestratorChannel> logger)
        : base(applicationOptions)
    {
        _identityResolver = identityResolver;
        _sessionKeyProvider = sessionKeyProvider;
        _turnScheduler = turnScheduler;
        _turnProcessor = turnProcessor;
        _sessionStore = sessionStore;
        _options = options;
        _logger = logger;

        UserAuthorization.OnUserSignInFailure(OnSignInFailureAsync);
    }

    [MembersAddedRoute]
    public async Task WelcomeAsync(
        ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (ChannelAccount member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient?.Id)
            {
                await turnContext.SendActivityAsync(
                    "Hello. Ask me what a KPI means, ask for a figure for an organization and "
                    + "period, or ask why something moved.",
                    cancellationToken: cancellationToken);
            }
        }
    }

    [MessageRoute(autoSignInHandlers: "mcs")]
    public async Task OnMessageAsync(
        ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        string question = turnContext.Activity.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(question))
        {
            return;
        }

        CallerIdentity caller;

        try
        {
            caller = await _identityResolver.ResolveAsync(
                turnContext, UserAuthorization, cancellationToken);
        }
        catch (CallerIdentityException ex)
        {
            // Identity failures are security events, surfaced as alerts rather than
            // swallowed diagnostics.
            _logger.LogError(
                ex, "ALERT: rejected turn — caller identity could not be established.");

            await turnContext.SendActivityAsync(
                "I could not verify your identity for this conversation.",
                cancellationToken: cancellationToken);

            return;
        }

        string sessionKey = _sessionKeyProvider.GetSessionKey(
            caller, turnContext.Activity.Conversation.Id);

        // Answered inline: it is a store delete, nowhere near the channel timeout, and the
        // user should not be told "looking that up" for it.
        if (IsResetCommand(question))
        {
            await _sessionStore.ResetAsync(sessionKey, cancellationToken);

            _logger.LogInformation("Session reset by user request.");

            await turnContext.SendActivityAsync(
                "Cleared our conversation and started a fresh KPIpedia thread.",
                cancellationToken: cancellationToken);

            return;
        }

        // Deterministic for this inbound activity, so a retried delivery resolves the same
        // pending record instead of creating a second one.
        string turnId = TurnIdFor(turnContext.Activity.Id);

        // The question is written to the session store rather than carried in the
        // orchestration payload, because the Durable Task dashboard exposes payloads and is a
        // different access-control boundary. Access tokens and tool results never enter
        // orchestration state either.
        bool created = await _sessionStore.CreateTurnAsync(
            sessionKey, turnId, question, cancellationToken);

        TurnDeliveryRecord? turn = await _sessionStore.ReadTurnAsync(sessionKey, turnId, cancellationToken);
        if (turn?.Status == TurnDeliveryStatus.Delivered)
        {
            return;
        }

        // Stored so the durable activity can resume this conversation later.
        string conversationRecordId =
            await Proactive.StoreConversationAsync(turnContext, cancellationToken);

        // Ack first and await it, so it is ordered ahead of the proactive answer.
        if (created)
        {
            await turnContext.SendActivityAsync(
                _options.AcknowledgementText, cancellationToken: cancellationToken);
        }

        // Activity.ChannelId is a ChannelId value object, not a string.
        string channelId = turnContext.Activity.ChannelId?.ToString() ?? "unknown";

        string instanceId = await _turnScheduler.ScheduleAsync(
            sessionKey,
            turnId,
            conversationRecordId,
            channelId,
            cancellationToken);

        _logger.LogInformation("Scheduled Orchestrator orchestration {InstanceId}.", instanceId);
    }

    /// <summary>
    /// Recognised without the model, so a reset still works when routing or the subagent is
    /// the thing that is broken.
    /// </summary>
    private static bool IsResetCommand(string text)
    {
        string normalized = text.Trim().TrimStart('/').Trim();

        return normalized.Equals("reset", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("new chat", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("start over", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("clear", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hashed because the activity id is channel-supplied and store keys reject characters
    /// that appear in Teams identifiers. Falls back to a fresh id when the channel supplies
    /// none, so two distinct messages can never share a pending-turn record.
    /// </summary>
    private static string TurnIdFor(string? activityId)
    {
        string source = string.IsNullOrWhiteSpace(activityId)
            ? Guid.NewGuid().ToString("N")
            : activityId;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    /// <summary>
    /// Runs the slow work after the proactive SDK has acquired the named handler's token.
    /// </summary>
    public Task ContinueTurnAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        OrchestratorTurnRequest request = ReadRequest(turnContext);

        return _turnProcessor.ProcessAsync(request, turnContext,
            ct => turnContext.GetTurnTokenAsync(_options.UserAuthorizationHandler, cancellationToken: ct),
            cancellationToken);
    }

    /// <summary>
    /// The payload may arrive as the original object when the continuation stays in process,
    /// or as JSON once the activity has been through the channel serializer. Property-name
    /// casing is not guaranteed across that boundary, so matching is case-insensitive.
    /// </summary>
    private static readonly JsonSerializerOptions RequestJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static OrchestratorTurnRequest ReadRequest(ITurnContext turnContext)
    {
        object value = turnContext.Activity.Value
            ?? throw new InvalidOperationException(
                "Continuation activity carried no orchestrator request.");

        if (value is OrchestratorTurnRequest typed)
        {
            return typed;
        }

        string json = value as string ?? JsonSerializer.Serialize(value);

        return JsonSerializer.Deserialize<OrchestratorTurnRequest>(json, RequestJsonOptions)
            ?? throw new InvalidOperationException(
                "Continuation activity payload was not an orchestrator request.");
    }

    private async Task OnSignInFailureAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        string handlerName,
        SignInResponse response,
        IActivity initiatingActivity,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Sign-in failed for handler {Handler}: {Cause}", handlerName, response.Cause);

        await turnContext.SendActivityAsync(
            "I could not sign you in, so I cannot reach KPIpedia on your behalf.",
            cancellationToken: cancellationToken);
    }
}
