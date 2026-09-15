using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>Computes once an answer is needed, caches before delivery, then retains a tombstone.</summary>
public sealed class ChannelTurnProcessor(
    ChannelSessionStore store, IHostedAgentClient hostedAgent, TimeProvider timeProvider,
    ILogger<ChannelTurnProcessor> logger)
{
    public async Task ProcessAsync(
        OrchestratorTurnRequest request,
        ITurnContext turnContext,
        Func<CancellationToken, Task<string>> getAssertion,
        CancellationToken ct)
    {
        TurnDeliveryRecord turn = await store.ReadTurnAsync(request.SessionKey, request.TurnId, ct)
            ?? throw new InvalidDataException("Channel delivery record is missing.");
        if (turn.Status == TurnDeliveryStatus.Delivered)
        {
            return;
        }

        if (turn.Status == TurnDeliveryStatus.Pending)
        {
            // Scope progress to computation and caching. Disposal joins any in-flight update
            // before either the first answer or a cached-answer retry uses the final send below.
            await using var progress = TurnProgress.Start(turnContext, logger, timeProvider, ct);
            string answer;
            try
            {
                string assertion = await getAssertion(ct);
                if (string.IsNullOrWhiteSpace(assertion))
                {
                    throw new InvalidOperationException("No user token was available for the continued turn.");
                }

                ChannelSessionState session = await store.LoadAsync(request.SessionKey, ct);
                if (string.IsNullOrWhiteSpace(session.HostedAgentConversationId))
                {
                    session.HostedAgentConversationId = await hostedAgent.CreateConversationAsync(ct);
                    if (string.IsNullOrWhiteSpace(session.HostedAgentConversationId))
                    {
                        throw new InvalidOperationException("The hosted agent returned no conversation ID.");
                    }

                    try
                    {
                        await store.SaveAsync(request.SessionKey, session, ct);
                    }
                    catch (Microsoft.Agents.Storage.EtagException)
                    {
                        // Another turn created the same session. Use the winning platform
                        // conversation instead of forking the user's conversation history.
                        session = await store.LoadAsync(request.SessionKey, ct);
                        if (string.IsNullOrWhiteSpace(session.HostedAgentConversationId))
                        {
                            throw;
                        }
                    }
                }

                answer = await hostedAgent.AskAsync(turn.Question!, assertion, session.HostedAgentConversationId, ct);
                if (string.IsNullOrWhiteSpace(answer))
                {
                    throw new InvalidDataException("The hosted agent returned no answer.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Hosted agent turn timed out.");
                answer = "That took too long to answer. Please try again.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hosted agent turn failed.");
                answer = "Something went wrong while answering that. Please try again.";
            }

            await store.SaveAnswerAsync(request.SessionKey, request.TurnId, turn, answer, ct);
            turn = await store.ReadTurnAsync(request.SessionKey, request.TurnId, ct)
                ?? throw new InvalidDataException("Cached channel answer is missing.");
        }

        ct.ThrowIfCancellationRequested();
        if (turn.Status == TurnDeliveryStatus.Delivered)
        {
            return;
        }

        ResourceResponse response = await turnContext.SendActivityAsync(MessageFactory.Text(turn.Answer!), ct);
        if (string.IsNullOrWhiteSpace(response?.Id))
        {
            throw new InvalidOperationException("The channel did not confirm final message creation.");
        }

        await store.MarkDeliveredAsync(request.SessionKey, request.TurnId, turn, ct);
        logger.LogInformation("Turn progress finished. AnswerLength={AnswerLength}", turn.Answer!.Length);
    }
}
