// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;

namespace ZavaFinance.Channel;

/// <summary>
/// Tells the user what is happening during a slow turn, then delivers the answer.
/// <para>
/// Progress and delivery are owned together on purpose. The two mechanisms below finish a turn in
/// different ways — one ends a stream, the other sends a message — and splitting them across two
/// classes is how a turn ends up showing a progress line that never resolves, or an answer that
/// arrives twice.
/// </para>
/// <para>
/// <b>Two mechanisms, chosen per channel.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Streaming informative updates</b> where the channel supports them. This is the mechanism
/// Teams and Microsoft 365 Copilot actually render as a live "working on it" status, and the
/// final answer replaces it in place.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Typing indicator plus timed nudges</b> everywhere else.
/// </description>
/// </item>
/// </list>
/// <para>
/// The distinction is not cosmetic. <b>Microsoft 365 Copilot does not render typing activities
/// at all</b> — they are accepted and silently dropped, which is indistinguishable from a broken
/// agent unless the send count is logged. Measured on a real turn: 21 typing activities sent, 0
/// failed, nothing visible. Informative updates are the supported signal on that surface.
/// </para>
/// <para>
/// The SDK's own <c>StartTypingTimer</c> cannot be used here either: it is documented to ignore
/// the call when "the current activity is not a message", and this runs inside a proactive
/// continuation whose inbound activity is an event. It also ends "once an outgoing activity has
/// been sent", which a nudge is.
/// </para>
/// <para>
/// No tool-specific text. Routing happens inside the hosted agent, so this host does not know
/// which tool is running until the answer arrives, and a confidently wrong progress message is
/// worse than a vague one.
/// </para>
/// </summary>
public sealed class TurnProgress : IAsyncDisposable
{
    /// <summary>
    /// Teams drops the typing indicator after a few seconds, so it is refreshed well inside that
    /// window. Only used on the non-streaming path.
    /// </summary>
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// What the user hears, and when. The first lands after the point where silence starts to
    /// read as failure; the second sets expectations for the genuinely long calls, which have
    /// been measured at over three minutes.
    /// </summary>
    private static readonly (TimeSpan After, string Text)[] Updates =
    [
        // Distinct from the channel's acknowledgement, which has already been sent as a normal
        // message in the inbound turn. Repeating it verbatim would read as a stutter.
        (TimeSpan.Zero,
            "Checking the finance sources…"),
        (TimeSpan.FromSeconds(15),
            "Still working — this can take a minute."),
        (TimeSpan.FromSeconds(90),
            "Still going. Complex questions can take a few minutes to come back.")
    ];

    private readonly ITurnContext _turnContext;
    private readonly ILogger _logger;
    private readonly bool _streaming;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private int _typingSent;
    private int _typingFailed;
    private int _updatesSent;
    private bool _completed;
    private bool _deliveredInStream;

    /// <summary>
    /// One entry per activity this turn sent, recording whether the channel created a message
    /// resource for it. See <see cref="TryCompleteStreamAsync"/> for why that matters.
    /// </summary>
    private readonly List<SentActivity> _sent = [];

    private readonly Lock _sentGate = new();

    private TurnProgress(ITurnContext turnContext, ILogger logger, TimeProvider timeProvider)
    {
        _turnContext = turnContext;
        _logger = logger;

        // Channels that do not support intermediate messages buffer the text and send one normal
        // message when the stream ends, so this path stays correct either way. It is read once,
        // because the answer must be delivered the same way the progress was started.
        _streaming = SupportsStreaming(turnContext, logger);

        RecordSendResults(turnContext);

        _loop = RunAsync(timeProvider, _stop.Token);
    }

    /// <summary>
    /// Observes the <see cref="ResourceResponse"/> the channel returns for every activity sent
    /// during this turn.
    /// <para>
    /// This is the only place the truth is available. The streaming API reports that it ended the
    /// stream, not whether the channel kept anything, and it swallows the response of its own
    /// final send — so without this hook a discarded answer is indistinguishable from a delivered
    /// one.
    /// </para>
    /// </summary>
    private void RecordSendResults(ITurnContext turnContext)
    {
        turnContext.OnSendActivities(async (context, activities, next) =>
        {
            ResourceResponse[] responses = await next();

            lock (_sentGate)
            {
                for (int i = 0; i < activities.Count; i++)
                {
                    // A created resource comes back with its id. An activity the channel accepted
                    // and dropped comes back without one.
                    bool created =
                        i < responses.Length
                        && !string.IsNullOrEmpty(responses[i]?.Id);

                    _sent.Add(new SentActivity(activities[i].Type?.ToString(), created));
                }
            }

            return responses;
        });
    }

