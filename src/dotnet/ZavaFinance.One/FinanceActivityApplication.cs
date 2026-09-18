using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore.BackgroundQueue;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using ZavaFinance.Agent;
using ZavaFinance.ActivitySupport;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.One;

/// <summary>Native Activity boundary; finance routing and session behavior belong to the existing orchestrator.</summary>
public sealed class FinanceActivityApplication : AgentApplication
{
    private const string IdentityFailure =
        "I could not confirm who you are, so I cannot look anything up on your behalf. Please sign in and try again.";
    private const string InvalidChoice =
        "I could not read that choice. Please use the latest card, or type your question again.";
    private readonly IActivityAssertionValidator _validator;
    private readonly IFinanceActivityRunner _finance;
    private readonly IConfidentialClientApplication _confidentialClient;
    private readonly SessionKeyProvider _sessionKeys;
    private readonly ActivityOptions _options;
    private readonly IActivityTaskQueue _activityQueue;
    private readonly ILogger<FinanceActivityApplication> _logger;

    public FinanceActivityApplication(
        AgentApplicationOptions applicationOptions,
        IActivityAssertionValidator validator,
        IFinanceActivityRunner finance,
        IConfidentialClientApplication confidentialClient,
        SessionKeyProvider sessionKeys,
        ActivityOptions options,
        IActivityTaskQueue activityQueue,
        ILogger<FinanceActivityApplication> logger)
        : base(applicationOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserAuthorizationHandler);
        _validator = validator;
        _finance = finance;
        _confidentialClient = confidentialClient;
        _sessionKeys = sessionKeys;
        _options = options;
        _activityQueue = activityQueue;
        _logger = logger;

