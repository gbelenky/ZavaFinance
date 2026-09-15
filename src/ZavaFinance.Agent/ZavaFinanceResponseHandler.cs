// Copyright (c) Microsoft Corporation.

using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.Agent;

/// <summary>
/// The Foundry hosted agent.
/// <para>
/// It answers open-ended finance questions <b>as the signed-in user</b>. The caller's assertion
/// arrives in a forwarded <c>x-client-user-token</c> header — Foundry forwards only
/// <c>x-client-*</c> headers to the container and terminates <c>Authorization</c> at the gateway,
/// so the user assertion and the hop credential cannot share one header.
/// </para>
/// <para>
/// The agent derives its own session key from the <b>validated</b> claims of that assertion. It
/// never accepts a caller-supplied session key or conversation id, so a caller cannot address
/// another user's state by asking for it.
/// </para>
/// <para>
/// The assertion is read from request context, never from the message, and never enters the
/// model's input, the routing history, the response, or telemetry.
/// </para>
/// </summary>
public sealed class ZavaFinanceResponseHandler : ResponseHandler
{
    private readonly OrchestratorAgent _orchestrator;
    private readonly AIAgent _routingAgent;
    private readonly UserAssertionValidator _validator;
    private readonly IConfidentialClientApplication _confidentialClient;
    private readonly SessionKeyProvider _sessionKeys;
    private readonly OboOptions _oboOptions;
    private readonly ILogger<ZavaFinanceResponseHandler> _logger;

    public ZavaFinanceResponseHandler(
        OrchestratorAgent orchestrator,
        AIAgent routingAgent,
        UserAssertionValidator validator,
        IConfidentialClientApplication confidentialClient,
        SessionKeyProvider sessionKeys,
        OboOptions oboOptions,
        ILogger<ZavaFinanceResponseHandler> logger)
    {
        _orchestrator = orchestrator;
        _routingAgent = routingAgent;
        _validator = validator;
        _confidentialClient = confidentialClient;
        _sessionKeys = sessionKeys;
        _oboOptions = oboOptions;
        _logger = logger;
    }

    public override IAsyncEnumerable<ResponseStreamEvent> CreateAsync(
        CreateResponse request,
        ResponseContext context,
        CancellationToken cancellationToken) =>
        new TextResponse(context, request, createText: ct => AnswerAsync(context, ct));

    private async Task<string> AnswerAsync(
        ResponseContext context, CancellationToken cancellationToken)
    {
        string question =
            await context.GetInputTextAsync(cancellationToken: cancellationToken)
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(question))
        {
            return "Ask me what a KPI means, for a figure, or why something moved.";
        }

        string? assertion =
            context.ClientHeaders.TryGetValue(_oboOptions.UserTokenHeader, out string? header)
                ? header
                : null;

        CallerIdentity caller;

        try
        {
            caller = await _validator.ValidateAsync(assertion, cancellationToken);
        }
        catch (CallerIdentityException ex)
        {
            // No identity means no turn. Every downstream call is permissioned, so continuing
            // without a user would either fail or, worse, answer as the application.
            _logger.LogWarning("Refusing turn: {Reason}", ex.Message);

            return "I could not confirm who you are, so I cannot look anything up on your "
                + "behalf. Please sign in and try again.";
        }

        // Derived from validated claims plus the conversation, inside the provider. There is no
        // overload that accepts a ready-made key, so no caller can supply one.
        string sessionKey = _sessionKeys.GetSessionKey(caller, ConversationOf(context));

        var tokenProvider = new OboTokenProvider(
            _confidentialClient,
            assertion!,
            _logger);

        try
        {
            return await _orchestrator.RunAsync(
                _routingAgent,
                tokenProvider,
                sessionKey,
                question,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Turn failed.");

            return "Something went wrong answering that. Please try again.";
        }
    }

    /// <summary>
    /// The conversation this response belongs to.
    /// <para>
    /// <c>ConversationChainId</c> is a deterministic, agent- and session-scoped correlation key
    /// supplied by the runtime, so it is stable across the turns of one conversation and is not
    /// caller-controlled. Combined with the validated tenant and user it gives the same isolation
    /// property the channel host gets from a Teams conversation id: two people in one shared
    /// conversation still receive different session keys.
    /// </para>
    /// </summary>
    private static string ConversationOf(ResponseContext context) =>
        string.IsNullOrWhiteSpace(context.ConversationChainId)
            ? context.ResponseId ?? "default"
            : context.ConversationChainId;
}