    private readonly record struct SentActivity(string? Type, bool Created);

    public static TurnProgress Start(
        ITurnContext turnContext, ILogger logger, TimeProvider timeProvider)
        => new(turnContext, logger, timeProvider);

    private static bool SupportsStreaming(ITurnContext turnContext, ILogger logger)
    {
        try
        {
            return turnContext.StreamingResponse?.IsStreamingChannel == true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Streaming support could not be determined; using messages.");

            return false;
        }
    }

    /// <summary>
    /// Delivers the answer and stops the progress.
    /// <para>
    /// On the streaming path the answer becomes the stream's final message, so it replaces the
    /// progress line rather than appearing beneath it. <b>The stream is then verified, and the
    /// answer is re-sent as an ordinary message if the channel did not actually render it.</b>
    /// </para>
    /// <para>
    /// That verification is not defensive padding; it is the fix for a silent, total loss of the
    /// answer on Microsoft 365 Copilot. That surface accepts a streamed activity with
    /// <c>202 Accepted</c> and <b>creates nothing</b> — no resource id comes back — so the frame
    /// is discarded. Ordinary message activities are created normally (<c>201</c>), which is why
    /// the acknowledgement always arrives and only the streamed answer disappears. Nothing throws,
    /// and the turn reports success.
    /// </para>
    /// <para>
    /// The check is on the <b>final message specifically</b>, read from the resource response the
    /// channel returned for it. An earlier version asked whether the stream had an id at all,
    /// which was measured to be wrong: Copilot created a resource for the first informative update
    /// (<c>201</c>) and then discarded both the second update and the answer (<c>202</c>), so the
    /// stream had an id while the answer was still lost.
    /// </para>
    /// </summary>
    public async Task CompleteAsync(string answer, CancellationToken cancellationToken)
    {
        // Stop progress first, so nothing can be queued after the stream is ended — which the
        // SDK treats as an error rather than a no-op.
        await StopAsync();

        _completed = true;

        if (_streaming && await TryCompleteStreamAsync(answer, cancellationToken))
        {
            _deliveredInStream = true;

            return;
        }

        // The one delivery this codebase has evidence for on every surface: an ordinary message
        // activity, which the channel answers with a created resource.
        await _turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
    }

    /// <summary>
    /// Ends the stream with the answer as its final message and reports whether the channel
    /// actually rendered it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the answer has been delivered and must not be sent again;
    /// <see langword="false"/> when nothing reached the user and the caller must fall back.
    /// </returns>
    private async Task<bool> TryCompleteStreamAsync(string answer, CancellationToken cancellationToken)
    {
        IStreamingResponse stream = _turnContext.StreamingResponse;

        try
        {
            // Read before ending. The SDK switches this off mid-turn when the channel rejects
            // streaming, and it then delivers the final message as an ordinary message itself —
            // so falling back in that case would post the answer twice.
            bool streamingActive = stream.IsStreamingChannel;

            if (!streamingActive)
            {
                stream.FinalMessage = MessageFactory.Text(answer);

                await stream.EndStreamAsync(cancellationToken);

                return true;
            }

            if (!stream.IsStreamStarted())
            {
                // The answer beat the first update, so there is no stream to finish. Ending it
                // with a final message would return NotStarted without sending anything, so the
                // message is deliberately not attached and the caller delivers it normally.
                await stream.EndStreamAsync(cancellationToken);

                return false;
            }

            stream.FinalMessage = MessageFactory.Text(answer);

            // Everything the stream sends from here is attributable, so the channel's verdict on
            // the final message can be read back afterwards.
            int mark;

            lock (_sentGate)
            {
                mark = _sent.Count;
            }

            StreamingResponseResult result = await stream.EndStreamAsync(cancellationToken);

            switch (result)
            {
                case StreamingResponseResult.UserCancelled:
                    // The user stopped the response. Pushing the answer at them anyway would
                    // override a deliberate choice.
                    return true;

                case StreamingResponseResult.Success:
                case StreamingResponseResult.Timeout:
                    // The final message was handed to the channel. Whether it became a message
                    // is decided below.
                    break;

                default:
                    // AlreadyEnded and Error both return before the final message is sent.
                    _logger.LogWarning(
                        "Stream ended as {Result} without sending the answer; "
                        + "delivering it as a message.",
                        result);

                    return false;
            }

            if (!FinalMessageWasCreated(mark))
            {
                // Accepted and discarded. See the remarks on CompleteAsync.
                _logger.LogWarning(
                    "The channel created no message for the streamed answer, so it was not "
                    + "rendered; delivering it as an ordinary message.");

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Never let the presentation layer lose an answer the user has already waited for.
            _logger.LogWarning(
                ex, "Could not end the response stream; sending the answer as a message.");

            return false;
        }
    }

    /// <summary>
    /// Whether the channel created a message resource for the answer the stream just sent.
    /// </summary>
    /// <param name="mark">Count of recorded sends taken immediately before the stream was ended.</param>
    private bool FinalMessageWasCreated(int mark)
    {
        lock (_sentGate)
        {
            for (int i = mark; i < _sent.Count; i++)
            {
                // The stream's intermediate frames are typing-class activities; the final answer
                // is the message. Only the message's fate decides whether the user saw anything.
                if (string.Equals(_sent[i].Type, ActivityTypes.Message, StringComparison.OrdinalIgnoreCase))
                {
                    return _sent[i].Created;
                }
            }
        }

        // The stream ended without sending a message at all.
        return false;
    }

    private async Task RunAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        long started = timeProvider.GetTimestamp();
        int next = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(started);

                if (next < Updates.Length && elapsed >= Updates[next].After)
                {
                    await SendUpdateAsync(Updates[next].Text, cancellationToken);
                    next++;
                }

                if (!_streaming)
                {
                    await SendTypingAsync(cancellationToken);
                }

                await Task.Delay(TypingInterval, timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the answer arrived.
        }
        catch (Exception ex)
        {
            // Progress is decoration. It must never be the reason a turn fails.
            _logger.LogWarning(ex, "Turn progress reporting stopped early.");
        }
    }