        UserAuthorization.OnUserSignInFailure(OnSignInFailureAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync,
            autoSignInHandlers: [options.UserAuthorizationHandler]);
        AddRoute((context, _) => Task.FromResult(ClarificationCard.IsSupportedInvoke(context.Activity)),
            OnClarificationInvokeAsync, isInvokeRoute: true,
            autoSignInHandlers: [options.UserAuthorizationHandler]);
    }

    public Task OnMessageAsync(
        ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
        => HandleTurnAsync(turnContext, enqueueInvoke: false, cancellationToken);

    public async Task OnClarificationInvokeAsync(
        ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        int status = await HandleTurnAsync(turnContext, enqueueInvoke: true, cancellationToken);
        await SendInvokeResponseAsync(turnContext, status, cancellationToken);
    }

    private static async Task SendInvokeResponseAsync(
        ITurnContext turnContext, int status, CancellationToken cancellationToken)
    {
        object body = turnContext.Activity.Name == "adaptiveCard/action"
            ? new AdaptiveCardInvokeResponse
            {
                StatusCode = status,
                Type = status == 200 ? ContentTypes.Message : ContentTypes.Error,
                Value = status == 200 ? "Request received." : new
                {
                    code = status switch { 401 => "Unauthorized", 503 => "ServiceUnavailable", _ => "BadRequest" },
                    message = "The choice could not be accepted. Please ask again."
                }
            }
            : new { task = (object?)null };
        await turnContext.SendActivityAsync(new Activity
        {
            Type = ActivityTypes.InvokeResponse,
            Value = new InvokeResponse { Status = turnContext.Activity.Name == "adaptiveCard/action" ? 200 : status, Body = body }
        }, cancellationToken);
    }

    private async Task<int> HandleTurnAsync(
        ITurnContext context, bool enqueueInvoke, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? assertion;
        CallerIdentity caller;
        try
        {
            assertion = await UserAuthorization.GetTurnTokenAsync(
                context, _options.UserAuthorizationHandler, cancellationToken);
            if (string.IsNullOrWhiteSpace(assertion))
                throw new CallerIdentityException("No delegated user assertion is available.");
            caller = await _validator.ValidateAsync(assertion, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CallerIdentityResolver.AssertPayloadAgrees(context, caller);
        }
        catch (CallerIdentityException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Rejected Activity turn: caller identity could not be verified.");
            return await RejectTurnAsync(context, enqueueInvoke, 401, IdentityFailure, cancellationToken);
        }
        catch (MsalException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Rejected Activity turn: delegated sign-in failed.");
            return await RejectTurnAsync(context, enqueueInvoke, 401, IdentityFailure, cancellationToken);
        }

        string? conversationId = context.Activity.Conversation?.Id;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            _logger.LogWarning("Rejected Activity turn: conversation ID is missing.");
            return await RejectTurnAsync(context, enqueueInvoke, 400,
                "This conversation has no identifier. Please start a new conversation.", cancellationToken);
        }

        ClarificationSubmission? submission;
        try
        {
            submission = ClarificationCard.ReadSubmission(context.Activity);
            if (enqueueInvoke && (!ClarificationCard.IsSupportedInvoke(context.Activity) || submission is null))
                throw new InvalidDataException("Missing or unsupported clarification selection.");
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Rejected malformed clarification submission.");
            return await RejectTurnAsync(context, enqueueInvoke, 400, InvalidChoice, cancellationToken);
        }

        if (enqueueInvoke)
        {
            // CloudAdapter waits for invokes. Re-dispatch as a message on M365's own queue,
            // with a fresh turn/token acquisition, never a captured turn context or assertion.
            IActivity message = context.Activity.Clone();
            message.Type = ActivityTypes.Message;
            // The acknowledged invoke's HTTP stream cannot carry this later connector reply.
            message.DeliveryMode = DeliveryModes.Normal;
            message.Name = string.Empty;
            message.Text = ClarificationCard.SubmissionQuestion;
            message.Value = JsonSerializer.SerializeToElement(new
            {
                schema = FinanceReplyProtocol.Schema,
                action = ClarificationCard.SubmitAction,
                requestId = submission!.RequestId,
                optionId = submission.OptionId,
                catalogVersion = submission.CatalogVersion
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!_activityQueue.QueueBackgroundActivity(
                context.Identity, context.Adapter, message, agentType: typeof(FinanceActivityApplication)))
            {
                _logger.LogWarning("Clarification rejected: M365 Activity queue is stopping.");
                return await RejectTurnAsync(context, enqueueInvoke, 503,
                    "I could not accept that choice right now. Please try again.", cancellationToken);
            }
            return 200;
        }

        string question = submission is null
            ? context.Activity.Text?.Trim() ?? string.Empty : ClarificationCard.SubmissionQuestion;
        if (string.IsNullOrEmpty(question))
        {
            await SendReplyAsync(context, new FinanceReply(
                "Ask me what a KPI means, for a figure, or why something moved."), cancellationToken);
            return 200;
        }

        string sessionKey = _sessionKeys.GetSessionKey(caller, conversationId);
        var tokens = new OboTokenProvider(_confidentialClient, assertion!, _logger);
        FinanceReply reply;
        try
        {
            reply = await _finance.RunReplyAsync(tokens, sessionKey, question, submission, cancellationToken);
        }
        catch (MsalException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Finance turn failed: delegated token exchange was refused.");
            reply = new FinanceReply("I could not obtain permission to answer on your behalf. Please sign in and try again.");
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Finance turn failed: downstream service request failed.");
            reply = new FinanceReply("A finance service could not be reached. Please try again.");
        }
        await SendReplyAsync(context, reply, cancellationToken);
        return 200;
    }

    private async Task<int> RejectTurnAsync(
        ITurnContext context, bool invoke, int status, string message, CancellationToken cancellationToken)
    {
        // Invoke errors belong to the HTTP protocol response, not an unacknowledged buffered chat reply.
        if (!invoke) await SendReplyAsync(context, new FinanceReply(message), cancellationToken);
        return status;
    }

    private async Task SendReplyAsync(
        ITurnContext context, FinanceReply reply, CancellationToken cancellationToken)
    {
        FinanceReplyProtocol.Validate(reply);
        string? card = reply.Clarification is { } prompt ? ClarificationCard.CreateJson(prompt) : null;
        IActivity message = ClarificationCard.CreateMessage(reply.Text, card);
        ResourceResponse response = await context.SendActivityAsync(message, cancellationToken);
        if (string.IsNullOrWhiteSpace(response?.Id))
        {
            _logger.LogError("Activity reply delivery returned no acknowledgement ID.");
            throw new InvalidOperationException("Activity reply delivery returned no acknowledgement ID.");
        }
    }

    private async Task OnSignInFailureAsync(
        ITurnContext context, ITurnState state, string handlerName, SignInResponse response,
        IActivity initiatingActivity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning("Sign-in failed for handler {Handler}.", handlerName);
        if (context.Activity.Type == ActivityTypes.Invoke)
        {
            if (ClarificationCard.IsSupportedInvoke(context.Activity))
                await SendInvokeResponseAsync(context, 401, cancellationToken);
            // Other authentication invokes retain the SDK's own response contract.
            return;
        }
        await SendReplyAsync(context, new FinanceReply(IdentityFailure), cancellationToken);
    }
}
