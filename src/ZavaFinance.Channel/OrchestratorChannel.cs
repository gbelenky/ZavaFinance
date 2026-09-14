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
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
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
    private readonly IHostedAgentClient _hostedAgent;
    private readonly OrchestratorSessionStore _sessionStore;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrchestratorChannel> _logger;

    public OrchestratorChannel(
        AgentApplicationOptions applicationOptions,
        ICallerIdentityResolver identityResolver,
        ISessionKeyProvider sessionKeyProvider,
        IOrchestratorTurnScheduler turnScheduler,
        IHostedAgentClient hostedAgent,
        OrchestratorSessionStore sessionStore,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        ILogger<OrchestratorChannel> logger)
        : base(applicationOptions)
    {
        _identityResolver = identityResolver;
        _sessionKeyProvider = sessionKeyProvider;
        _turnScheduler = turnScheduler;
        _hostedAgent = hostedAgent;
        _sessionStore = sessionStore;
        _options = options;
        _timeProvider = timeProvider;
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
        await _sessionStore.SavePendingTurnAsync(
            sessionKey, turnId, question, cancellationToken);

        // Stored so the durable activity can resume this conversation later.
        string conversationRecordId =
            await Proactive.StoreConversationAsync(turnContext, cancellationToken);

        // Ack first and await it, so it is ordered ahead of the proactive answer.
        await turnContext.SendActivityAsync(
            _options.AcknowledgementText, cancellationToken: cancellationToken);

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
    /// Runs the slow work on the durable path. The <c>[ContinueConversation]</c> attribute
    /// makes the SDK acquire the named handler's token for this proactive turn, which is how
    /// a per-user OBO token is obtained outside the original inbound turn.
    /// </summary>
    public async Task ContinueTurnAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        OrchestratorTurnRequest request = ReadRequest(turnContext);

        PendingTurn? pendingTurn = await _sessionStore.ReadPendingTurnAsync(
            request.SessionKey, request.TurnId, cancellationToken);

        if (pendingTurn is null || string.IsNullOrWhiteSpace(pendingTurn.Question))
        {
            // The record is deleted once answered, so this is a replay of a completed turn.
            _logger.LogInformation(
                "No pending turn {TurnId}; the answer was already delivered.", request.TurnId);

            return;
        }

        string answer = pendingTurn.Answer ?? string.Empty;

        // Started only when there is actually a wait. A replayed turn already has its answer and
        // must not narrate a delay that is not happening.
        TurnProgress? progress = null;

        if (string.IsNullOrEmpty(answer))
        {
            progress = TurnProgress.Start(turnContext, _logger, _timeProvider);

            try
            {
                // Acquired late and used immediately. This is the raw Teams SSO token, which is
                // the assertion the hosted agent exchanges; it is forwarded and then dropped,
                // never stored and never written to an orchestration payload.
                string assertion = await turnContext.GetTurnTokenAsync(
                    _options.UserAuthorizationHandler, cancellationToken: cancellationToken);

                if (string.IsNullOrWhiteSpace(assertion))
                {
                    throw new InvalidOperationException(
                        "No user token was available for the continued turn.");
                }

                // The hosted agent's conversation is created once per user session and reused.
                // Without it each turn opens a new conversation, and the agent loses the latest
                // KPI and the Copilot Studio handle every time.
                OrchestratorSessionState state =
                    await _sessionStore.LoadAsync(request.SessionKey, cancellationToken);

                if (string.IsNullOrWhiteSpace(state.HostedAgentConversationId))
                {
                    state.HostedAgentConversationId =
                        await _hostedAgent.CreateConversationAsync(cancellationToken);

                    await _sessionStore.SaveAsync(request.SessionKey, state, cancellationToken);
                }

                answer = await _hostedAgent.AskAsync(
                    pendingTurn.Question,
                    assertion,
                    state.HostedAgentConversationId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // A front door with no fallback is a dead end in Teams.
                _logger.LogError(ex, "Hosted agent turn failed.");
                answer = "Something went wrong while answering that. Please try again.";
            }

            // Persist before delivery. If delivery fails and Durable Task retries the activity,
            // the slow downstream call is not repeated.
            await _sessionStore.SavePendingTurnAnswerAsync(
                request.SessionKey, request.TurnId, pendingTurn, answer, cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "Reusing the completed answer for retried turn {TurnId}.", request.TurnId);
        }

        if (progress is not null)
        {
            // Delivered through the progress, because on a streaming channel the answer has to
            // become the stream's final message. Sending it separately would leave a live status
            // line that never resolves.
            await using (progress)
            {
                await progress.CompleteAsync(answer, cancellationToken);
            }
        }
        else
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(answer), cancellationToken);
        }

        await _sessionStore.DeletePendingTurnAsync(
            request.SessionKey, request.TurnId, cancellationToken);
    }

    /// <summary>
    /// Tool-specific progress messages are <b>not available in this topology</b>, and that is a
    /// deliberate, recorded limitation rather than an oversight.
    /// <para>
    /// Routing happens inside the hosted agent, so this host learns which tool was selected only
    /// when the finished answer comes back — by which point a message naming it is pointless. The
    /// user therefore sees the tool-neutral acknowledgement for the whole wait, which can be a
    /// minute or more.
    /// </para>
    /// <para>
    /// Restoring it requires the hosted agent to stream, so the channel can observe an early
    /// event carrying the selected route and relay it. That is a real option — the Responses
    /// protocol supports SSE — but it trades this design's "responses are returned whole" rule
    /// for partial output, so it is not taken by default.
    /// </para>
    /// </summary>
    internal static string AcknowledgementFor(OrchestratorOptions options) =>
        options.AcknowledgementText;

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