    /// <summary>
    /// Sends one progress update, falling back to an ordinary message once the channel has been
    /// seen to discard streamed frames.
    /// <para>
    /// Microsoft 365 Copilot creates a resource for the <i>first</i> streamed frame and discards
    /// every later one, so on a long turn the "still working" nudges silently disappear — which is
    /// exactly when the user most needs them. Once a discarded frame has been observed, the
    /// remaining updates go out as ordinary messages, which that surface does render.
    /// </para>
    /// </summary>
    private async Task SendUpdateAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            if (_streaming && !StreamedFramesAreBeingDiscarded())
            {
                await _turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                    text, cancellationToken);
            }
            else
            {
                // Sent in order and awaited, because Teams renders by server timestamp and
                // parallel sends can arrive out of order.
                await _turnContext.SendActivityAsync(
                    MessageFactory.Text(text), cancellationToken);
            }

            _updatesSent++;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Progress update not delivered.");
        }
    }

    /// <summary>
    /// Whether the channel has already been observed accepting a streamed frame without creating
    /// anything for it.
    /// </summary>
    private bool StreamedFramesAreBeingDiscarded()
    {
        lock (_sentGate)
        {
            foreach (SentActivity sent in _sent)
            {
                // Intermediate frames are typing-class. A discarded one proves this surface will
                // not render the rest of the stream either.
                if (!sent.Created
                    && string.Equals(
                        sent.Type, ActivityTypes.Typing, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task SendTypingAsync(CancellationToken cancellationToken)
    {
        try
        {
            IActivity typing = Activity.CreateTypingActivity();

            // Addressed from the turn's own conversation reference rather than sent bare. This
            // runs inside a proactive continuation, and an activity with no ConversationAccount
            // has nowhere to be delivered.
            typing.ApplyConversationReference(
                _turnContext.Activity.GetConversationReference(), isIncoming: false);

            await _turnContext.SendActivityAsync(typing, cancellationToken);

            _typingSent++;
        }
        catch (Exception ex)
        {
            _typingFailed++;
            _logger.LogDebug(ex, "Typing indicator not delivered.");
        }
    }

    private async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync();
        }

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // Counts only, never content. Enough to tell "never sent" apart from "sent and the
        // channel did not render it", which is otherwise invisible from outside.
        // DeliveredInStream=False on a streaming channel means the stream was not rendered and
        // the answer went out as an ordinary message instead.
        _logger.LogInformation(
            "Turn progress finished. Streaming={Streaming} Updates={Updates} "
            + "TypingSent={TypingSent} TypingFailed={TypingFailed} Completed={Completed} "
            + "DeliveredInStream={DeliveredInStream}",
            _streaming,
            _updatesSent,
            _typingSent,
            _typingFailed,
            _completed,
            _deliveredInStream);

        _stop.Dispose();
    }
}
